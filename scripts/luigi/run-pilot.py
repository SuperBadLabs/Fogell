#!/usr/bin/env python3
"""Supervise a prepared Luigi smoke/pilot/qualification with bounded guards.

Only the validated private Fogell unit/container can be stopped on a guard or
supervisor failure. Portable --self-test uses fake commands and temporary files;
it never contacts Luigi. Do not replace an actively running campaign's copy.
"""
import argparse
import json
import os
from pathlib import Path
import re
import selectors
import signal
import stat
import subprocess
import tempfile
import time
import uuid

MAX_BYTES = 2147483648
MIN_FREE = 21474836480


def require(value, reason):
    if not value:
        raise ValueError(reason)


def validate_config(config, root):
    require(config['service_unit'] == 'fogell.service', 'unexpected controller service')
    require(config['deployment_root'] == str(root) and config['state_root'] == str(root/'state'), 'unexpected deployment paths')
    require(config['url'] == 'http://127.0.0.1:46206', 'unexpected URL')
    ids = [config[k] for k in ('controller_container_id', 'postgres_container_id')]
    require(all(re.fullmatch('[a-f0-9]{64}', x) for x in ids) and ids[0] != ids[1], 'immutable distinct container IDs required')
    for key in ('organization', 'project'):
        require(str(uuid.UUID(config[key])) == config[key], 'noncanonical tenant identity')


def validate_command(argv, config, phase):
    require(isinstance(argv, list) and all(isinstance(x, str) for x in argv), 'command must be argument vector')
    require(argv[:5] == ['podman', 'exec', config['controller_container_id'], 'python3', '/app/scripts/prove-self-hosted-pilot.py'],
            'pilot command does not own exact container and harness')
    require(len(argv[5:]) % 2 == 0, 'unpaired pilot option')
    pairs = list(zip(argv[5::2], argv[6::2]))
    require(len({k for k, _ in pairs}) == len(pairs), 'duplicate pilot option')
    options = dict(pairs)
    fixed = {'--url': config['url'], '--organization': config['organization'], '--project': config['project'],
             '--token-file': '/run/fogell/token', '--cache': '/tmp/fogell-pilot-luigi/packages',
             '--state-root': '/data', '--deployment-declaration': '/campaign/deployment.json',
             '--retention-command': '/campaign/retention-command.json',
             '--work': '/campaign/'+phase+'-work', '--output': '/campaign/'+phase}
    require(set(options) == set(fixed) | {'--loops', '--tool-sha256'}, 'unknown or missing pilot option')
    require(all(options[k] == v for k, v in fixed.items()), 'pilot command scope differs')
    require(re.fullmatch('[a-f0-9]{64}', options['--tool-sha256']), 'tool closure must be pinned')
    require(re.fullmatch('[0-9]+', options['--loops']), 'invalid loop count')
    loops = int(options['--loops'])
    require((phase == 'smoke' and loops == 1) or (phase == 'pilot' and loops == 100)
            or (phase == 'qualification' and 30 <= loops <= 100), 'phase loop count differs')
    return loops


def bounded_command(argv, timeout=10):
    require(0 < timeout <= 45, 'invalid operation deadline')
    origin = time.monotonic()
    output, errors = bytearray(), bytearray()
    with subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True) as child:
        try:
            with selectors.DefaultSelector() as selector:
                for stream, destination in ((child.stdout, output), (child.stderr, errors)):
                    os.set_blocking(stream.fileno(), False)
                    selector.register(stream, selectors.EVENT_READ, destination)
                while selector.get_map():
                    require(time.monotonic()-origin < timeout, 'owned operation deadline')
                    for selected, _ in selector.select(.05):
                        chunk = os.read(selected.fileobj.fileno(), 16384)
                        if not chunk:
                            selector.unregister(selected.fileobj)
                        else:
                            selected.data.extend(chunk)
                            require(len(output)+len(errors) <= 65536, 'operation response exceeds bound')
                child.wait(timeout=max(.01, timeout-(time.monotonic()-origin)))
        finally:
            if child.returncode is None:
                try: os.killpg(child.pid, signal.SIGKILL)
                except ProcessLookupError: pass
                child.wait(timeout=5)
        require(child.returncode == 0, 'owned operation failed: '+Path(argv[0]).name)
    return output.decode().strip()


def verify_unit(config, command=bounded_command):
    value = command(['systemctl', '--user', 'show', config['service_unit'], '--property=ExecStart', '--value'], timeout=10)
    require('podman start' in value and config['controller_container_id'] in value, 'unit does not own exact controller')


def storage_sample(root, *, max_entries=100000, max_seconds=5, max_bytes=MAX_BYTES, min_free=MIN_FREE, filesystem=os.statvfs):
    started = time.monotonic()
    size = count = 0
    def scan(fd, depth):
        nonlocal size, count
        require(depth <= 128, 'state directory depth bound')
        with os.scandir(fd) as entries:
            for entry in entries:
                count += 1
                require(count <= max_entries, 'state entry inventory bound')
                require(time.monotonic()-started < max_seconds, 'state inventory deadline')
                try:
                    info = entry.stat(follow_symlinks=False)
                    if stat.S_ISDIR(info.st_mode):
                        child = os.open(entry.name, os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW, dir_fd=fd)
                        try: scan(child, depth+1)
                        finally: os.close(child)
                    elif stat.S_ISREG(info.st_mode):
                        size += info.st_size
                except FileNotFoundError:
                    continue
                require(size < max_bytes, 'state content bytes guard exceeded')
    fd = os.open(root/'state', os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    try: scan(fd, 0)
    finally: os.close(fd)
    fs = filesystem(root)
    free = fs.f_bavail * fs.f_frsize
    require(free > min_free and fs.f_favail > 10000, 'host storage reserve guard exceeded')
    return {'state_content_bytes': size, 'state_entry_count': count,
            'host_free_bytes': free, 'host_free_inodes': fs.f_favail}


def cleanup_owned(child, config, command=bounded_command):
    """Every cleanup stage is bounded and errors never skip the next stage."""
    errors = []
    if child is None or child.poll() is not None:
        return errors
    try:
        verify_unit(config, command)
        command(['systemctl', '--user', 'stop', config['service_unit']], timeout=45)
    except Exception as exc:
        errors.append({'action': 'stop_unit', 'error_type': type(exc).__name__})
        try:
            # Safe fallback is the prevalidated immutable owned container, never
            # a different unit or host-wide process signal.
            command(['podman', 'stop', '--time', '10', config['controller_container_id']], timeout=20)
        except Exception as fallback:
            errors.append({'action': 'stop_owned_container', 'error_type': type(fallback).__name__})
    try:
        child.terminate()
    except (OSError, ProcessLookupError):
        pass
    try:
        child.wait(timeout=5)
    except subprocess.TimeoutExpired:
        try:
            child.kill()
            child.wait(timeout=5)
        except Exception as exc:
            errors.append({'action': 'reap_owned_client', 'error_type': type(exc).__name__})
    except Exception as exc:
        errors.append({'action': 'reap_owned_client', 'error_type': type(exc).__name__})
    return errors


def atomic_receipt(path, value):
    temporary = path.with_suffix('.tmp')
    with temporary.open('w') as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def self_test():
    from types import SimpleNamespace
    controls = []
    with tempfile.TemporaryDirectory(prefix='fogell-supervisor-check-') as directory:
        root = Path(directory)
        (root/'state').mkdir()
        config = {'service_unit':'fogell.service', 'deployment_root':str(root), 'state_root':str(root/'state'),
                  'url':'http://127.0.0.1:46206', 'controller_container_id':'a'*64, 'postgres_container_id':'b'*64,
                  'organization':str(uuid.uuid4()), 'project':str(uuid.uuid4())}
        validate_config(config, root)
        for key, value in [('service_unit','jenkins.service'), ('controller_container_id','short-name'), ('state_root','/etc')]:
            try: validate_config(dict(config, **{key:value}), root)
            except ValueError: controls.append('reject_'+key)
            else: raise AssertionError('unsafe configuration accepted')
        fixed = {'--url':config['url'],'--organization':config['organization'],'--project':config['project'],
                 '--token-file':'/run/fogell/token','--cache':'/tmp/fogell-pilot-luigi/packages',
                 '--state-root':'/data','--deployment-declaration':'/campaign/deployment.json',
                 '--retention-command':'/campaign/retention-command.json','--work':'/campaign/qualification-work',
                 '--output':'/campaign/qualification','--tool-sha256':'c'*64,'--loops':'30'}
        argv = ['podman','exec',config['controller_container_id'],'python3','/app/scripts/prove-self-hosted-pilot.py']
        for key,value in fixed.items(): argv.extend([key,value])
        require(validate_command(argv,config,'qualification') == 30, 'qualification refused')
        mutations = [argv+['--loops','30'], argv+['--dangerous','true'], list(argv)]
        mutations[-1][2] = 'd'*64
        for index, bad in enumerate(mutations):
            try: validate_command(bad,config,'qualification')
            except ValueError: controls.append('reject_command_'+str(index))
            else: raise AssertionError('unsafe command accepted')
        try: verify_unit(config, lambda *a, **k:'podman start '+('c'*64))
        except ValueError: controls.append('reject_rebound_unit')
        else: raise AssertionError('rebound unit accepted')
        fs = lambda root: SimpleNamespace(f_bavail=100, f_frsize=1, f_favail=20000)
        (root/'state/file').write_bytes(b'abcd')
        (root/'outside').write_bytes(b'x'*1000)
        (root/'state/link').symlink_to(root/'outside')
        require(storage_sample(root, min_free=0, filesystem=fs)['state_content_bytes'] == 4, 'symlink target counted')
        controls.append('nested_symlink_not_followed')
        for kwargs in ({'max_entries':1}, {'max_bytes':4}, {'min_free':100}, {'max_seconds':0}):
            try: storage_sample(root, filesystem=fs, **({'min_free':0}|kwargs))
            except ValueError: controls.append('guard_reject_'+next(iter(kwargs)))
            else: raise AssertionError('guard accepted known-bad inventory')
        calls = []
        def fake_command(argv, timeout=10):
            calls.append((argv, timeout))
            if argv[:3] == ['systemctl','--user','show']:
                return 'podman start '+config['controller_container_id']
            if argv[:3] == ['systemctl','--user','stop']:
                raise subprocess.TimeoutExpired(argv, timeout)
            return ''
        class FakeChild:
            def __init__(self): self.actions = []
            def poll(self): return None
            def terminate(self): self.actions.append('terminate')
            def kill(self): self.actions.append('kill')
            def wait(self, timeout):
                self.actions.append(('wait',timeout))
                if self.actions.count(('wait',5)) == 1:
                    raise subprocess.TimeoutExpired(['owned-client'], timeout)
                return -9
        child = FakeChild()
        errors = cleanup_owned(child, config, fake_command)
        require(errors and 'kill' in child.actions and any(a[:2]==['podman','stop'] for a, _ in calls), 'cleanup skipped after stop timeout')
        require(all(t <= 45 for _, t in calls), 'cleanup command unbounded')
        controls.append('stop_timeout_still_reaps_and_records')
        import sys
        for code, budget, expected in [('import time;time.sleep(5)',.05,'deadline'),
                                       ('import os;os.write(1,b"x"*131072)',2,'exceeds bound')]:
            try: bounded_command([sys.executable,'-c',code], timeout=budget)
            except ValueError as exc:
                require(expected in str(exc), 'bounded child failed for wrong reason')
                controls.append('bounded_command_'+expected.replace(' ','_'))
            else: raise AssertionError('unbounded command accepted')
        atomic_receipt(root/'receipt.json', {'passed':False,'cleanup_errors':errors})
        require(json.loads((root/'receipt.json').read_text())['cleanup_errors'], 'failure receipt lost')
        controls.append('cleanup_failure_receipt_retained')
    print(json.dumps({'passed':True,'controls':controls}))
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--phase', choices=['smoke','pilot','qualification'])
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test: return self_test()
    require(args.phase is not None, 'phase required')
    os.umask(0o077)
    root = Path.home()/'services/fogell'
    config = json.loads((root/'deployment.json').read_text())
    validate_config(config, root)
    output = root/'campaigns'
    argv = json.loads((output/(args.phase+'-command.json')).read_text())
    loops = validate_command(argv, config, args.phase)
    receipt_path = output/(args.phase+'-supervisor.json')
    require(not (output/args.phase).exists() and not receipt_path.exists(), 'campaign output already exists')
    started = time.monotonic()
    receipt = {'schema_version':1,'passed':False,'phase':args.phase,'loops':loops,'interval_seconds':5,
               'max_state_bytes':MAX_BYTES,'min_host_free_bytes':MIN_FREE,'max_seconds':5480,
               'limits_are_guardrails_not_disk_quotas':True,'samples':0,'cleanup_errors':[]}
    child = None
    def interrupted(signum, frame): raise InterruptedError('campaign supervisor interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    def sample():
        row = storage_sample(root)
        row['elapsed_seconds'] = time.monotonic()-started
        states = {}
        for label,key in [('controller','controller_container_id'),('postgres','postgres_container_id')]:
            fields = bounded_command(['podman','inspect','--format','{{.Id}} {{.State.Pid}}',config[key]],timeout=5).split()
            require(len(fields)==2 and fields[0]==config[key] and int(fields[1])>0, 'owned container absent')
            path = next(line.split(':',2)[2] for line in Path('/proc/'+fields[1]+'/cgroup').read_text().splitlines() if line.startswith('0::'))
            require('..' not in Path(path).parts, 'unexpected cgroup path')
            cg = Path('/sys/fs/cgroup')/path.lstrip('/')
            states[label] = {k:(cg/k).read_text().strip() for k in ['memory.current','memory.peak','memory.events','cpu.stat','pids.current']}
            states[label].update(container_id=fields[0],host_pid=int(fields[1]))
        row['cgroups'] = states
        return row
    try:
        verify_unit(config)
        receipt['prelaunch_guard'] = sample()
        atomic_receipt(receipt_path,receipt)
        with (output/(args.phase+'.stdout')).open('xb') as log, (output/(args.phase+'-resources.ndjson')).open('x') as samples:
            child = subprocess.Popen(argv,stdout=log,stderr=subprocess.STDOUT)
            while True:
                require(time.monotonic()-started < 5480, 'outer campaign deadline')
                row = sample()
                samples.write(json.dumps(row)+'\n'); samples.flush(); receipt['samples'] += 1
                require(os.fstat(log.fileno()).st_size <= 64*1024**2, 'supervisor log exceeds64MiB')
                code = child.poll()
                if code is not None: break
                time.sleep(5)
            receipt['exit_code'] = code
            require(code==0,'pilot process failed')
            measured = json.loads((output/args.phase/'receipt.json').read_text())
            require(measured['passed'] is True and len(measured['runs']) == 2*loops, 'pilot receipt incomplete or failed')
            receipt['passed'] = True
    except BaseException as exc:
        receipt['error'] = type(exc).__name__+':'+str(exc)
    finally:
        try:
            receipt['cleanup_errors'] = cleanup_owned(child,config)
        except BaseException as exc:
            receipt['cleanup_errors'].append({'action':'cleanup_interrupted','error_type':type(exc).__name__})
        finally:
            if receipt['cleanup_errors']: receipt['passed'] = False
            receipt['elapsed_seconds'] = time.monotonic()-started
            atomic_receipt(receipt_path,receipt)
    print(json.dumps({'passed':receipt['passed'],'receipt':str(receipt_path)}))
    return 0 if receipt['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
