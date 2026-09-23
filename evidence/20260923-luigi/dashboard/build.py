#!/usr/bin/env python3
"""Build the standalone dashboard from hash-verified, retained campaign evidence."""
from pathlib import Path
import hashlib
import json
import math
import tarfile

HERE = Path(__file__).resolve().parent
BASE = HERE.parent
summary_bytes = (BASE / 'summary.json').read_bytes()
s = json.loads(summary_bytes)
m = json.loads((BASE / 'manifest.json').read_text())
archive = BASE / 'campaigns.tgz'
assert s['passed'] and hashlib.sha256(archive.read_bytes()).hexdigest() == m['archive_sha256']
with tarfile.open(archive) as tar:
    def raw(name):
        data = tar.extractfile(name).read()
        assert hashlib.sha256(data).hexdigest() == m['files'][name]['sha256']
        assert len(data) == m['files'][name]['bytes']
        return data
    versions = {}
    for key, folder, version in [('baseline_v1', 'pilot', 'v1'), ('qualification_v3', 'qualification', 'v3')]:
        receipt = json.loads(raw(f'campaigns/{folder}/receipt.json'))
        loops = [n / 1000 for n in receipt['loop_durations_ms']]
        diagnostics = [r['first_actionable_diagnostic_ms'] / 1000 for r in receipt['runs'][::2]]
        assert abs(sorted(loops)[math.ceil(.95 * len(loops)) - 1] - s[key]['loop_latency']['p95_ms'] / 1000) < 1e-8
        resources = [json.loads(line) for line in raw(f'campaigns/{folder}-resources.ndjson').splitlines()]
        supervisor = json.loads(raw(f'campaigns/{folder}-supervisor.json'))
        versions[version] = dict(summary=s[key], loops=loops, diagnostics=diagnostics,
            wall_seconds=supervisor['elapsed_seconds'], loop_seconds=sum(loops),
            samples=[dict(minutes=r['elapsed_seconds'] / 60,
                state_mib=r['state_content_bytes'] / 2**20,
                memory_mib=int(r['cgroups']['controller']['memory.current']) / 2**20) for r in resources])
    faults = json.loads(raw('campaigns/adversarial-v3/receipt.json'))
    assert len(faults['scenarios']) == 21 and all(r['passed'] for r in faults['scenarios'])
    load = [j['observed_ms'] / 1000 for j in faults['jobs'] if j['label'].startswith('load-')]
    assert len(load) == 32
    fault_rows = [dict(label=r['label'], passed=r['passed']) for r in faults['scenarios']]

data = dict(versions=versions, load=sorted(load), faults=fault_rows,
    recovery=s['recovery_v3'], gate_tests=s['local_validation']['receipt']['test_total'],
    handoff=s['handoff']['observation'], archive_sha256=m['archive_sha256'],
    summary_sha256=hashlib.sha256(summary_bytes).hexdigest())
(HERE / 'metrics.json').write_text(json.dumps(data, indent=2) + '\n')
template = (HERE / 'template.html').read_text()
(HERE / 'index.html').write_text(template.replace('__METRICS__', json.dumps(data).replace('<', '\\u003c')))
print(json.dumps({'passed': True, 'baseline_loops': len(versions['v1']['loops']), 'qualified_loops': len(versions['v3']['loops']), 'fault_scenarios': len(fault_rows), 'load_builds': len(load)}))
