import hashlib,json,os,pathlib,signal,subprocess,sys,time
root=pathlib.Path(__file__).resolve().parent
config=json.loads((root/'lab.json').read_text())
repo=pathlib.Path('/home/srikanth/projects/fogell')
def sql(text,database=None):
 return subprocess.run(['podman','exec','-i',config['container'],'psql','-X','-q','-A','-t','-U','fogell','-d',database or config['database'],'-v','ON_ERROR_STOP=1'],input=text.encode(),stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True).stdout.decode().strip()
def connection(database=None):
 return f"Host=127.0.0.1;Port={config['port']};Username=fogell;Database={database or config['database']}"
def migrate():
 sql('CREATE TABLE IF NOT EXISTS schema_migrations(version text PRIMARY KEY,checksum text NOT NULL,applied_at timestamptz NOT NULL DEFAULT clock_timestamp())')
 for p in sorted((repo/'src/Fogell.Store/migrations').glob('*.sql')):
  v=p.name.split('_')[0];h=hashlib.sha256(p.read_bytes()).hexdigest()
  existing=sql(f"SELECT checksum FROM schema_migrations WHERE version='{v}'")
  if existing:assert existing==h,(v,'checksum drift')
  else:sql(f"BEGIN;\n{p.read_text()}\nINSERT INTO schema_migrations(version,checksum) VALUES('{v}','{h}');COMMIT;")
def grants():
 role=config['runtime_role']
 tables=sql("SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename NOT IN ('schema_migrations','controller_metadata','organization_work_roots','build_retention') ORDER BY tablename").splitlines()
 sql(f"GRANT USAGE ON SCHEMA public TO {role};GRANT SELECT,UPDATE(singleton) ON controller_metadata TO {role};GRANT SELECT ON organization_work_roots,build_retention TO {role};GRANT SELECT,INSERT,UPDATE,DELETE ON "+','.join(tables)+f" TO {role};GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO {role};")
def start():
 assert not (root/'process.json').exists(),'existing process ownership receipt'
 env=os.environ.copy()
 env.update(FOGELL_DATABASE_URL=connection()+f";Options=-c role={config['runtime_role']};No Reset On Close=true;Maximum Pool Size=8",FOGELL_MAINTENANCE_DATABASE_URL=connection(),FOGELL_API_TOKEN_FILE=str(root/'token'),FOGELL_LISTEN_URL=config['url'],FOGELL_STATE_ROOT=config.get('state_root',str(root/'state')),FOGELL_RUN_HOST_PATH=str(repo/'tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host'),FOGELL_LOCAL_TRUST_POOL='trusted-linux',FOGELL_MAX_PIPELINE_BYTES=str(16*1024*1024),FOGELL_MAX_LOG_CHUNKS='100',FOGELL_WORKER_POLL_MS='50',FOGELL_WORKER_LEASE_SECONDS='60')
 executable=repo/'src/Fogell.Controller.Host/bin/Release/net10.0/Fogell.Controller.Host'
 with (root/'controller.log').open('ab') as log:
  process=subprocess.Popen([str(executable)],env=env,stdout=log,stderr=log,start_new_session=True)
 identity={'pid':process.pid,'start':pathlib.Path(f'/proc/{process.pid}/stat').read_text().split(')')[1].split()[19],'exe':str(executable),'database':config['database']}
 (root/'process.json').write_text(json.dumps(identity))
 print(json.dumps(identity))
def stop():
 p=json.loads((root/'process.json').read_text());proc=pathlib.Path(f"/proc/{p['pid']}")
 if proc.exists():
  stat=(proc/'stat').read_text().split(')')[1].split()
  if stat[0]!='Z':
   assert stat[19]==p['start'] and str((proc/'exe').resolve())==p['exe'],'process identity changed'
   os.kill(p['pid'],signal.SIGTERM)
   for _ in range(200):
    if not proc.exists() or (proc/'stat').read_text().split(')')[1].split()[0]=='Z':break
    time.sleep(.1)
   else:raise RuntimeError('controller did not exit gracefully')
 (root/'process.json').unlink()
 print('controller stopped')
if __name__=='__main__':
 cmd=sys.argv[1]
 if cmd=='init':
  migrate();grants()
  sql(f"INSERT INTO organizations(id,slug) VALUES('{config['organization']}','fg269-pilot');INSERT INTO projects(id,organization_id,slug) VALUES('{config['project']}','{config['organization']}','self-host');")
 elif cmd=='migrate':migrate();grants()
 elif cmd=='start':start()
 elif cmd=='stop':stop()
 elif cmd=='sql':print(sql(sys.stdin.read()))
