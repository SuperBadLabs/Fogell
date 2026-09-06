#!/usr/bin/env python3
"""Throughput under simultaneous load: six real builds launched at once.

Single-build latency is the wrong axis for a CI server -- real pipelines do not
have 500 stages, but a real server does have many builds arriving together.
This launches all six projects SIMULTANEOUSLY on each engine and measures the
time until the last one finishes.

A fourth arm, `bare`, runs the same six builds as plain parallel shells with no
CI engine at all. It is the floor: whatever an engine costs above `bare`,
measured in the SAME session, is the engine.
"""
import concurrent.futures as cf, http.cookiejar, json, os, pathlib, re, shutil
import subprocess, time, urllib.error, urllib.parse, urllib.request, uuid

HOME = pathlib.Path.home()
PIPE = HOME / "standoff" / "pipelines"
DLL = HOME / "faceoff2" / "fogell" / "net10.0" / "Fogell.Run.Host.dll"
WS = HOME / "standoff" / "cws"
JURL = "http://127.0.0.1:18086"
MCENV = HOME / "faceoff2" / "mcrun" / "env"
MVN = str(HOME / "fogell-build-jenkins" / "maven" / "bin" / "mvn")
FLAGS = ("-B -o -DskipTests -Dspotbugs.skip=true -Dcheckstyle.skip=true "
         "-Dmaven.javadoc.skip=true -Drat.skip=true -Dmaven.source.skip=true "
         "-Ddevelocity.cache.local.enabled=false")
ROUNDS = 2
PROJECTS = [("jenkinsci_git-plugin", "21", ""), ("apache_activemq", "25", ":activemq-client"),
            ("apache_cxf", "21", ":cxf-core"), ("apache_dubbo", "21", ":dubbo-common"),
            ("apache_maven", "21", ":maven-core"), ("apache_camel", "21", ":camel-api")]
ARMS = ["bare", "fogell", "jenkins", "mcloving"]

jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
def crumb():
    with opener.open(f"{JURL}/crumbIssuer/api/json", timeout=20) as r:
        return json.load(r)["crumb"]
def jpost(path, data=None, ctype=None):
    req = urllib.request.Request(f"{JURL}{path}", data=data, method="POST")
    req.add_header("Jenkins-Crumb", crumb())
    if ctype: req.add_header("Content-Type", ctype)
    try:
        with opener.open(req, timeout=300) as r: return r.status
    except urllib.error.HTTPError as e: return e.code

def bare_one(proj, tag):
    name, jdk, sel = proj
    d = HOME / "standoff" / name
    env = dict(os.environ, JAVA_HOME=f"/usr/lib/jvm/java-{jdk}-openjdk-amd64")
    pl = f" -pl {sel} -am" if sel else ""
    t = time.monotonic()
    p = subprocess.run(f"cd {d} && {MVN} {FLAGS}{pl} clean package",
                       shell=True, capture_output=True, text=True, timeout=5400, env=env)
    return (int((time.monotonic()-t)*1000), None) if p.returncode == 0 else (None, "bare failed")

def fogell_one(proj, tag):
    name = proj[0]
    ws, jr = WS / f"w-{tag}", WS / f"j-{tag}.log"
    shutil.rmtree(ws, ignore_errors=True); jr.unlink(missing_ok=True)
    ws.mkdir(parents=True, exist_ok=True)
    t = time.monotonic()
    p = subprocess.run(["dotnet", str(DLL), str(PIPE / f"{name}.Jenkinsfile"), str(ws), tag, str(jr)],
                       capture_output=True, text=True, timeout=5400)
    if p.returncode != 0: return None, f"exit {p.returncode}"
    if "already-terminal" in p.stdout: return None, "already-terminal"
    return int((time.monotonic()-t)*1000), None

def jenkins_one(proj, tag):
    name = proj[0]; job = f"cc-{tag}"
    esc = (PIPE / f"{name}.Jenkinsfile").read_text().replace("&","&amp;").replace("<","&lt;").replace(">","&gt;")
    cfg = ("<?xml version='1.1' encoding='UTF-8'?><flow-definition plugin=\"workflow-job\">"
           "<keepDependencies>false</keepDependencies><properties/>"
           "<definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\">"
           f"<script>{esc}</script><sandbox>false</sandbox></definition><triggers/>"
           "<disabled>false</disabled></flow-definition>")
    jpost(f"/job/{job}/doDelete")
    if jpost(f"/createItem?name={urllib.parse.quote(job)}", cfg.encode(), "application/xml") not in (200,201):
        return None, "createItem"
    with opener.open(f"{JURL}/job/{job}/api/json?tree=nextBuildNumber", timeout=20) as r:
        n = json.load(r)["nextBuildNumber"]
    t = time.monotonic()
    if jpost(f"/job/{job}/build") not in (200,201): return None, "trigger"
    end = t + 5400
    while time.monotonic() < end:
        try:
            with opener.open(f"{JURL}/job/{job}/{n}/api/json?tree=result", timeout=20) as r:
                b = json.load(r)
            if b.get("result"):
                return (int((time.monotonic()-t)*1000), None) if b["result"] == "SUCCESS" else (None, b["result"])
        except urllib.error.HTTPError: pass
        time.sleep(1.0)
    return None, "stalled"

def _mcenv():
    env = dict(os.environ)
    for line in MCENV.read_text().splitlines():
        m = re.match(r"export (\w+)=(.*)", line.strip())
        if m: env[m.group(1)] = m.group(2)
    return env
MCE = None
def mcloving_one(proj, tag):
    name = proj[0]; env = MCE
    cli = str(HOME / "faceoff2" / "bin" / "mcloving-cli")
    base = [cli, "--server", env["MCLOVING_URL"], "--token", env["MCLOVING_API_TOKEN"],
            "--organization", env["MCLOVING_ORGANIZATION_ID"]]
    pid = str(uuid.uuid4())
    p = subprocess.run(base + ["apply", pid, "--slug", tag, "--expected-revision", "0",
                               str(PIPE / f"{name}.yaml")], capture_output=True, text=True, timeout=600, env=env)
    if p.returncode != 0: return None, "apply"
    t = time.monotonic()
    p = subprocess.run(base + ["submit", pid, "--idempotency-key", f"k-{tag}"],
                       capture_output=True, text=True, timeout=600, env=env)
    m = re.search(r"^build_id: (\S+)", p.stdout or "", re.M)
    if p.returncode != 0 or not m: return None, "submit"
    build, last, end = m.group(1), "submitted", t + 5400
    while time.monotonic() < end:
        q = subprocess.run(base + ["status", build], capture_output=True, text=True, timeout=300, env=env)
        sm = re.search(r"^status: (\S+)", q.stdout or "", re.M)
        last = sm.group(1) if sm else last
        if last == "succeeded": return int((time.monotonic()-t)*1000), None
        if last in ("failed","cancelled","aborted"): return None, f"build {last}"
        time.sleep(1.0)
    return None, f"stalled({last})"

RUN = {"bare": bare_one, "fogell": fogell_one, "jenkins": jenkins_one, "mcloving": mcloving_one}
MCE = _mcenv()
WS.mkdir(parents=True, exist_ok=True)
totals = {a: [] for a in ARMS}
fails = []

for r in range(1, ROUNDS+1):
    for ai in range(len(ARMS)):
        arm = ARMS[(r + ai) % len(ARMS)]
        stamp = int(time.time())
        print(f"=== round {r}  {arm}: launching all 6 simultaneously ===", flush=True)
        t0 = time.monotonic()
        with cf.ThreadPoolExecutor(max_workers=len(PROJECTS)) as pool:
            futs = {pool.submit(RUN[arm], p, f"{arm}-{p[0].replace('_','-')}-r{r}-{stamp}"): p
                    for p in PROJECTS}
            per = {}
            for f in cf.as_completed(futs):
                proj = futs[f]
                ms, err = f.result()
                if err:
                    fails.append((arm, proj[0], r, err)); print(f"   {proj[0]:<22} FAILED {err}", flush=True)
                else:
                    per[proj[0]] = ms
        wall = int((time.monotonic()-t0)*1000)
        if len(per) == len(PROJECTS):
            totals[arm].append(wall)
            print(f"   ALL SIX DONE in {wall} ms   (slowest single build {max(per.values())} ms)", flush=True)
        else:
            print(f"   INCOMPLETE ({len(per)}/{len(PROJECTS)}) after {wall} ms", flush=True)

def med(v):
    s = sorted(v); return s[len(s)//2] if len(s)%2 else (s[len(s)//2-1]+s[len(s)//2])//2

print("\n=== wall clock to finish ALL SIX builds launched at once ===")
if fails:
    print(f"!! {len(fails)} failed run(s); arms with failures are not summarised")
    for a,p,r,e in fails: print(f"   {a} {p} r{r}: {e}")
ok = {a: v for a, v in totals.items() if len(v) == ROUNDS}
for a, v in sorted(ok.items(), key=lambda kv: med(kv[1])):
    print(f"  {a:<9} median {med(v):>8} ms   raw {v}")
if "bare" in ok:
    b = med(ok["bare"])
    print(f"\n=== engine cost above the no-engine floor ({b} ms) ===")
    for a, v in sorted(ok.items(), key=lambda kv: med(kv[1])):
        if a != "bare":
            m = med(v)
            print(f"  {a:<9} +{m-b:>7} ms   {m/b:.2f}x the floor   engine = {100*(m-b)/m:.0f}% of wall clock")
