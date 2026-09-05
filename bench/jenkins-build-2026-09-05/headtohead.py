#!/usr/bin/env python3
"""Fogell vs Jenkins on ONE identical real build: Jenkins core 2.577.

Same Jenkinsfile, same host, same warm ~/.m2, same offline Maven, build cache
disabled so both do identical work. Wall clock from trigger to terminal is the
only comparable quantity: Jenkins' self-reported `duration` excludes queue and
post-build bookkeeping that a user waits through anyway.

Engine order alternates per round so drift on the box does not land on one side.
"""
import http.cookiejar, json, pathlib, shutil, subprocess, time, urllib.parse, urllib.request

HOME = pathlib.Path.home()
ROOT = HOME / "fogell-build-jenkins"
JF = ROOT / "headtohead.Jenkinsfile"
DLL = HOME / "faceoff2" / "fogell" / "net10.0" / "Fogell.Run.Host.dll"
JURL = "http://127.0.0.1:18086"
ROUNDS = 3

jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

def crumb():
    with opener.open(f"{JURL}/crumbIssuer/api/json", timeout=15) as r:
        return json.load(r)["crumb"]

def post(path, data=None, ctype=None):
    req = urllib.request.Request(f"{JURL}{path}", data=data, method="POST")
    req.add_header("Jenkins-Crumb", crumb())
    if ctype:
        req.add_header("Content-Type", ctype)
    try:
        with opener.open(req, timeout=120) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code

def run_jenkins(tag):
    job = f"h2h-{tag}"
    script = JF.read_text()
    esc = script.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    cfg = f"""<?xml version='1.1' encoding='UTF-8'?>
<flow-definition plugin="workflow-job">
  <keepDependencies>false</keepDependencies><properties/>
  <definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps">
    <script>{esc}</script><sandbox>false</sandbox>
  </definition>
  <triggers/><disabled>false</disabled>
</flow-definition>"""
    # Fresh job every heat: Jenkins' per-build cost grows with job history.
    post(f"/job/{job}/doDelete")
    code = post(f"/createItem?name={urllib.parse.quote(job)}", cfg.encode(), "application/xml")
    if code not in (200, 201):
        return None, f"createItem {code}"
    with opener.open(f"{JURL}/job/{job}/api/json?tree=nextBuildNumber", timeout=15) as r:
        n = json.load(r)["nextBuildNumber"]
    start = time.monotonic()
    if post(f"/job/{job}/build") not in (200, 201):
        return None, "trigger failed"
    deadline = start + 1800
    while time.monotonic() < deadline:
        try:
            with opener.open(f"{JURL}/job/{job}/{n}/api/json?tree=result,duration", timeout=15) as r:
                b = json.load(r)
            if b.get("result"):
                wall = int((time.monotonic() - start) * 1000)
                return (wall, b["result"], b.get("duration")), None
        except urllib.error.HTTPError:
            pass
        time.sleep(1.0)
    return None, "stalled"

def run_fogell(tag):
    ws = ROOT / f"ws-{tag}"
    jr = ROOT / f"j-{tag}.log"
    shutil.rmtree(ws, ignore_errors=True)
    jr.unlink(missing_ok=True)
    ws.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    p = subprocess.run(["dotnet", str(DLL), str(JF), str(ws), tag, str(jr)],
                       capture_output=True, text=True, timeout=1800)
    wall = int((time.monotonic() - start) * 1000)
    if p.returncode != 0:
        return None, f"exit {p.returncode}: {(p.stdout+p.stderr).strip()[-200:]}"
    if "already-terminal" in p.stdout:
        return None, "already-terminal"
    return (wall, "SUCCESS", None), None

results = {"fogell": [], "jenkins": []}
failures = {"fogell": 0, "jenkins": 0}
for r in range(1, ROUNDS + 1):
    order = ["fogell", "jenkins"] if r % 2 else ["jenkins", "fogell"]
    print(f"=== round {r} (order: {' '.join(order)}) ===", flush=True)
    for eng in order:
        tag = f"{eng}-r{r}-{int(time.time())}"
        got, err = (run_fogell if eng == "fogell" else run_jenkins)(tag)
        if err:
            failures[eng] += 1
            print(f"  {eng:<8} FAILED: {err}", flush=True)
            continue
        wall, verdict, selfdur = got
        results[eng].append(wall)
        extra = f"  (jenkins self-reported {selfdur} ms)" if selfdur else ""
        print(f"  {eng:<8} {wall:>7} ms  {verdict}{extra}", flush=True)

bad = [e for e, n in failures.items() if n]
if bad:
    print("\n!! REFUSING TO SUMMARISE: " + ", ".join(f"{e} failed {failures[e]}/{ROUNDS}" for e in bad))
    print("   A fast failure is indistinguishable from a fast win in a ratio.")
    raise SystemExit(1)

print("\n=== medians (wall clock, trigger -> terminal) ===")
for eng, vals in results.items():
    if vals:
        s = sorted(vals)
        med = s[len(s)//2] if len(s) % 2 else (s[len(s)//2 - 1] + s[len(s)//2]) // 2
        print(f"  {eng:<8} n={len(vals)}  median {med} ms   raw {vals}")
if results["fogell"] and results["jenkins"]:
    mf = sorted(results["fogell"])[len(results["fogell"])//2]
    mj = sorted(results["jenkins"])[len(results["jenkins"])//2]
    print(f"\n  Jenkins overhead vs Fogell on this build: {mj - mf} ms ({mj/mf:.2f}x total wall clock)")
