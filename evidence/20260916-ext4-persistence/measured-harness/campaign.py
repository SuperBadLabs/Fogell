"""Ext4 guest-power-cut campaign. Never repairs or reinitializes pool metadata."""
import hashlib, json, pathlib, time
import vm, api
STATE='/srv/fogell/state'
POOL=STATE+'/workspaces'
HELPER='/opt/fogell/storage-pool.py'
POOL_ID='ext4persistence20260916'

def record(test,**data):
    row=dict(test=test,time=time.time(),**data)
    with (vm.ROOT/'results.jsonl').open('a') as f:f.write(json.dumps(row,sort_keys=True)+'\n')
    print(json.dumps(row,sort_keys=True),flush=True)
    return row

def python(code):
    return vm.guest(['sudo','python3','-c',code]).stdout

def snapshot():
    return json.loads(python('''import os,stat,json,hashlib
p='/srv/fogell/state/workspaces/'
r={}
for name in ['.fogell-pool-id','.fogell-pool-state']:
 s=os.lstat(p+name);assert stat.S_ISREG(s.st_mode)
 b=open(p+name,'rb').read()
 r[name]={'sha256':hashlib.sha256(b).hexdigest(),'size':len(b),'mode':oct(stat.S_IMODE(s.st_mode)),'inode':s.st_ino,'device':s.st_dev,'nonzero':any(b)}
print(json.dumps(r))'''))

def assert_state(s,dirty,expected=None):
    item=s['.fogell-pool-state']
    assert item['size']==4096 and item['mode']=='0o600' and item['nonzero']==dirty,s
    assert s['.fogell-pool-id']['mode']=='0o600',s
    if expected is not None:assert s==expected,{'expected':expected,'observed':s}
    return item['sha256']

def wait_idle():
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        s=snapshot()
        if not s['.fogell-pool-state']['nonzero']:return assert_state(s,False)
        time.sleep(.1)
    raise AssertionError('pool did not become idle')

def controller(start):
    vm.guest(['sudo','systemctl','start' if start else 'stop','fogell-controller.service'])
    if not start:
        assert vm.guest(['sudo','systemctl','show','-p','MainPID','--value','fogell-controller.service']).stdout.strip()=='0'
        assert not api.runner_present(), 'live execution writer remains'

def submit(shell):
    r=api.submit(api.pipeline(shell));assert r['code']==201,r
    return r['data']['build_id']

def succeeds(build):
    r=api.settle(build,90);assert r['data']['status']=='success',r
    wait_idle()

def control(label):
    api.ready()
    build=submit('test -d "$TMPDIR"; temp=$(/usr/bin/mktemp); case "$temp" in "$HOME/tmp/"*) printf bounded > "$temp";; *) exit 89;; esac')
    succeeds(build);record(label,build_id=build,status='success',bounded_tmpdir=True,state=snapshot())
    return build

def no_start(build):
    # Observe across multiple worker polling intervals, then bind to DB state.
    for _ in range(4):
        assert api.request('/health/ready',auth=False)['code']==503
        assert api.attempt_state(build)=='queued' and not api.runner_present(),build
        time.sleep(.5)

def receipts():
    return json.loads(python('''import pathlib,json,hashlib
p=pathlib.Path('/srv/fogell/state/storage-pool-receipts')
r={}
if p.exists():
 for f in sorted(p.glob('*.json')):
  b=f.read_bytes();r[f.name]={'sha256':hashlib.sha256(b).hexdigest(),'record':json.loads(b)}
print(json.dumps(r))'''))

def prepare_receipts(cycle):
    python(f'''import pathlib,os,pwd
root=pathlib.Path('{STATE}');p=root/'storage-pool-receipts';p.mkdir(mode=0o700,exist_ok=True)
a=root/'recovery-archive';a.mkdir(mode=0o700,exist_ok=True);d=a/'cycle-{cycle}';d.mkdir(mode=0o700)
for f in p.iterdir():
 assert f.is_file() and not f.is_symlink()
 f.rename(d/f.name)
u=pwd.getpwnam('fogell');os.chown(p,u.pw_uid,u.pw_gid)
for x in [p,d,a,root]:
 fd=os.open(x,os.O_RDONLY|os.O_DIRECTORY);os.fsync(fd);os.close(fd)
''')
    assert receipts()=={}

def helper(sha,note,attest=True,check=True):
    args=['sudo','-u','fogell','python3',HELPER,'recover','--state-root',STATE,'--pool-id',POOL_ID,'--expected-record-sha256',sha,'--operator-note',note]
    if attest:args.append('--writers-extinct')
    return vm.guest(args,check=check)

def capture_boot(label):
    kernel=vm.guest(['sudo','journalctl','-k','-b','--no-pager']).stdout
    (vm.ROOT/('kernel-'+label+'.log')).write_text(kernel)
    (vm.ROOT/('postgres-'+label+'.log')).write_text(vm.guest(['sudo','journalctl','-u','postgresql@16-main.service','-b','--no-pager']).stdout)

def pressure(kind):
    api.ready();before=api.pool_stats()
    if kind=='bytes':
        command=f'head -c 536870912 /dev/zero >{POOL}/.proof-byte-fill'
        cleanup=f'rm -f {POOL}/.proof-byte-fill'
    else:
        command=f'i=0; while test $i -lt 8192; do : >{POOL}/.proof-inode-$i || exit $?; i=$((i+1)); done'
        cleanup=f'rm -f {POOL}/.proof-inode-*'
    r=api.exec_controller(command,check=False)
    assert r.returncode!=0 and 'No space left on device' in r.stderr,(r.returncode,r.stderr)
    full=api.pool_stats()
    assert full['available_bytes' if kind=='bytes' else 'available_inodes']==0,full
    api.exec_controller(f'printf writable >{STATE}/.proof-controller-write; test -s {STATE}/.proof-controller-write; rm {STATE}/.proof-controller-write')
    api.ready(expect=503);build=submit('printf relieved >pressure-control.txt');no_start(build)
    decision=api.pool_decision();expected='storage_pool_byte_pressure' if kind=='bytes' else 'storage_pool_inode_pressure'
    assert decision['decision']==expected,decision
    api.exec_controller(cleanup);api.ready();succeeds(build)
    record('ext4_'+kind+'_pressure',before=before,exhausted=full,restored=api.pool_stats(),kernel_enospc=r.stderr.strip(),state_writable=True,queued_without_runner=True,durable_reason=expected,build_id=build,relief_status='success')

def cycle(number):
    prepare_receipts(number)
    api.ready();active=submit(f'printf armed > crash-{number}.txt; sleep 300; printf unexpected >after-crash.txt')
    api.wait_attempt(active,'running',30)
    deadline=time.monotonic()+20
    while not api.runner_present(active):
        assert time.monotonic()<deadline,'runner never appeared'
        time.sleep(.1)
    before=snapshot();sha=assert_state(before,True)
    processes=vm.guest(['ps','-u','fogell','-o','pid,ppid,stat,args']).stdout
    record('active_checkpoint',cycle=number,build_id=active,state=before,processes=processes)
    vm.stop(f'active-{number}',abrupt=True)
    vm.boot(f'active-reboot-{number}');capture_boot(f'active-reboot-{number}')
    assert_state(snapshot(),True,before)
    controller(True);api.ready(expect=503)
    queued=submit(f'printf recovered > recovered-{number}.txt');no_start(queued)
    assert_state(snapshot(),True,before)
    record('dirty_reboot_refused',cycle=number,state=snapshot(),queued_build=queued,readiness=503,no_runner=True)
    controller(False)
    for name,digest,attest in [('missing-attestation',sha,False),('wrong-hash','0'*64,True)]:
        r=helper(digest,f'negative-{number}-{name}',attest=attest,check=False)
        assert r.returncode!=0
        assert_state(snapshot(),True,before);assert receipts()=={}
        record('recovery_refused',cycle=number,reason=name,exit_code=r.returncode,dirty_sha256=sha,no_receipt=True)
    vm.guest(['sudo','install','-d','-o','fogell','-g','fogell','-m','0700','/run/fogell-proof'])
    vm.guest(['sudo','systemd-run','--unit=fogell-recovery-cutpoint','--uid=fogell','--gid=fogell','--property=NoNewPrivileges=yes','--','/usr/bin/python3','/opt/fogell/recovery-cutpoint.py',HELPER,'recover','--state-root',STATE,'--pool-id',POOL_ID,'--writers-extinct','--expected-record-sha256',sha,'--operator-note',f'cutpoint-cycle-{number}'])
    deadline=time.monotonic()+20
    while vm.guest(['sudo','test','-s','/run/fogell-proof/checkpoint.json'],check=False).returncode:
        assert time.monotonic()<deadline,'receipt fsync checkpoint not reached'
        time.sleep(.1)
    witness=json.loads(vm.guest(['sudo','cat','/run/fogell-proof/checkpoint.json']).stdout)
    assert witness['successful_fsync'] is True
    actual_helper_sha=vm.guest(['sha256sum',HELPER]).stdout.split()[0]
    assert witness['helper_sha256']==actual_helper_sha
    receipt_identity=json.loads(python("import os,json;s=os.stat('/srv/fogell/state/storage-pool-receipts');print(json.dumps({'device':s.st_dev,'inode':s.st_ino}))"))
    assert witness['fd_identity']==receipt_identity
    sealed=receipts();assert len(sealed)==1
    rec=next(iter(sealed.values()))['record'];assert rec['old_record_sha256']==sha and rec['writers_extinct_attested']=='true'
    assert_state(snapshot(),True,before)
    record('receipt_fsync_checkpoint',cycle=number,witness=witness,receipts=sealed,state=snapshot())
    vm.stop(f'receipt-before-clear-{number}',abrupt=True)
    vm.boot(f'receipt-reboot-{number}');capture_boot(f'receipt-reboot-{number}')
    assert_state(snapshot(),True,before);assert receipts()==sealed
    controller(True);api.ready(expect=503);no_start(queued)
    record('receipt_reboot_refused',cycle=number,state=snapshot(),receipts=receipts(),readiness=503,no_runner=True)
    controller(False)
    result=json.loads(helper(sha,f'complete-cycle-{number}').stdout);assert result['status']=='recovered'
    assert_state(snapshot(),False)
    complete=receipts();assert len(complete)==2
    assert all(x['record']['old_record_sha256']==sha for x in complete.values())
    record('explicit_recovery',cycle=number,old_sha256=sha,receipts=complete,state=snapshot(),old_writers_extinct=True)
    vm.stop(f'after-clear-{number}',abrupt=True)
    vm.boot(f'idle-reboot-{number}');capture_boot(f'idle-reboot-{number}')
    assert_state(snapshot(),False);assert receipts()==complete
    controller(True);api.ready();succeeds(queued)
    record('recovered_idle_reboot',cycle=number,queued_build=queued,status='success',state=snapshot(),receipts=complete)

def main():
    token=vm.guest(['sudo','cat',STATE+'/api-token']).stdout.strip();(vm.ROOT/'token').write_text(token);(vm.ROOT/'token').chmod(0o600)
    api.sql(f"INSERT INTO organizations(id,slug) VALUES('{api.ORG}','persistence-verified'); INSERT INTO projects(id,organization_id,slug) VALUES('{api.PROJECT}','{api.ORG}','persistence-verified');")
    topology=vm.guest(['sudo','findmnt','--json','-o','TARGET,SOURCE,FSTYPE,OPTIONS,UUID']).stdout
    (vm.ROOT/'mounts.json').write_text(topology)
    params=api.sql('SHOW fsync; SHOW synchronous_commit; SHOW full_page_writes;');assert params.splitlines()==['on','on','on'],params
    record('durable_topology',postgres_parameters=params,kernel=vm.guest(['uname','-r']).stdout.strip(),pool=api.pool_stats())
    controller(True);control('baseline_control')
    pressure('bytes');pressure('inodes')
    control('before_clean_shutdown');controller(False)
    vm.stop('clean-control');vm.boot('clean-control-reboot');capture_boot('clean-control-reboot')
    assert_state(snapshot(),False);controller(True);control('clean_reboot_control')
    for n in range(1,4):cycle(n)
    controller(False);assert_state(snapshot(),False)
    record('campaign_pass',active_cuts=3,receipt_before_clear_cuts=3,after_clear_cuts=3,clean_reboots=1)
    vm.stop('campaign-complete')

if __name__=='__main__':main()
