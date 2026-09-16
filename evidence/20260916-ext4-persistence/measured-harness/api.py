"""Minimal controller API client for the owned persistence guest."""
import json, ssl, time, urllib.error, urllib.request, uuid
from vm import ROOT, guest, run, API_PORT
BASE = f'https://127.0.0.1:{API_PORT}'
ORG = 'a0000000-0000-4000-8000-000000000373'
PROJECT = 'b0000000-0000-4000-8000-000000000373'
BUILDS = f'/api/v1/organizations/{ORG}/projects/{PROJECT}/builds'

def sql(query):
    return guest(['sudo','-u','postgres','psql','-X','-v','ON_ERROR_STOP=1','-d','fogell','-At'],input=query).stdout.strip()

def exec_controller(script, check=True):
    return guest(['sudo','-u','fogell','sh','-ec',script],check=check)

def ready(expect=200,budget=30):
    deadline=time.monotonic()+budget
    while time.monotonic()<deadline:
        last=request('/health/ready',auth=False,timeout=3)
        if last['code']==expect:return last
        time.sleep(.25)
    raise AssertionError(f'readiness expected {expect}, observed {last}')

def runner_present(build=None):
    r=guest(['pgrep','-a','-u','fogell','-f','/opt/fogell/runner/Fogell.Run.Host'],check=False)
    assert r.returncode in (0,1),r.stderr
    return r.returncode==0 and (build is None or build.replace('-','') in r.stdout)


HTTP_CLIENT = r"""
import sys,json,time,ssl,urllib.request,urllib.error,pathlib
r=json.load(sys.stdin);headers=r['headers'];body=r['body'];start=time.monotonic()
if r['auth']:headers['Authorization']='Bearer '+pathlib.Path('/vm/token').read_text().strip()
if body is not None:
 body=body.encode('utf-8');headers['Content-Type']='application/x-jenkinsfile'
try:
 try:
  with urllib.request.urlopen(urllib.request.Request('https://127.0.0.1:8080'+r['path'],data=body,headers=headers,method=r['method']),timeout=r['timeout'],context=ssl._create_unverified_context()) as response:code,raw=response.status,response.read()
 except urllib.error.HTTPError as error:code,raw=error.code,error.read()
 try:data=json.loads(raw)
 except ValueError:data=raw.decode('utf-8',errors='replace')[:500]
 result={'code':code,'data':data,'seconds':round(time.monotonic()-start,3)}
except Exception as error:result={'code':0,'error':type(error).__name__}
print(json.dumps(result))
"""

def request(path, method='GET', body=None, headers=None, auth=True, timeout=12):
    from vm import NAME
    values={'path':path,'method':method,'body':body,'headers':dict(headers or {}),'auth':auth,'timeout':timeout}
    result=run(['podman','exec','-i',NAME,'python3','-c',HTTP_CLIENT],input=json.dumps(values),timeout=timeout+8)
    return json.loads(result.stdout)


def pipeline(shell):
    return "pipeline { agent any stages { stage('storage') { steps { sh '''" + shell + "''' } } } }"

def stash_pipeline(shell):
    return (
        "pipeline { agent any stages { stage('storage') { steps { sh '''"
        + shell
        + "'''; stash name: 'storage-payload', includes: 'payload.bin' } } } }"
    )

def submit(source, key=None):
    return request(BUILDS, method="POST", body=source, headers={"Idempotency-Key": key or str(uuid.uuid4())})

def build_status(build):
    return request(f"{BUILDS}/{build}")

def settle(build, budget=75):
    deadline = time.monotonic() + budget
    last = None
    while time.monotonic() < deadline:
        last = build_status(build)
        state = last.get("data", {}).get("status")
        if state not in (None, "queued", "running"):
            return last
        time.sleep(0.2)
    raise RuntimeError(f"build did not settle: {build} {last}")

def attempt_state(build):
    return sql(
        "SELECT a.state FROM attempts a JOIN nodes n ON n.id=a.node_id AND n.organization_id=a.organization_id "
        f"WHERE n.build_id='{build}' ORDER BY a.created_at DESC LIMIT 1"
    )

def wait_attempt(build, wanted, budget=20):
    deadline = time.monotonic() + budget
    observed = ""
    while time.monotonic() < deadline:
        observed = attempt_state(build)
        if observed == wanted:
            return observed
        time.sleep(0.15)
    raise RuntimeError(f"attempt {build} never reached {wanted}; last={observed}")

def pool_decision():
    result = exec_controller("test -f /srv/fogell/state/storage-admission.json && cat /srv/fogell/state/storage-admission.json", check=False)
    if result.returncode:
        return None
    try:
        return json.loads(result.stdout)
    except ValueError:
        return {"unparseable": True}

def pool_stats():
    """Read the kernel's actual block and inode counters for the mounted pool."""
    output = exec_controller(
        "stat -fc '%b %S %a %c %d' /srv/fogell/state/workspaces", check=True
    ).stdout.strip().split()
    if len(output) != 5 or not all(part.isdecimal() for part in output):
        raise RuntimeError("unable to read pool statfs counters")
    blocks, block_size, available_blocks, inodes, available_inodes = map(int, output)
    return {
        "total_bytes": blocks * block_size,
        "available_bytes": available_blocks * block_size,
        "total_inodes": inodes,
        "available_inodes": available_inodes,
    }

def remove_build_data(build):
    """Release only the exact build/attempt paths created by this harness."""
    build_key = build.replace("-", "")
    attempt = sql(
        "SELECT a.id FROM attempts a JOIN nodes n ON n.id=a.node_id AND n.organization_id=a.organization_id "
        f"WHERE n.build_id='{build}' ORDER BY a.created_at DESC LIMIT 1"
    ).replace("-", "")
    organization = ORG.replace("-", "")
    paths = [
        f"/srv/fogell/state/workspaces/{organization}/{build_key}",
        f"/srv/fogell/state/workspaces/{organization}/_artifacts/{build_key}",
        f"/srv/fogell/state/workspaces/{organization}/_artifact-snapshots/{attempt}",
        f"/srv/fogell/state/workspaces/{organization}/_runtime/{attempt}",
    ]
    exec_controller("rm -rf -- " + " ".join(paths))
