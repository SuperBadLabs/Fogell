#!/usr/bin/env python3
"""Independent native runner black-box checks for the Luigi campaign."""
import concurrent.futures
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

source, logs = map(Path, sys.argv[1:3])
runner = source/'tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host'
results = []

def document(steps, **extra):
    return dict(version=1, stages=[dict(name='verify',steps=steps)], **extra)

def case(name, definition, expected, contains=None, invalid=False, verify=None):
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='fogell-luigi-matrix-') as tmp:
        root = Path(tmp)
        (root/'pipeline.json').write_text(definition if isinstance(definition,str) else json.dumps(definition))
        env = dict(os.environ, FOGELL_CAMPAIGN_SECRET='must-not-inherit', CAMPAIGN_HOST_SECRET='must-not-inherit')
        try:
            run = subprocess.run([str(runner),str(root/'pipeline.json'),str(root/'workspace'),'build',str(root/'journal')],
                                 env=env,capture_output=True,timeout=15)
            output=(run.stdout+run.stderr).decode(errors='replace')
            (logs/(name+'.log')).write_text(output)
            assert run.returncode == expected, f'exit {run.returncode}, expected {expected}'
            if contains: assert contains in output, f'missing {contains!r}'
            if invalid:
                assert not (root/'workspace').exists() and not (root/'journal').exists(), 'invalid input created durable state'
            if verify: verify(root,output)
            result=dict(name=name,passed=True,seconds=round(time.monotonic()-started,3))
        except Exception as error:
            result=dict(name=name,passed=False,error=str(error),seconds=round(time.monotonic()-started,3))
        print(json.dumps(result),flush=True)
        results.append(result)

def absent(relative):
    def check(root,output): assert not (root/'workspace/build'/relative).exists(), f'unexpected effect {relative}'
    return check

case('literal',document([{'echo':'${HOME} remains literal π 😀'}]),0,'${HOME} remains literal π 😀')
case('environment-override',document([{'run':'test "$VALUE" = step','env':{'VALUE':'step'}}],env={'VALUE':'pipeline'}),0)
case('environment-no-inheritance',document([{'run':'test -z "$CAMPAIGN_HOST_SECRET$FOGELL_CAMPAIGN_SECRET"'}]),0)
case('environment-process-local',document([{'run':'export LOCAL_ONLY=yes'},{'run':'test -z "$LOCAL_ONLY"'}]),0)
case('working-directory',document([{'run':'mkdir sub'},{'run':'printf worked > file','working_directory':'sub'},{'archive':'sub/file'}]),0)
case('python-shebang',document([{'run':'#!/usr/bin/python3\nprint("python-interpreter")'}]),0,'python-interpreter')
case('stderr',document([{'run':'printf stderr-marker >&2'}]),0,'stderr-marker')
case('exit-seven',document([{'run':'exit 7'}]),1,'"exit_code":7')
case('failure-skip',document([{'run':'exit 7'},{'run':'touch forbidden'},{'echo':'always-marker','always':True}]),1,'always-marker',verify=absent('forbidden'))
case('cross-stage-skip',{'version':1,'stages':[{'name':'first','steps':[{'run':'exit 1'}]},{'name':'second','steps':[{'run':'touch forbidden'},{'echo':'always-marker','always':True}]}]},1,'always-marker',verify=absent('forbidden'))
case('timeout-cleanup',document([{'run':'sleep 30','timeout_seconds':1},{'echo':'after-timeout','always':True}]),1,'after-timeout')
case('archive-missing',document([{'archive':'absent.txt'}]),1)
case('archive-traversal',document([{'archive':'../*'}]),1)
case('archive-symlink',document([{'run':'ln -s /etc/hostname escape'},{'archive':'escape'}]),2,'RUN_FAILED')
case('directory-symlink',document([{'run':'ln -s /tmp escape'},{'run':'true','working_directory':'escape'}]),2,'RUN_FAILED')
case('directory-missing',document([{'run':'true','working_directory':'absent'}]),2,'RUN_FAILED')
case('test-report-pass',document([{'run':"printf '<testsuite><testcase name=\"ok\" classname=\"tests\"/></testsuite>' > report.xml"},{'test_report':'report.xml'}]),0)
case('test-report-fail',document([{'run':"printf '<testsuite><testcase name=\"bad\" classname=\"tests\"><failure message=\"broken\"/></testcase></testsuite>' > report.xml"},{'test_report':'report.xml'},{'echo':'continued-after-unstable'}]),1,'continued-after-unstable')
case('test-report-missing',document([{'test_report':'absent.xml'}]),1)
case('test-report-malformed',document([{'run':"printf '<testsuite>' > report.xml"},{'test_report':'report.xml'},{'echo':'cleanup-after-malformed-report','always':True}]),2,'cleanup-after-malformed-report')
case('output-overflow',document([{'run':"head -c 18000000 /dev/zero | tr '\\0' x"}]),2,'OUTPUT_LIMIT_EXCEEDED')

invalids={
 'empty':'','null':'null','array':'[]','malformed':'{',
 'unsupported-version':document([{'echo':'x'}])|{'version':2},
 'version-string':document([{'echo':'x'}])|{'version':'1'},
 'root-unknown':document([{'echo':'x'}])|{'unexpected':True},
 'duplicate-key':'{"version":1,"version":1,"stages":[]}',
 'no-stages':{'version':1,'stages':[]},
 'no-steps':document([]),'double-operation':document([{'run':'true','echo':'x'}]),
 'empty-command':document([{'run':' '}]),'unknown-operation':document([{'sh':'true'}]),
 'timeout-zero':document([{'run':'true','timeout_seconds':0}]),
 'timeout-too-large':document([{'run':'true','timeout_seconds':86401}]),
 'timeout-string':document([{'run':'true','timeout_seconds':'1'}]),
 'timeout-float':document([{'run':'true','timeout_seconds':1.5}]),
 'always-string':document([{'echo':'x','always':'true'}]),
 'path-absolute':document([{'run':'true','working_directory':'/tmp'}]),
 'path-traversal':document([{'run':'true','working_directory':'a/../b'}]),
 'env-reserved':document([{'echo':'x'}],env={'FOGELL_SECRET':'x'}),
 'env-numeric':document([{'echo':'x'}],env={'VALUE':1}),
 'env-invalid':document([{'echo':'x'}],env={'BAD-NAME':'x'}),
 'env-newline':document([{'echo':'x'}],env={'NAME\n':'x'}),
 'string-nul':document([{'echo':'\u0000'}]),
 'scalar-limit':document([{'echo':'x'*16385}]),
 'collection-limit':document([{'echo':'x'}]*4097),
 'source-limit':' '*262145,
 'depth-limit':'['*18+'0'+']'*18,
 'duplicate-stage':{'version':1,'stages':[{'name':'same','steps':[{'echo':'x'}]}]*2},
}
for name,value in invalids.items():
    case('admission-'+name,value,2,invalid=True)
for i in range(40):
    # Deterministic invalid-field fuzzing must fail admission before side effects.
    case(f'unknown-field-{i:02}',document([{'run':'touch forbidden',f'unknown_{i}':{'nested':['π',i,False]}}]),2,invalid=True)
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    list(pool.map(lambda i:case(f'concurrent-run-{i:02}',document([{'run':f'printf case-{i} > output; cat output'},{'archive':'output'}]),0,f'case-{i}'),range(24)))
(logs/'matrix-results.json').write_text(json.dumps(results,indent=2)+'\n')
print(f'MATRIX {sum(r["passed"] for r in results)}/{len(results)} passed',flush=True)
sys.exit(0 if all(r['passed'] for r in results) else 1)
