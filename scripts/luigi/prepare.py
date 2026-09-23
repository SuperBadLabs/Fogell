#!/usr/bin/env python3
"""Record deployment identity/effective limits and prepare the pinned pilot."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess


def cmd(argv):
    return subprocess.check_output(argv, timeout=30).decode().strip()


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--refresh', action='store_true', help='After upgrade: retain prior declaration and prepare30-loop qualification plus recovery pins')
args = parser.parse_args()

def require(value, message):
    if not value:
        raise ValueError(message)

root = Path.home()/'services/fogell'
c = json.loads((root/'deployment.json').read_text())
ctl = c['controller_container_id']
pg = c['postgres_container_id']
out = root/'campaigns'
release = Path(c.get('release_root', root/'release')).resolve()
require(release.is_relative_to(root) and release != root, 'unowned release path')
require(platform.node() == 'luigi', 'Luigi only')
if args.refresh:
    require(json.loads((out/'pilot/receipt.json').read_text())['passed'] is True, 'baseline must finish before refresh')
else:
    require(not (out/'deployment.json').exists(), 'declaration exists; use explicit post-campaign refresh')
effective = {}
for name, identity, memory, pids, cpu in [('controller',ctl,8589934592,512,8),('postgres',pg,2147483648,256,2)]:
    values = cmd(['podman','exec',identity,'sh','-c',
                  'cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/pids.max /sys/fs/cgroup/cpu.max /sys/fs/cgroup/memory.swap.max']).splitlines()
    quota, period = map(int,values[2].split())
    require(int(values[0]) == memory and int(values[1]) == pids and quota/period == cpu and values[3]=='0', 'effective cgroup bounds differ')
    effective[name] = dict(zip(['memory.max','pids.max','cpu.max','memory.swap.max'],values))
tool = json.loads(cmd(['podman','exec',ctl,'dotnet','/app/tools/Fogell.Client/bin/Release/net10.0/Fogell.Client.dll',
                      'tool-identity','--run-host','/app/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host']))
require(cmd(['podman','exec','--env','DOTNET_CLI_HOME=/tmp',ctl,'dotnet','--version'])=='10.0.301', 'SDK differs')
manifest = json.loads((release/'release-manifest.json').read_text())
for name, sha in manifest['files'].items():
    require((release/name).resolve().is_relative_to(release) and hashlib.sha256((release/name).read_bytes()).hexdigest()==sha, 'release hash differs')
inspect = json.loads(cmd(['podman','inspect',ctl]))[0]
require(inspect['HostConfig']['ReadonlyRootfs'] is True, 'root filesystem is writable')
vfs = __import__('os').statvfs(root)
declaration = {'profile':1,'host':platform.uname()._asdict(), 'deployment':c,
               'cpu_models':sorted({line.split(':',1)[1].strip() for line in Path('/proc/cpuinfo').read_text().splitlines() if line.startswith('model name')}),
               'sdk':'10.0.301','run_host':tool,'source_manifest_sha256':hashlib.sha256((release/'release-manifest.json').read_bytes()).hexdigest(),
               'release_files':manifest['files'],'effective_cgroups':effective,
               'controller':{'workers':1,'poll_ms':100,'lease_seconds':30,'max_log_chunks':100,
                             'max_pipeline_bytes':16777216,'storage_pool_enabled':False},
               'mounts':[{k:m.get(k) for k in ['Type','Source','Destination','RW','Options']} for m in inspect['Mounts']],
               'filesystem':{'free_bytes':vfs.f_bavail*vfs.f_frsize,'free_inodes':vfs.f_favail},
               'guard':{'max_state_bytes':2147483648,'min_host_free_bytes':21474836480,'is_hard_quota':False}}
if args.refresh:
    previous = json.loads((out/'deployment.json').read_text())
    snapshot = out/('deployment-before-'+previous['deployment']['controller_container_id']+'.json')
    require(not snapshot.exists(), 'previous declaration was already archived')
    snapshot.write_bytes((out/'deployment.json').read_bytes())
(out/'deployment.json').write_text(json.dumps(declaration,indent=2)+'\n')
retention=['dotnet','/app/tools/Fogell.Retention/bin/Release/net10.0/Fogell.Retention.dll','--state-root','/data',
           '--organization',c['organization'],'--keep-builds','2','--max-bytes','67108864','--max-age-seconds','86400',
           '--max-operations','1024','--budget-seconds','30']
(out/'retention-command.json').write_text(json.dumps(retention)+'\n')
base=['python3','/app/scripts/prove-self-hosted-pilot.py','--url',c['url'],'--organization',c['organization'],
      '--project',c['project'],'--token-file','/run/fogell/token','--cache','/tmp/fogell-pilot-luigi/packages',
      '--state-root','/data','--deployment-declaration','/campaign/deployment.json',
      '--retention-command','/campaign/retention-command.json','--tool-sha256',tool['run_host_sha256']]
for name,loops in ([('smoke',1),('qualification',30)] if args.refresh else [('smoke',1),('pilot',100)]):
    argv=['podman','exec',ctl]+base+['--loops',str(loops),'--work','/campaign/'+name+'-work','--output','/campaign/'+name]
    (out/(name+'-command.json')).write_text(json.dumps(argv)+'\n')
print(json.dumps({'prepared':True,'tool':tool,'effective_cgroups':effective}))
