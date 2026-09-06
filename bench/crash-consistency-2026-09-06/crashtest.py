#!/usr/bin/env python3
"""Crash consistency: what does each engine do to a step's SIDE EFFECT when the
engine is SIGKILLed mid-step and then restarted?

The pipeline appends a marker line per stage. Stage 2 appends its marker and
then sleeps, so the kill always lands after the effect has happened but before
the step is recorded complete -- the exact window where at-least-once,
at-most-once and exactly-once diverge.

Marker counts after recovery:
  s2 == 1  the effect happened once across the crash
  s2 == 2  the step re-executed -- AT-LEAST-ONCE (duplicate side effect)
  s2 == 0  impossible here (marker is written before the sleep)
Also recorded: the engine's OWN terminal verdict, so we can see whether its
record agrees with what physically happened.
"""
import http.cookiejar, json, os, pathlib, re, shutil, subprocess, time
import urllib.error, urllib.parse, urllib.request, uuid

HOME = pathlib.Path.home()
LAB = HOME / "crashlab"
DLL = HOME / "faceoff2" / "fogell" / "net10.0" / "Fogell.Run.Host.dll"
JURL = "http://127.0.0.1:18086"
MCENV = HOME / "faceoff2" / "mcrun" / "env"
SLEEP_IN_STEP = 45      # stage 2 sleeps this long after writing its marker
KILL_AFTER = 15         # kill the engine this long into the run

jarj = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jarj))
def crumb():
    with opener.open(f"{JURL}/crumbIssuer/api/json", timeout=20) as r:
        return json.load(r)["crumb"]
def jpost(p, data=None, ctype=None):
    q = urllib.request.Request(f"{JURL}{p}", data=data, method="POST")
    q.add_header("Jenkins-Crumb", crumb())
    if ctype: q.add_header("Content-Type", ctype)
    try:
        with opener.open(q, timeout=120) as r: return r.status
    except urllib.error.HTTPError as e: return e.code

def cmds(marker):
    return [("s1", f"echo s1 >> {marker}"),
            ("s2", f"echo s2 >> {marker}; sleep {SLEEP_IN_STEP}"),
            ("s3", f"echo s3 >> {marker}")]

def write_pipelines(tag, marker):
    jf = ["pipeline {", "  agent any", "  stages {"]
    for s, c in cmds(marker):
        jf += [f"    stage('{s}') {{", "      steps {", f'        sh "{c}"', "      }", "    }"]
    jf += ["  }", "}", ""]
    p1 = LAB / f"{tag}.Jenkinsfile"; p1.write_text("\n".join(jf))
    yl = ["version: 1", f'name: "crash-{tag}"', "stages:"]
    for i, (s, c) in enumerate(cmds(marker)):
        yl += [f'  - id: "{s}"', f'    name: "{s}"', "    steps:", "      - process:",
               '          program: "/bin/sh"', f'          args: ["-c", "{c}"]']
    yl.append("")
    p2 = LAB / f"{tag}.yaml"; p2.write_text("\n".join(yl))
    return p1, p2

def counts(marker):
    if not marker.exists(): return {}
    out = {}
    for line in marker.read_text().split():
        out[line] = out.get(line, 0) + 1
    return out

def sh(c, t=120):
    return subprocess.run(c, shell=True, capture_output=True, text=True, timeout=t)

# ---------------- Fogell: kill Run.Host, re-run with the SAME journal --------
def fogell(tag, marker):
    jf, _ = write_pipelines(tag, marker)
    ws, jr = LAB / f"ws-{tag}", LAB / f"j-{tag}.log"
    shutil.rmtree(ws, ignore_errors=True); jr.unlink(missing_ok=True)
    ws.mkdir(parents=True, exist_ok=True)
    p = subprocess.Popen(["dotnet", str(DLL), str(jf), str(ws), tag, str(jr)],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(KILL_AFTER)
    sh(f"pkill -9 -f 'Fogell.Run.Host.dll {jf}'")
    p.kill(); p.wait(timeout=60)
    before = counts(marker)
    time.sleep(2)
    r = subprocess.run(["dotnet", str(DLL), str(jf), str(ws), tag, str(jr)],
                       capture_output=True, text=True, timeout=600)
    verdict = "success" if r.returncode == 0 else f"exit {r.returncode}"
    if "already-terminal" in (r.stdout or ""): verdict += " (already-terminal)"
    jtail = [l for l in (jr.read_text().splitlines() if jr.exists() else []) if l.startswith(("step-","stage-","build-"))]
    return before, counts(marker), verdict, jtail

# ---------------- McLoving: SIGKILL the agent, restart it -------------------
def mcenv():
    e = dict(os.environ)
    for line in MCENV.read_text().splitlines():
        m = re.match(r"export (\w+)=(.*)", line.strip())
        if m: e[m.group(1)] = m.group(2)
    return e

def mcloving(tag, marker):
    _, yml = write_pipelines(tag, marker)
    env = mcenv(); cli = str(HOME / "faceoff2" / "bin" / "mcloving-cli")
    base = [cli, "--server", env["MCLOVING_URL"], "--token", env["MCLOVING_API_TOKEN"],
            "--organization", env["MCLOVING_ORGANIZATION_ID"]]
    pid = str(uuid.uuid4())
    if subprocess.run(base + ["apply", pid, "--slug", tag, "--expected-revision", "0", str(yml)],
                      capture_output=True, text=True, timeout=300, env=env).returncode != 0:
        return {}, {}, "apply failed", []
    r = subprocess.run(base + ["submit", pid, "--idempotency-key", f"k-{tag}"],
                       capture_output=True, text=True, timeout=300, env=env)
    m = re.search(r"^build_id: (\S+)", r.stdout or "", re.M)
    if not m: return {}, {}, "submit failed", []
    build = m.group(1)
    time.sleep(KILL_AFTER)
    sh("pkill -9 -f 'faceoff2/bin/mcloving-agent'")
    before = counts(marker)
    time.sleep(3)
    sh("bash ~/faceoff2/mc-agent-up.sh", t=120)
    last, end = "?", time.monotonic() + 420
    while time.monotonic() < end:
        q = subprocess.run(base + ["status", build], capture_output=True, text=True, timeout=120, env=env)
        sm = re.search(r"^status: (\S+)", q.stdout or "", re.M)
        last = sm.group(1) if sm else last
        if last in ("succeeded", "failed", "cancelled", "aborted"): break
        time.sleep(2)
    return before, counts(marker), last, []

# ---------------- Jenkins: SIGKILL the container, restart it ----------------
def jenkins(tag, marker):
    jf, _ = write_pipelines(tag, marker)
    job = f"crash-{tag}"
    esc = jf.read_text().replace("&","&amp;").replace("<","&lt;").replace(">","&gt;")
    cfg = ("<?xml version='1.1' encoding='UTF-8'?><flow-definition plugin=\"workflow-job\">"
           "<keepDependencies>false</keepDependencies><properties/>"
           "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
           f"<script>{esc}</script><sandbox>false</sandbox></definition><triggers/>"
           "<disabled>false</disabled></flow-definition>")
    jpost(f"/job/{job}/doDelete")
    if jpost(f"/createItem?name={urllib.parse.quote(job)}", cfg.encode(), "application/xml") not in (200,201):
        return {}, {}, "createItem failed", []
    with opener.open(f"{JURL}/job/{job}/api/json?tree=nextBuildNumber", timeout=20) as r:
        n = json.load(r)["nextBuildNumber"]
    jpost(f"/job/{job}/build")
    time.sleep(KILL_AFTER)
    sh("podman kill jenkins-faceoff", t=120)
    before = counts(marker)
    time.sleep(3)
    sh("podman start jenkins-faceoff", t=180)
    end = time.monotonic() + 420
    while time.monotonic() < end:
        try:
            with opener.open(f"{JURL}/job/{job}/{n}/api/json?tree=result", timeout=15) as r:
                b = json.load(r)
            if b.get("result"): return before, counts(marker), b["result"], []
        except Exception: pass
        time.sleep(3)
    return before, counts(marker), "no terminal verdict in 420s", []

LAB.mkdir(parents=True, exist_ok=True)
print(f"kill at t+{KILL_AFTER}s, stage-2 step sleeps {SLEEP_IN_STEP}s after writing its marker\n")
for name, fn in (("fogell", fogell), ("mcloving", mcloving), ("jenkins", jenkins)):
    tag = f"{name}-{int(time.time())}"
    marker = LAB / f"marker-{tag}.txt"
    try:
        before, after, verdict, journal = fn(tag, marker)
    except Exception as exc:
        print(f"=== {name}: HARNESS ERROR {type(exc).__name__}: {exc}\n"); continue
    s2 = after.get("s2", 0)
    if s2 == 1: call = "one effect across the crash"
    elif s2 >= 2: call = f"DUPLICATE SIDE EFFECT (s2 ran {s2}x) -- at-least-once"
    else: call = "effect never happened"
    print(f"=== {name} ===")
    print(f"  markers at kill : {before}")
    print(f"  markers at end  : {after}")
    print(f"  engine verdict  : {verdict}")
    print(f"  -> {call}")
    if journal: print("  journal:", " | ".join(journal[-6:]))
    print()
