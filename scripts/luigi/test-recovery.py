#!/usr/bin/env python3
"""Portable recovery-helper safety controls. Never contacts a deployment."""
import importlib.util
import ast
import json
import os
from pathlib import Path
import stat
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('luigi_recovery', Path(__file__).with_name('recovery.py'))
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)

TARGET = 'fogell_luigi_restore_test'
SOURCE = ('FOGELL_DATABASE_URL=Host=127.0.0.1;Port=34567;Username=fogell_runtime;Password=local_test_only;Database=fogell\n'
          'FOGELL_MAINTENANCE_DATABASE_URL=Host=127.0.0.1;Port=34567;Username=fogell;Password=local_test_only;Database=fogell\n'
          'UNCHANGED=value\n')


class EnvironmentTests(unittest.TestCase):
    def test_owned_target_rewrites_only_database(self):
        changed = recovery.target_environment(SOURCE, TARGET, expected_port=34567)
        self.assertEqual(changed.count('Database='+TARGET), 2)
        self.assertEqual(changed.replace(TARGET, 'fogell'), SOURCE)

    def test_unsafe_target_names_refused(self):
        for name in ('fogell', 'postgres', '../other', 'fogell_luigi_restore_ok;Database=other', 'fogell_luigi_restore_X'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                recovery.target_environment(SOURCE, name)

    def test_duplicate_missing_or_alias_configuration_refused(self):
        invalid = [SOURCE+SOURCE.splitlines()[0]+'\n', SOURCE.splitlines()[0]+'\n',
                   SOURCE.replace('Database=fogell', 'Database=fogell;database=other', 1),
                   SOURCE.replace('Database=fogell', 'Database=fogell;Initial Catalog=other', 1),
                   SOURCE.replace('Host=127.0.0.1', 'Host=127.0.0.1;host=other', 1),
                   SOURCE.replace('Password=local_test_only', 'Password="local_test_only"', 1)]
        for index, source in enumerate(invalid):
            with self.subTest(index=index), self.assertRaises(ValueError):
                recovery.target_environment(source, TARGET, expected_port=34567)

    def test_different_endpoint_or_role_refused(self):
        for old, new in [('Host=127.0.0.1','Host=remote.invalid'), ('Port=34567','Port=34568'),
                         ('Username=fogell_runtime','Username=fogell'), ('Username=fogell;','Username=other;'),
                         ('Database=fogell','Database=other')]:
            with self.subTest(new=new), self.assertRaises(ValueError):
                recovery.target_environment(SOURCE.replace(old,new,1), TARGET, expected_port=34567)


class PodmanInspectTests(unittest.TestCase):
    def rows(self):
        return [{'Name': '/' + name, 'Id': f'{index:064x}',
                 'State': {'StartedAt': '2026-09-23T01:02:03.123456789Z', 'Status': 'running'}}
                for index, name in enumerate(recovery.PROTECTED, start=1)]

    def projection(self, rows):
        # Exercise the frozen adapter's real nested function with a fake command;
        # avoid changing the deployed harness merely to expose a test interface.
        tree = ast.parse(Path(recovery.__file__).read_text())
        function = next(n for n in ast.walk(tree) if isinstance(n, ast.FunctionDef) and n.name == 'protected')
        def command(argv):
            self.assertEqual(argv, ['podman', 'inspect'] + recovery.PROTECTED)
            self.assertNotIn('--format', argv)  # No Go time/string template path.
            return json.dumps(rows).encode()
        namespace = {'command': command, 'json': json, 'require': recovery.require,
                     'PROTECTED': recovery.PROTECTED, 'ctl': 'a' * 64, 'pg': 'b' * 64}
        exec(compile(ast.Module(body=[function], type_ignores=[]), recovery.__file__, 'exec'), namespace)
        return namespace['protected']()

    def test_full_inspect_json_preserves_exact_rfc3339_timestamp(self):
        rows = json.loads(json.dumps(self.rows()))
        result = self.projection(list(reversed(rows)))
        self.assertEqual([r['name'] for r in result], recovery.PROTECTED)
        self.assertTrue(all(r['started_at'] == '2026-09-23T01:02:03.123456789Z' for r in result))
        changed = self.rows()
        changed[0]['State']['StartedAt'] = '2026-09-23T01:02:03.123456788Z'
        self.assertNotEqual(result, self.projection(changed))

    def test_missing_or_owned_protected_identity_is_refused(self):
        with self.assertRaises(ValueError):
            self.projection(self.rows()[:-1])
        rows = self.rows()
        rows[0]['Id'] = 'a' * 64
        with self.assertRaises(ValueError):
            self.projection(rows)


class FilesystemTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='fogell-recovery-check-')
        self.root = Path(self.temporary.name)
        self.state = self.root/'state'
        self.state.mkdir()
        self.tmp = self.state/'tmp'
        self.tmp.mkdir()
        self.good_space = SimpleNamespace(f_bavail=100*1024**3, f_frsize=1, f_favail=100000)

    def tearDown(self):
        self.temporary.cleanup()

    def fifo(self, name='clr-debug-pipe-123-456-in', parent=None):
        path = (parent or self.tmp)/name
        os.mkfifo(path,0o600)
        return path

    def guard(self):
        return recovery.guard(self.root,[self.state])

    def test_guard_records_content_and_is_not_quota(self):
        (self.state/'source').write_bytes(b'abcd')
        with patch.object(recovery.os,'statvfs',return_value=self.good_space):
            result = self.guard()
        self.assertEqual(result['content_bytes'],4)
        self.assertFalse(result['is_hard_quota'])

    def test_guard_refuses_symbolic_root_and_entry(self):
        outside = self.root/'outside'
        outside.mkdir()
        (outside/'keep').write_text('preserve')
        link = self.state/'link'
        link.symlink_to(outside,target_is_directory=True)
        with self.assertRaises(ValueError): self.guard()
        link.unlink()
        base = self.root/'linked-state'
        base.symlink_to(outside,target_is_directory=True)
        with self.assertRaises(ValueError): recovery.guard(self.root,[base])
        self.assertEqual((outside/'keep').read_text(),'preserve')

    def test_guard_rejects_byte_and_host_reserve_bounds(self):
        (self.state/'source').write_bytes(b'abcd')
        with patch.object(recovery,'MAX_BYTES',4), self.assertRaises(ValueError):
            self.guard()
        for free, inodes in ((recovery.MIN_FREE,100000),(100*1024**3,10000)):
            with self.subTest(free=free,inodes=inodes):
                fake=SimpleNamespace(f_bavail=free,f_frsize=1,f_favail=inodes)
                with patch.object(recovery.os,'statvfs',return_value=fake), self.assertRaises(ValueError):
                    self.guard()

    def test_guard_entry_cap_includes_directories(self):
        info=SimpleNamespace(st_mode=stat.S_IFDIR|0o700)
        with patch.object(recovery.os,'walk',return_value=[(str(self.state),['fake']*100001,[])]), \
             patch.object(Path,'lstat',return_value=info), self.assertRaises(ValueError):
            self.guard()

    def test_cleanup_preserves_regular_data_and_journals_fifo_removal(self):
        regular=self.tmp/'clr-debug-pipe-123-456-out'
        regular.write_text('ordinary data despite endpoint-shaped name')
        endpoint=self.fifo()
        journal=self.root/'cleanup.json'
        removed=recovery.cleanup_endpoints(self.state,journal)
        self.assertFalse(endpoint.exists())
        self.assertTrue(regular.exists())
        self.assertEqual(len(removed),1)
        receipt=json.loads(journal.read_text())
        self.assertEqual(receipt['completed'],['tmp/'+endpoint.name])
        self.assertIsNone(receipt['next'])
        self.assertEqual(stat.S_IMODE(journal.stat().st_mode),0o600)

    def test_later_unknown_fifo_refuses_before_any_deletion(self):
        endpoint=self.fifo()
        unknown=self.fifo('application.pipe')
        walk=[(str(self.tmp),[],[endpoint.name,unknown.name])]
        with patch.object(recovery.os,'walk',return_value=walk), self.assertRaises(ValueError):
            recovery.cleanup_endpoints(self.state)
        self.assertTrue(endpoint.exists())
        self.assertTrue(unknown.exists())

    def test_cleanup_requires_temporary_parent_and_refuses_links(self):
        outside=self.root/'keep'
        outside.write_text('preserve')
        link=self.tmp/'dotnet-diagnostic-123-456-socket'
        link.symlink_to(outside)
        with self.assertRaises(ValueError): recovery.cleanup_endpoints(self.state)
        self.assertTrue(link.is_symlink())
        self.assertEqual(outside.read_text(),'preserve')
        link.unlink()
        wrong=self.fifo(parent=self.state)
        with self.assertRaises(ValueError): recovery.cleanup_endpoints(self.state)
        self.assertTrue(wrong.exists())

    def test_cleanup_refuses_symlink_root(self):
        link=self.root/'linked-state'
        link.symlink_to(self.state,target_is_directory=True)
        endpoint=self.fifo()
        with self.assertRaises(ValueError): recovery.cleanup_endpoints(link)
        self.assertTrue(endpoint.exists())

    def test_identity_change_before_unlink_refused(self):
        endpoint=self.fifo()
        original=Path.lstat
        reads=0
        def changing(path,*args,**kwargs):
            nonlocal reads
            info=original(path,*args,**kwargs)
            if path==endpoint:
                reads+=1
                if reads>1:
                    return SimpleNamespace(st_dev=info.st_dev,st_ino=info.st_ino+1,st_mode=info.st_mode)
            return info
        with patch.object(Path,'lstat',new=changing), self.assertRaises(ValueError):
            recovery.cleanup_endpoints(self.state)
        self.assertTrue(endpoint.exists())

    def test_partial_unlink_failure_keeps_durable_progress(self):
        first=self.fifo()
        second=self.fifo('clr-debug-pipe-124-456-in')
        journal=self.root/'cleanup.json'
        original=Path.unlink
        def unlink(path,*args,**kwargs):
            if path==second: raise OSError('injected test-only unlink failure')
            return original(path,*args,**kwargs)
        walk=[(str(self.tmp),[],[first.name,second.name])]
        with patch.object(recovery.os,'walk',return_value=walk), patch.object(Path,'unlink',new=unlink), self.assertRaises(OSError):
            recovery.cleanup_endpoints(self.state,journal)
        receipt=json.loads(journal.read_text())
        self.assertEqual(receipt['completed'],['tmp/'+first.name])
        self.assertEqual(receipt['next'],'tmp/'+second.name)
        self.assertFalse(first.exists())
        self.assertTrue(second.exists())


if __name__=='__main__':
    unittest.main(verbosity=2)
