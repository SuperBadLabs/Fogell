import hashlib,json,os,re,stat,subprocess
from pathlib import Path
root=Path(__file__).resolve().parent
repo=Path('/home/srikanth/projects/fogell')
r=json.loads((repo/'evidence/20260922-ai-e2e/pilot/receipt.json').read_text())
assert r['passed'] is True and len(r['runs'])==60
subprocess.run(['python3',str(repo/'scripts/prove-self-hosted-pilot.py'),'--verify',str(repo/'evidence/20260922-ai-e2e/pilot/receipt.json')],check=True)
c=json.loads((root/'lab.json').read_text());assert c['database']=='fogell_pilot_final'
subprocess.run(['python3',str(root/'lab.py'),'stop'],check=True)
removed=[]
for p in (root/'state').rglob('*'):
 s=p.lstat()
 if stat.S_ISREG(s.st_mode) or stat.S_ISDIR(s.st_mode):continue
 assert p.parent==root/'state/tmp'
 m=re.fullmatch(r'(?:clr-debug-pipe-(\d+)-\d+-(?:in|out)|dotnet-diagnostic-(\d+)-\d+-socket)',p.name)
 assert m and (stat.S_ISFIFO(s.st_mode) or stat.S_ISSOCK(s.st_mode))
 pid=int(m.group(1) or m.group(2));assert not Path(f'/proc/{pid}').exists()
 removed.append({'path':str(p.relative_to(root/'state')),'mode':oct(s.st_mode),'owner_pid_absent':pid});p.unlink()
(root/'quiesce-cleanup.json').write_text(json.dumps({'removed_volatile_runtime_endpoints':removed},indent=2)+'\n')
last=r['runs'][-1]
w={'organization':c['organization'],'project':c['project'],'build':last['admission']['build_id'],'attempt':last['admission']['attempt_id'],'junit_sha256':last['junit_sha256']}
(root/'workload-proof.json').write_text(json.dumps(w,indent=2)+'\n')
env=os.environ.copy();env['FOGELL_MAINTENANCE_DATABASE_URL']=f"Host=127.0.0.1;Port={c['port']};Username=fogell;Database={c['database']}"
subprocess.run(['dotnet','fsi','--exec',str(repo/'scripts/prove-paired-recovery-stale.fsx'),'seed',str(root/'stale-authority.json')],env=env,check=True)
env['FOGELL_MAINTENANCE_DATABASE_URL']=f"Host=127.0.0.1;Port={c['port']};Username=fogell;Database=fogell_pilot_final_restored"
subprocess.run(['python3',str(repo/'scripts/prove-paired-recovery.py'),'--container',c['container'],'--source-database',c['database'],'--target-database','fogell_pilot_final_restored','--state-root',str(root/'state'),'--restored-state-root',str(root/'restored-state'),'--output',str(root/'recovery'),'--authority-receipt',str(root/'stale-authority.json'),'--workload-proof',str(root/'workload-proof.json'),'--post-restore-command',str(root/'post-restore-command.json'),'--writers-quiesced'],env=env,check=True)
print((root/'recovery/receipt.json').read_text())
