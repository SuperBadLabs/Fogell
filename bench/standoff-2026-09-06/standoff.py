#!/usr/bin/env python3
"""Three-engine standoff over six real OSS projects, on mario.

Fogell / Jenkins / McLoving each build the SAME bounded target in each project,
from byte-identical shell commands -- a Jenkinsfile for Fogell and Jenkins, the
same commands in McLoving's YAML schema for McLoving.

Each project builds under a JDK that satisfies its OWN declared requirement and
the maven-enforcer is NOT skipped, so a project that refuses this environment
says so instead of being gagged into a false pass.

Engine order rotates per (round, project) so position effects spread. mario
drifts within a session -- only within-run comparison is meaningful.
"""
import http.cookiejar, json, os, pathlib, re, shutil, subprocess, time, urllib.error, urllib.parse, urllib.request, uuid

HOME = pathlib.Path.home()
PIPE = HOME / "standoff" / "pipelines"
DLL = HOME / "faceoff2" / "fogell" / "net10.0" / "Fogell.Run.Host.dll"
WS = HOME / "standoff" / "ws"
JURL = "http://127.0.0.1:18086"
MCENV = HOME / "faceoff2" / "mcrun" / "env"
ROUNDS = 2
PROJECTS = ["jenkinsci_git-plugin", "apache_activemq", "apache_cxf",
            "apache_dubbo", "apache_maven", "apache_camel"]
ENGINES = ["fogell", "jenkins", "mcloving"]

jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

def crumb():
    with opener.open(f"{JURL}/crumbIssuer/api/json", timeout=15) as r:
        return json.load(r)["crumb"]

def jpost(path, data=None, ctype=None):
    req = urllib.request.Request(f"{JURL}{path}", data=data, method="POST")
    req.add_header("Jenkins-Crumb", crumb())
    if ctype: req.add_header("Content-Type", ctype)
    try:
        with opener.open(req, timeout=180) as r: return r.status
    except urllib.error.HTTPError as e: return e.code

def run_jenkins(project, tag):
    job = f"so-{tag}"
    script = (PIPE / f"{project}.Jenkinsfile").read_text()
    esc = script.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    cfg = ("<?xml version='1.1' encoding='UTF-8'?>\n<flow-definition plugin=\"workflow-job\">"
           "<keepDependencies>false</keepDependencies><properties/>"
           "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
           f"<script>{esc}</script><sandbox>false</sandbox></definition>"
           "<triggers/><disabled>false</disabled></flow-definition>")
    jpost(f"/job/{job}/doDelete")
    if jpost(f"/createItem?name={urllib.parse.quote(job)}", cfg.encode(), "application/xml") not in (200, 201):
        return None, "createItem failed"
    with opener.open(f"{JURL}/job/{job}/api/json?tree=nextBuildNumber", timeout=15) as r:
        n = json.load(r)["nextBuildNumber"]
    start = time.monotonic()
    if jpost(f"/job/{job}/build") not in (200, 201):
        return None, "trigger failed"
    end = start + 3600
    while time.monotonic() < end:
        try:
            with opener.open(f"{JURL}/job/{job}/{n}/api/json?tree=result", timeout=15) as r:
                b = json.load(r)
            if b.get("result"):
                if b["result"] != "SUCCESS": return None, f"jenkins {b['result']}"
                return int((time.monotonic() - start) * 1000), None
        except urllib.error.HTTPError: pass
        time.sleep(1.0)
    return None, "stalled"

def run_fogell(project, tag):
    ws, jr = WS / f"w-{tag}", WS / f"j-{tag}.log"
    shutil.rmtree(ws, ignore_errors=True); jr.unlink(missing_ok=True)
    ws.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    p = subprocess.run(["dotnet", str(DLL), str(PIPE / f"{project}.Jenkinsfile"),
                        str(ws), tag, str(jr)], capture_output=True, text=True, timeout=3600)
    ms = int((time.monotonic() - start) * 1000)
    if p.returncode != 0:
        return None, f"exit {p.returncode}: {(p.stdout + p.stderr).strip()[-160:]}"
    if "already-terminal" in p.stdout: return None, "already-terminal"
    return ms, None

def _mcenv():
    env = dict(os.environ)
    for line in MCENV.read_text().splitlines():
        m = re.match(r"export (\w+)=(.*)", line.strip())
        if m: env[m.group(1)] = m.group(2)
    return env

def run_mcloving(project, tag):
    env = _mcenv()
    cli = str(HOME / "faceoff2" / "bin" / "mcloving-cli")
    base = [cli, "--server", env["MCLOVING_URL"], "--token", env["MCLOVING_API_TOKEN"],
            "--organization", env["MCLOVING_ORGANIZATION_ID"]]
    pid = str(uuid.uuid4())
    p = subprocess.run(base + ["apply", pid, "--slug", tag, "--expected-revision", "0",
                               str(PIPE / f"{project}.yaml")], capture_output=True, text=True,
                       timeout=300, env=env)
    if p.returncode != 0: return None, f"apply: {(p.stdout+p.stderr).strip()[-160:]}"
    start = time.monotonic()
    p = subprocess.run(base + ["submit", pid, "--idempotency-key", f"k-{tag}"],
                       capture_output=True, text=True, timeout=300, env=env)
    m = re.search(r"^build_id: (\S+)", p.stdout or "", re.M)
    if p.returncode != 0 or not m: return None, f"submit: {(p.stdout+p.stderr).strip()[-160:]}"
    build, last, end = m.group(1), "submitted", start + 3600
    while time.monotonic() < end:
        q = subprocess.run(base + ["status", build], capture_output=True, text=True, timeout=180, env=env)
        sm = re.search(r"^status: (\S+)", q.stdout or "", re.M)
        last = sm.group(1) if sm else last
        if last == "succeeded": return int((time.monotonic() - start) * 1000), None
        if last in ("failed", "cancelled", "aborted"): return None, f"build {last}"
        time.sleep(1.0)
    return None, f"stalled (last={last})"

RUN = {"fogell": run_fogell, "jenkins": run_jenkins, "mcloving": run_mcloving}
res = {e: {p: [] for p in PROJECTS} for e in ENGINES}
fails = []
WS.mkdir(parents=True, exist_ok=True)

for r in range(1, ROUNDS + 1):
    for pi, project in enumerate(PROJECTS):
        order = ENGINES[(r + pi) % 3:] + ENGINES[:(r + pi) % 3]
        print(f"=== round {r}  {project}  (order: {' '.join(order)}) ===", flush=True)
        for eng in order:
            tag = f"{eng}-{project.replace('_','-')}-r{r}-{int(time.time())}"
            ms, err = RUN[eng](project, tag)
            if err:
                fails.append((eng, project, r, err))
                print(f"  {eng:<9} FAILED: {err}", flush=True)
                continue
            res[eng][project].append(ms)
            print(f"  {eng:<9} {ms:>7} ms", flush=True)

def med(v):
    s = sorted(v)
    return s[len(s)//2] if len(s) % 2 else (s[len(s)//2 - 1] + s[len(s)//2]) // 2

print("\n=== per-project medians (ms) ===")
print(f"{'project':<24}{'fogell':>10}{'jenkins':>10}{'mcloving':>10}")
for p in PROJECTS:
    row = "".join(f"{med(res[e][p]):>10}" if res[e][p] else f"{'--':>10}" for e in ENGINES)
    print(f"{p:<24}{row}")

if fails:
    print(f"\n!! {len(fails)} FAILED RUN(S) -- refusing an overall ratio; a fast failure")
    print("   is indistinguishable from a fast win.")
    for e, p, r, err in fails: print(f"   {e} {p} r{r}: {err}")
else:
    print("\n=== totals across all six projects (sum of medians) ===")
    tot = {e: sum(med(res[e][p]) for p in PROJECTS) for e in ENGINES}
    for e, v in sorted(tot.items(), key=lambda kv: kv[1]):
        print(f"  {e:<9} {v:>8} ms   {tot['jenkins']/v:.2f}x vs Jenkins")
