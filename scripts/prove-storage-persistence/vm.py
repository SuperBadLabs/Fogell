"""Owned rootless KVM guest for Fogell persistence evidence; run on Luigi."""
import json, os, pathlib, shlex, subprocess, time, uuid
ROOT = pathlib.Path(os.environ.get('FOGELL_PERSISTENCE_ROOT', '/tmp/fogell-persistence-20260916'))
IMAGE = 'localhost/fogell-persistence-qemu:20260916'
NAME = 'fogell-persistence-vm-20260916'
SSH_PORT = 19422
API_PORT = 19443
DISKS = ('os.qcow2', 'pool.raw', 'state.raw', 'postgres.raw')
OWNED_ID = None

def valid_id(value):
    return isinstance(value, str) and len(value) == 64 and all(c in '0123456789abcdef' for c in value)

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

def require_owned(container_id=None):
    """Return a captured harness ID; never route guest work through NAME."""
    ident = container_id if container_id is not None else OWNED_ID
    assert valid_id(ident) and ident == OWNED_ID, 'the active owned VM ID is required'
    return ident


def ssh_args(container_id=None):
    return ['podman','exec','-i',require_owned(container_id),'ssh','-i','/vm/guest-key','-p','2222','-o','BatchMode=yes','-o','ConnectTimeout=3','-o','StrictHostKeyChecking=accept-new','-o','UserKnownHostsFile=/vm/known-hosts-inside','ubuntu@127.0.0.1']

def guest(args, check=True, timeout=60, input=None, container_id=None):
    return run(ssh_args(container_id)+[shlex.join(args)],check=check,timeout=timeout,input=input)

def copy_to_guest(source, target, container_id=None):
    relative=pathlib.Path(source).relative_to(ROOT)
    return run(['podman','exec','-i',require_owned(container_id),'scp','-i','/vm/guest-key','-P','2222','-o','BatchMode=yes','-o','StrictHostKeyChecking=accept-new','-o','UserKnownHostsFile=/vm/known-hosts-inside','/vm/'+str(relative),'ubuntu@127.0.0.1:'+target],timeout=180)

def inspect(container_id=None):
    r=run(['podman','inspect',container_id or NAME],check=False)
    return None if r.returncode else json.loads(r.stdout)[0]

def disk_identity():
    return {name:{'device':(ROOT/name).stat().st_dev,'inode':(ROOT/name).stat().st_ino} for name in DISKS}


def owned_id(container):
    if not isinstance(container, dict):
        return None
    ident = container.get('Id')
    config = container.get('Config')
    if not isinstance(config, dict):
        return None
    labels = config.get('Labels')
    if labels is None:
        labels = {}
    if not isinstance(labels, dict):
        return None
    owner = labels.get('io.fogell.persistence.boot')
    if (
        valid_id(ident)
        and isinstance(owner, str) and len(owner) == 32 and all(c in '0123456789abcdef' for c in owner)
    ):
        return ident
    return None


def register_owned(container_id):
    global OWNED_ID
    assert valid_id(container_id), 'invalid owned container ID'
    OWNED_ID = container_id
    return container_id


def active_owned_id():
    return OWNED_ID


def adopt_named_owned():
    """Adopt only the current fixed-name container if it has our boot label."""
    global OWNED_ID
    OWNED_ID = None
    candidate = inspect()
    ident = owned_id(candidate)
    return register_owned(ident) if ident is not None else None


def clear_owned(container_id):
    global OWNED_ID
    if OWNED_ID == container_id:
        OWNED_ID = None


def remove_owned(container_id, failure=None):
    try:
        run(['podman','rm','--force',container_id])
    except Exception as cleanup_error:
        if failure is None:
            raise
        failure.add_note('Owned VM cleanup also failed: '+str(cleanup_error))
    else:
        clear_owned(container_id)


def cleanup_active_owned(failure=None):
    if OWNED_ID is not None:
        cleanup_owned(OWNED_ID, failure)


def cleanup_owned(container_id, failure=None):
    """Remove this exact owned ID, never a current fixed-name replacement."""
    assert valid_id(container_id), 'invalid owned container ID'
    candidate = inspect(container_id)
    if candidate is None:
        clear_owned(container_id)
        return
    if owned_id(candidate) != container_id:
        clear_owned(container_id)
        if failure is not None:
            failure.add_note('Owned VM ID was replaced by an unowned container; it was not removed')
        return
    remove_owned(container_id, failure)

def boot(label, restricted=True):
    assert inspect() is None, 'test VM container name already exists'
    owner=uuid.uuid4().hex
    serial='serial-'+label+'.log'
    qemu=['qemu-system-x86_64','-accel','kvm','-cpu','host','-smp','2','-m','4096','-display','none','-monitor','none','-serial','file:/vm/'+serial,'-no-reboot']
    for name in DISKS:
        fmt='qcow2' if name.endswith('qcow2') else 'raw'
        qemu += ['-drive',f'file=/vm/{name},format={fmt},if=virtio,cache=none,aio=native,discard=ignore']
    qemu += ['-cdrom','/vm/seed.iso','-netdev','user,id=n1,net=10.77.0.0/24,restrict='+('on' if restricted else 'off')+',hostfwd=tcp:0.0.0.0:2222-:22,hostfwd=tcp:0.0.0.0:8080-:8080','-device','virtio-net-pci,netdev=n1']
    args=['podman','run','-d','--name',NAME,'--label','io.fogell.persistence.boot='+owner,'--userns=keep-id','--user','1000:1000','--group-add','keep-groups','--device','/dev/kvm','--cap-drop=all','--security-opt=no-new-privileges','--memory=6g','--cpus=4','--pids-limit=128','--network=slirp4netns','-v',str(ROOT)+':/vm:rw',IMAGE]+qemu
    ident=None
    try:
        started=run(args).stdout.strip()
        assert len(started)==64 and all(c in '0123456789abcdef' for c in started), 'invalid created container ID'
        ident=register_owned(started)
        record('vm_start',label=label,container_id=ident,qemu_args=qemu,disks=disk_identity())
        deadline=time.monotonic()+150
        while time.monotonic()<deadline:
            r=guest(['cat','/proc/sys/kernel/random/boot_id'],check=False,timeout=6,container_id=ident)
            if r.returncode==0:
                current=inspect(ident)
                assert current and current['State']['Running'], 'QEMU stopped before SSH'
                record('guest_boot',label=label,boot_id=r.stdout.strip(),qemu_host_pid=current['State']['Pid'])
                return r.stdout.strip()
            state=inspect(ident)
            assert state and state['State']['Running'],'QEMU stopped before SSH'
            time.sleep(1)
        raise TimeoutError('guest SSH did not become ready')
    except BaseException as failure:
        # A successful run gives the immutable ID of our own container. A failed
        # run can still have created one; the fresh label distinguishes that case
        # from an unrelated container winning the fixed-name race.
        if ident is None:
            try:
                candidate=inspect()
                if owned_id(candidate) and candidate['Config']['Labels']['io.fogell.persistence.boot']==owner:
                    ident=register_owned(candidate['Id'])
            except Exception as lookup_error:
                failure.add_note('Could not identify the failed boot container: '+str(lookup_error))
        if ident is not None:
            remove_owned(ident, failure)
        raise


def stop(label, abrupt=False, container_id=None):
    ident = require_owned(container_id)
    before=inspect(ident)
    if before is None:
        clear_owned(ident)
        return
    observed = owned_id(before)
    assert observed == ident, 'refusing a container whose ID differs from the owned target'
    register_owned(ident)
    failure=None
    try:
        assert before['State']['Running'], 'owned VM was already stopped'
        identities=disk_identity()
        if abrupt: run(['podman','kill','--signal','KILL',ident])
        else: guest(['sudo','poweroff'],check=False,timeout=10,container_id=ident)
        deadline=time.monotonic()+60
        while time.monotonic()<deadline:
            after=inspect(ident)
            assert after is not None, 'owned VM disappeared during shutdown'
            if not after['State']['Running']:break
            time.sleep(.2)
        else:raise TimeoutError('QEMU did not exit')
        if abrupt:assert after['State']['ExitCode']==137,after['State']
        else:assert after['State']['ExitCode']==0,after['State']
        record('vm_power_cut' if abrupt else 'vm_clean_shutdown',label=label,container_id=ident,old_qemu_host_pid=before['State']['Pid'],exit_code=after['State']['ExitCode'],disk_identities_unchanged=identities==disk_identity())
        assert identities==disk_identity()
    except BaseException as error:
        failure=error
        raise
    finally:
        remove_owned(ident, failure)

if __name__=='__main__':
    import sys
    if sys.argv[1]=='boot':boot(sys.argv[2],restricted='--provision' not in sys.argv)
    elif sys.argv[1]=='stop':
        assert adopt_named_owned() is not None, 'stop requires an active harness-owned VM'
        stop(sys.argv[2],abrupt='--kill' in sys.argv)
    else:raise SystemExit('boot LABEL [--provision] | stop LABEL [--kill]')
