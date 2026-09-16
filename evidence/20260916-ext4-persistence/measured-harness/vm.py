"""Owned rootless KVM guest for Fogell persistence evidence; run on Luigi."""
import json, os, pathlib, shlex, subprocess, time
ROOT = pathlib.Path(os.environ.get('FOGELL_PERSISTENCE_ROOT', '/tmp/fogell-persistence-20260916'))
IMAGE = 'localhost/fogell-persistence-qemu:20260916'
NAME = 'fogell-persistence-vm-20260916'
SSH_PORT = 19422
API_PORT = 19443
DISKS = ('os.qcow2', 'pool.raw', 'state.raw', 'postgres.raw')

def run(args, check=True, timeout=60, input=None):
    r = subprocess.run(args, input=input, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    if check and r.returncode:
        raise RuntimeError(f'{args[0]} failed ({r.returncode}): {r.stderr[-1200:]}')
    return r

def record(test, **values):
    row = dict(test=test, time=time.time(), **values)
    with (ROOT/'vm-events.jsonl').open('a') as f: f.write(json.dumps(row,sort_keys=True)+'\n')
    print(json.dumps(row,sort_keys=True),flush=True)
    return row

def ssh_args():
    return ['podman','exec','-i',NAME,'ssh','-i','/vm/guest-key','-p','2222','-o','BatchMode=yes','-o','ConnectTimeout=3','-o','StrictHostKeyChecking=accept-new','-o','UserKnownHostsFile=/vm/known-hosts-inside','ubuntu@127.0.0.1']

def guest(args, check=True, timeout=60, input=None):
    return run(ssh_args()+[shlex.join(args)],check=check,timeout=timeout,input=input)

def copy_to_guest(source, target):
    relative=pathlib.Path(source).relative_to(ROOT)
    return run(['podman','exec','-i',NAME,'scp','-i','/vm/guest-key','-P','2222','-o','BatchMode=yes','-o','StrictHostKeyChecking=accept-new','-o','UserKnownHostsFile=/vm/known-hosts-inside','/vm/'+str(relative),'ubuntu@127.0.0.1:'+target],timeout=180)

def inspect():
    r=run(['podman','inspect',NAME],check=False)
    return None if r.returncode else json.loads(r.stdout)[0]

def disk_identity():
    return {name:{'device':(ROOT/name).stat().st_dev,'inode':(ROOT/name).stat().st_ino} for name in DISKS}

def boot(label, restricted=True):
    assert inspect() is None, 'test VM container name already exists'
    serial='serial-'+label+'.log'
    qemu=['qemu-system-x86_64','-accel','kvm','-cpu','host','-smp','2','-m','4096','-display','none','-monitor','none','-serial','file:/vm/'+serial,'-no-reboot']
    for name in DISKS:
        fmt='qcow2' if name.endswith('qcow2') else 'raw'
        qemu += ['-drive',f'file=/vm/{name},format={fmt},if=virtio,cache=none,aio=native,discard=ignore']
    qemu += ['-cdrom','/vm/seed.iso','-netdev','user,id=n1,net=10.77.0.0/24,restrict='+('on' if restricted else 'off')+',hostfwd=tcp:0.0.0.0:2222-:22,hostfwd=tcp:0.0.0.0:8080-:8080','-device','virtio-net-pci,netdev=n1']
    args=['podman','run','-d','--name',NAME,'--userns=keep-id','--user','1000:1000','--group-add','keep-groups','--device','/dev/kvm','--cap-drop=all','--security-opt=no-new-privileges','--memory=6g','--cpus=4','--pids-limit=128','--network=slirp4netns','-v',str(ROOT)+':/vm:rw',IMAGE]+qemu
    ident=run(args).stdout.strip()
    record('vm_start',label=label,container_id=ident,qemu_args=qemu,disks=disk_identity())
    deadline=time.monotonic()+150
    while time.monotonic()<deadline:
        r=guest(['cat','/proc/sys/kernel/random/boot_id'],check=False,timeout=6)
        if r.returncode==0:
            record('guest_boot',label=label,boot_id=r.stdout.strip(),qemu_host_pid=inspect()['State']['Pid'])
            return r.stdout.strip()
        state=inspect()
        assert state and state['State']['Running'],'QEMU stopped before SSH'
        time.sleep(1)
    raise TimeoutError('guest SSH did not become ready')

def stop(label, abrupt=False):
    before=inspect();assert before and before['State']['Running']
    identities=disk_identity()
    if abrupt: run(['podman','kill','--signal','KILL',NAME])
    else: guest(['sudo','poweroff'],check=False,timeout=10)
    deadline=time.monotonic()+60
    while time.monotonic()<deadline:
        after=inspect()
        if not after['State']['Running']:break
        time.sleep(.2)
    else:raise TimeoutError('QEMU did not exit')
    if abrupt:assert after['State']['ExitCode']==137,after['State']
    else:assert after['State']['ExitCode']==0,after['State']
    record('vm_power_cut' if abrupt else 'vm_clean_shutdown',label=label,container_id=before['Id'],old_qemu_host_pid=before['State']['Pid'],exit_code=after['State']['ExitCode'],disk_identities_unchanged=identities==disk_identity())
    assert identities==disk_identity()
    run(['podman','rm',NAME])

if __name__=='__main__':
    import sys
    if sys.argv[1]=='boot':boot(sys.argv[2],restricted='--provision' not in sys.argv)
    elif sys.argv[1]=='stop':stop(sys.argv[2],abrupt='--kill' in sys.argv)
    else:raise SystemExit('boot LABEL [--provision] | stop LABEL [--kill]')
