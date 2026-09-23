import hashlib,importlib.util,json,pathlib,subprocess,time,urllib.request,uuid
root=pathlib.Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('lab',root/'lab.py');lab=importlib.util.module_from_spec(spec);spec.loader.exec_module(lab)
c=lab.config
c['database']='fogell_pilot_restored_20260922'
c['state_root']=str(root/'restored-state')
(root/'lab.json').write_text(json.dumps(c,indent=2)+'\n')
started=subprocess.run(['python3',str(root/'lab.py'),'start'],capture_output=True,check=True)
identity=json.loads((root/'process.json').read_text())
def verify_owned_process():
 proc=pathlib.Path(f"/proc/{identity['pid']}")
 info=(proc/'stat').read_text().split(')')[1].split()
 assert info[0]!='Z' and info[19]==identity['start'] and str((proc/'exe').resolve())==identity['exe']
for _ in range(150):
 verify_owned_process()
 try:
  with urllib.request.urlopen(c['url']+'/health/ready',timeout=1) as r:
   if r.status==200:break
 except Exception:time.sleep(.1)
else:raise RuntimeError('restored controller did not become ready')
verify_owned_process()
w=json.loads((root/'workload-proof.json').read_text())
url=f"{c['url']}/api/v1/organizations/{w['organization']}/projects/{w['project']}/builds/{w['build']}/attempts/{w['attempt']}/artifacts/reports/domain.xml"
request=urllib.request.Request(url,headers={'Authorization':'Bearer '+(root/'token').read_text().strip()})
with urllib.request.urlopen(request,timeout=10) as r:
 assert r.status==200
 data=r.read(2097153)
assert len(data)<=2097152
digest=hashlib.sha256(data).hexdigest();assert digest==w['junit_sha256']
cmd=json.loads((root/'smoke-command.json').read_text())
cmd=[v.replace('smoke-work-1','recovery-control-work').replace('/smoke-1','/recovery-control') for v in cmd]
cmd[cmd.index('--state-root')+1]=c['state_root']
declaration=json.loads((root/'deployment.json').read_text())
declaration['recovery_target']={'database':c['database'],'state_root':c['state_root'],'owned_process':identity}
(root/'restored-deployment.json').write_text(json.dumps(declaration,indent=2)+'\n')
cmd[cmd.index('--deployment-declaration')+1]=str(root/'restored-deployment.json')
control=subprocess.run(cmd,capture_output=True,timeout=90)
(root/'recovery-control.stdout').write_bytes(control.stdout)
(root/'recovery-control.stderr').write_bytes(control.stderr)
assert control.returncode==0,'recovery control failed'
receipt=json.loads((root/'recovery-control/receipt.json').read_text())
assert receipt['passed'] is True
for run in receipt['runs']:
 build=str(uuid.UUID(run['admission']['build_id']))
 expected='unstable' if run['kind']=='failed' else 'success'
 assert lab.sql(f"SELECT status FROM builds WHERE id='{build}'")==expected,'control used a different database'
verify_owned_process()
print(json.dumps({'passed':True,'database':c['database'],'previous_artifact_sha256':digest,'control_receipt_sha256':hashlib.sha256((root/'recovery-control/receipt.json').read_bytes()).hexdigest(),'new_builds':[r['admission']['build_id'] for r in receipt['runs']]}))
