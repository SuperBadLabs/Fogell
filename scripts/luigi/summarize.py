#!/usr/bin/env python3
"""Offline, fail-closed verification of an extracted Luigi evidence export.

Inputs are read-only. No network, subprocess, deployment mutation or repair is
performed. Archived database/state payloads are deliberately excluded by the
exporter; their equality is an adapter attestation, not independently re-proven
by this report. Retained source/JUnit/SDK evidence is independently rebound.
"""
import argparse
import copy
from collections import Counter
from datetime import datetime
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import stat
import sys
import tempfile

SCRIPTS = Path(__file__).resolve().parents[1]
FAULTS = ('cancel', 'runner_kill', 'controller_kill', 'postgres_outage')
PREREQUISITES = ('api_negative_controls', 'concurrent_unique_load', 'idempotency_race',
                 'shell', 'timeout', 'output-cap', 'artifact-cap', 'scratch-enospc')
TERMINAL = {'success', 'succeeded', 'failure', 'failed', 'unstable', 'aborted'}
KNOWN = TERMINAL | {'queued', 'pending', 'running', 'reconciliation_required'}
CAP_ERRORS = {'output-cap': ('ValueError:output_limit_reason_missing', 'OUTPUT_LIMIT_EXCEEDED'),
              'artifact-cap': ('ValueError:artifact_limit_reason_missing', 'ARTIFACT_LIMIT_EXCEEDED')}


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def load_module(name, filename):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def number(value):
    require(type(value) in (int, float) and math.isfinite(value) and value >= 0, 'invalid measured number')
    return value


def percentiles(values):
    ordered = sorted(number(v) for v in values)
    require(ordered, 'missing latency observations')
    return {'observations': len(ordered), 'p50_ms': ordered[math.ceil(len(ordered) * .50)-1],
            'p95_ms': ordered[math.ceil(len(ordered) * .95)-1]}


class Evidence:
    def __init__(self, root):
        self.root = root.resolve()
        require(self.root.is_dir(), 'evidence root is not a directory')
        self.hashes = {}
        # Check before passing any retained paths to the existing artifact reader.
        count = total = 0
        for path in self.root.rglob('*'):
            mode = path.lstat().st_mode
            require(stat.S_ISDIR(mode) or stat.S_ISREG(mode), 'linked/special evidence input')
            count += 1
            require(count <= 25000, 'evidence entry bound exceeded')
            if stat.S_ISREG(mode):
                size = path.stat().st_size
                require(size <= 256 * 1024**2, 'evidence file bound exceeded')
                total += size
                require(total <= 1024**3, 'evidence aggregate bound exceeded')

    def path(self, name):
        path = self.root / name
        require(path.resolve().is_relative_to(self.root), 'evidence path escapes root')
        require(path.is_file() and not path.is_symlink(), 'missing evidence: ' + str(name))
        return path

    def data(self, name):
        data = self.path(name).read_bytes()
        digest = sha(data)
        previous = self.hashes.setdefault(str(name), digest)
        require(previous == digest, 'evidence changed while reading')
        return data

    def json(self, name):
        return json.loads(self.data(name))

    def lines(self, name):
        return [json.loads(line) for line in self.data(name).splitlines() if line.strip()]


def protected(rows, expected):
    require(isinstance(rows, list) and len(rows) == 5, 'missing protected services')
    identities = {r['id']: r['started_at'] for r in rows}
    require(len(identities) == 5 and identities == expected, 'protected identities/start times changed')
    for row in rows:
        if 'running' in row:
            require(row['running'] is True, 'protected service stopped')
        if 'status' in row:
            require(row['status'] == 'running', 'protected service stopped')


def pilot_identity(receipt):
    deployment = receipt['declaration']['deployment']['deployment']
    return {key: deployment[key] for key in ('controller_container_id', 'postgres_container_id', 'source_commit')}


def pilot(evidence, name, loops, checker):
    relative = 'campaigns/' + name + '/receipt.json'
    receipt = evidence.json(relative)
    checker.verify_artifacts(evidence.path(relative), receipt)
    measured = checker.validate(receipt)
    require(receipt['declaration']['expected_test_count'] == 41, 'unexpected Domain test suite size')
    require(receipt['declaration']['loops'] == loops and len(receipt['retention_phases']) == loops // 5,
            'wrong ' + name + ' loop/retention count')
    require([p['after_loop'] for p in receipt['retention_phases']] == list(range(5, loops+1, 5)),
            'retention phases duplicated or out of order')
    phases = receipt['retention_phases']
    sweeps = [s for p in phases for s in p['sweeps']]
    result = {'identity': pilot_identity(receipt), 'tool_sha256': receipt['declaration']['tool_sha256'],
              'receipt': relative, 'measurements': measured,
              'per_kind_terminal_latency': {kind: percentiles([r['terminal_observed_ms'] for r in receipt['runs'] if r['kind'] == kind])
                                            for kind in ('failed', 'corrected')},
              'loop_latency': percentiles(receipt['loop_durations_ms']),
              'diagnostic_latency': percentiles([r['first_actionable_diagnostic_ms'] for r in receipt['runs'][::2]]),
              'retention': {'phases': len(phases), 'sweeps': len(sweeps),
                            'maintenance_elapsed_ms': sum(number(s['ElapsedMs']) for s in sweeps),
                            'physical_bytes_reclaimed': sum(number(p['physical_bytes_before']) - number(p['physical_bytes_after']) for p in phases),
                            'selected_logical_bytes': sum(number(s['SelectedLogicalBytes']) for s in sweeps),
                            'selected_builds': sum(number(s['Selected']) for s in sweeps),
                            'expired_builds': sum(number(s['Expired']) for s in sweeps)}}
    supervisor_name = 'campaigns/' + name + '-supervisor.json'
    supervisor = evidence.json(supervisor_name)
    require(supervisor['passed'] is True and supervisor.get('exit_code') == 0 and not supervisor.get('cleanup_errors'),
            name + ' supervisor failed')
    samples = evidence.lines('campaigns/' + name + '-resources.ndjson')
    require(samples and len(samples) == supervisor['samples'], name + ' resource sample count differs')
    result['resources'] = resources(samples, result['identity'])
    return receipt, result


def resources(samples, identities):
    values = {'state_content_bytes': [], 'host_free_bytes': []}
    grouped = {name: {key: [] for key in ('memory.current', 'memory.peak', 'pids.current')} for name in ('controller', 'postgres')}
    for sample in samples:
        for key in values:
            require(key in sample, 'unsupported resource sample schema: ' + key)
            values[key].append(number(sample[key]))
        for name, metrics in grouped.items():
            group = sample['cgroups'][name]
            require(group.get('container_id', identities[name+'_container_id']) == identities[name+'_container_id'],
                    'resource sample container differs')
            for key in metrics:
                metrics[key].append(number(int(group[key])))
    require(max(values['state_content_bytes']) < 2147483648 and min(values['host_free_bytes']) > 21474836480,
            'recorded resource guard exceeded')
    return {'samples': len(samples), 'state_content_peak_bytes': max(values['state_content_bytes']),
            'host_free_min_bytes': min(values['host_free_bytes']),
            'cgroups': {name: {key: max(observed) for key, observed in metrics.items()} for name, metrics in grouped.items()},
            'memory_peak_scope': 'kernel cgroup lifetime high-water mark; memory.current peak is sampled during this campaign'}


def replay_feedback(records):
    histories, admissions = {}, Counter()
    for row in records:
        if row.get('method') == 'POST' and row.get('status') in (200, 201, 202):
            value = json.loads(row['body'])
            if 'build_id' in value and 'attempt_id' in value:
                require(type(value.get('was_existing')) is bool, 'raw admission replay flag missing')
                admissions[(value['build_id'], value['attempt_id'], value['was_existing'])] += 1
        if row.get('method') != 'GET' or row.get('status') != 200 or '/feedback?from=' not in row.get('url', ''):
            continue
        value = json.loads(row['body'])
        build = value['build_id']
        require('/' + build + '/feedback?from=' in row['url'], 'raw feedback URL identity differs')
        state = histories.setdefault(build, {'cursor': 0, 'chunks': [], 'last': None, 'statuses': []})
        require(type(value['schema_version']) is int and value['schema_version'] == 1 and value['status'] in KNOWN, 'invalid raw feedback identity/status')
        for key in ('has_more', 'is_terminal', 'truncated', 'cancellation_requested'):
            require(type(value[key]) is bool, 'invalid raw feedback boolean')
        require(value['is_terminal'] == (value['status'] in TERMINAL), 'raw terminal flag differs')
        cursor = state['cursor']
        require(type(value['from_sequence']) is int and value['from_sequence'] == cursor
                and row['url'].endswith('?from=' + str(cursor)), 'raw cursor gap')
        for chunk in value['chunks']:
            require(type(chunk['sequence']) is int and chunk['sequence'] == cursor and isinstance(chunk['body'], str),
                    'raw chunk missing/duplicated')
            require(type(chunk.get('truncated')) is bool, 'raw chunk truncation missing')
            cursor += 1
            state['chunks'].append(chunk)
        require(type(value['next_sequence']) is int and value['next_sequence'] == cursor, 'raw continuation differs')
        require(not value['has_more'] or cursor > state['cursor'], 'raw nonprogressing backlog')
        state['statuses'].append(value['status'])
        state.update(cursor=cursor, last={k: v for k, v in value.items() if k != 'chunks'})
    return histories, admissions


def old_cap_failure(scenario, jobs):
    label = scenario['label']
    require(label in CAP_ERRORS and scenario.get('error') == CAP_ERRORS[label][0], 'v1 failure is not the known typed-cap gap')
    selected = [j for j in jobs if j['label'] == label]
    require(len(selected) == 1, 'missing v1 cap job')
    job = selected[0]
    require(job['last_page']['status'] in {'failure', 'failed'}, 'v1 cap did not reach actual failure')
    chunks = job['chunks']
    require(any(CAP_ERRORS[label][1] in c['body'] for c in chunks), 'v1 named raw cap cause not observed')
    diagnostics = [c['diagnostic'] for c in chunks if c.get('diagnostic')]
    require(any(d.get('category') == 'infrastructure' and 'RUN_FAILED:' in d.get('message', '') for d in diagnostics),
            'v1 generic typed fallback not observed')
    require(not any(CAP_ERRORS[label][1] in d.get('message', '') for d in diagnostics), 'v1 typed-cap cause was actually present')


def historical_controller_gap(receipt, histories, records):
    """Recognize the observed failure, never relabel it as successful recovery."""
    scenario = next(s for s in receipt['scenarios'] if s['label'] == 'controller_kill-1')
    require(scenario.get('error') == 'ValueError:fault_reason_missing', 'historical controller failure differs')
    jobs = {j['label']: j for j in receipt['jobs']}
    job = jobs['controller_kill-1']
    raw = histories[job['admission']['build_id']]
    require(raw['last']['status'] == 'reconciliation_required' and not raw['last']['is_terminal']
            and 'running' in raw['statuses'], 'historical controller did not reconcile active work')
    require(not any(c.get('diagnostic') for c in raw['chunks']), 'historical controller typed reason actually present')
    require(sum(c['body'] == 'FG_LUIGI_FAULT_STARTED' for c in raw['chunks']) == 1
            and not any(c['body'] == 'FG_LUIGI_FAULT_COMPLETED' for c in raw['chunks']),
            'historical interrupted execution marker differs')
    target = receipt['declaration']['target']
    cid = target['controller_container_id']
    commands = receipt['commands']
    kills = [c for c in commands if c['argv'][:2] == ['podman', 'kill']]
    restarts = [c for c in commands if c['argv'][:3] == ['systemctl', '--user', 'restart']]
    require(len(kills) == len(restarts) == 1 and kills[0]['argv'] == ['podman', 'kill', '--signal', 'KILL', cid]
            and restarts[0]['argv'] == ['systemctl', '--user', 'restart', target['service_unit']],
            'historical crash/restart scope differs')
    kill, restart = kills[0], restarts[0]
    require(all(c['exit_code'] == 0 and not c.get('error') for c in (kill, restart)), 'historical crash/restart failed')
    injected = number(kill['campaign_ms'])
    restored = number(restart['campaign_ms']) + number(restart['elapsed_ms'])
    require(injected + number(kill['elapsed_ms']) <= number(restart['campaign_ms']), 'historical restart preceded crash')
    inspections = [c for c in commands if c['argv'] == ['podman', 'inspect', '--format', '{{.Id}} {{.State.Running}} {{.State.Pid}}', cid]
                   and c['exit_code'] == 0 and not c.get('error')]
    before = [c for c in inspections if number(c['campaign_ms']) + number(c['elapsed_ms']) <= injected]
    after = [c for c in inspections if number(c['campaign_ms']) >= restored]
    require(before and after, 'historical restart PID evidence missing')
    old, new = before[-1]['stdout'].split(), after[0]['stdout'].split()
    require(len(old) == len(new) == 3 and old[:2] == new[:2] == [cid, 'true']
            and old[2].isdigit() and new[2].isdigit() and int(old[2]) > 0 and int(new[2]) > 0
            and old[2] != new[2], 'historical controller PID did not change')
    require(not any(c['argv'][:3] in (['systemctl', '--user', 'stop'], ['systemctl', '--user', 'start']) for c in commands),
            'historical controller campaign also changed dependency service')
    for label in ('controller_kill-1', 'controller_kill-1-queued-0', 'controller_kill-1-queued-1'):
        selected = jobs[label]
        build = selected['admission']['build_id']
        pages = [r for r in records if r.get('method') == 'GET' and r.get('status') == 200
                 and '/' + build + '/feedback?from=' in r.get('url', '')]
        earlier = [json.loads(r['body']) for r in pages if number(r['campaign_ms']) + number(r['elapsed_ms']) <= injected]
        require(earlier and earlier[-1]['status'] == ('running' if label == 'controller_kill-1' else 'queued'),
                'historical fault was not preceded by active target and queued controls')
        if label != 'controller_kill-1':
            require(selected.get('passed') is False and histories[build]['statuses'] == ['queued'],
                    'historical queued control was later observed or incorrectly claimed passing')
    require(any(r.get('method') == 'GET' and r.get('url') == target['url'] + '/health/ready'
                and r.get('status') == 200 and number(r['campaign_ms']) >= restored for r in records),
            'historical post-restart readiness missing')


def scenario_contract(receipt, previous):
    scenarios = receipt['scenarios']
    labels = [s['label'] for s in scenarios]
    required = list(PREREQUISITES) + [kind+'-'+str(i) for kind in FAULTS for i in range(1, 4)] + ['final_healthy_control']
    require(len(labels) == len(set(labels)), 'duplicate adversarial scenario')
    if previous == 'controller_gap':
        prefix = required[:required.index('controller_kill-1')+1] + ['final_healthy_control']
        require(labels == prefix and [s['label'] for s in scenarios if s['passed'] is not True] == ['controller_kill-1'],
                'historical controller gap scenario shape differs')
    elif previous:
        failed = [s['label'] for s in scenarios if s['passed'] is not True]
        require(failed and all(label in CAP_ERRORS for label in failed), 'unexpected historical failure')
        prefix = list(PREREQUISITES[:PREREQUISITES.index(failed[-1])+1]) + ['final_healthy_control']
        require(labels in (required, prefix), 'v1 omitted prerequisite evidence')
    else:
        require(labels == required and len(labels) == 21, 'newest full21 scenario campaign missing')


def postgres_hold(declaration, result, records, commands):
    require(type(declaration.get('postgres_outage_hold_seconds')) is int
            and declaration['postgres_outage_hold_seconds'] == 20, 'postgres hold declaration differs')
    measured = result['postgres_outage_hold']
    start, end, actual = (number(measured[key]) for key in ('start_campaign_ms', 'end_campaign_ms', 'hold_ms'))
    require(actual >= 20000 and end >= start and abs(end - start - actual) < 0.001,
            'postgres outage hold missing or too short')
    target = declaration['target']
    stops = [row for row in commands if row.get('argv') == ['systemctl', '--user', 'stop', target['postgres_service_unit']]
             and row.get('exit_code') == 0 and not row.get('error')
             and number(row['campaign_ms']) + number(row['elapsed_ms']) <= start]
    require(stops, 'postgres hold has no preceding successful owned stop')
    stopped = max(number(row['campaign_ms']) + number(row['elapsed_ms']) for row in stops)
    require(any(row.get('method') == 'GET' and row.get('url') == target['url'] + '/health/ready'
                and row.get('status') == 503 and stopped <= number(row['campaign_ms'])
                and number(row['campaign_ms']) + number(row['elapsed_ms']) <= start for row in records),
            'postgres hold has no preceding raw readiness503 after stop')
    starts = [row for row in commands if row.get('argv') == ['systemctl', '--user', 'start', target['postgres_service_unit']]]
    require(any(row.get('exit_code') == 0 and not row.get('error') and number(row['campaign_ms']) >= end for row in starts),
            'postgres hold has no subsequent successful owned start')
    require(not any(stopped <= number(row['campaign_ms']) < end for row in starts),
            'postgres restarted before hold completed')


def adversarial(evidence, directory, expected_cid, expected_pg, expected_protected, previous, driver):
    relative = directory + '/receipt.json'
    receipt = evidence.json(relative)
    require(not receipt.get('error') and receipt.get('censored_jobs') == 0, 'adversarial campaign error/censoring')
    target = receipt['declaration']['target']
    require(target['controller_container_id'] == expected_cid and target['postgres_container_id'] == expected_pg,
            'adversarial version/container differs')
    require(set(target['protected_container_ids']) == set(expected_protected), 'adversarial protected IDs differ')
    protected(receipt['protected_baseline'], expected_protected)
    require(type(receipt['declaration'].get('postgres_outage_hold_seconds')) is int
            and receipt['declaration']['postgres_outage_hold_seconds'] == 20, 'adversarial postgres hold declaration missing')
    scenarios = receipt['scenarios']
    labels = [s['label'] for s in scenarios]
    scenario_contract(receipt, previous)
    observations = receipt['protected_observations']
    require([o['label'] for o in observations] == labels, 'missing protected observation')
    for observation in observations:
        protected(observation['containers'], expected_protected)
        require(observation['containers'] == receipt['protected_baseline'], 'protected configuration changed within campaign')
    records = evidence.lines(directory + '/http.ndjson')
    histories, admissions = replay_feedback(records)
    for scenario in scenarios:
        if scenario['label'].startswith('postgres_outage-') and scenario.get('passed') is True:
            postgres_hold(receipt['declaration'], scenario['result'], records, receipt['commands'])
    require(any(r.get('status') == 401 for r in records) and any(r.get('status') == 422 for r in records)
            and any(r.get('status') == 413 for r in records) and any(r.get('status') == 409 and 'idempotency_conflict' in r.get('body', '') for r in records),
            'raw negative API/idempotency controls missing')
    jobs = receipt['jobs']
    by_label = {j['label']: j for j in jobs}
    require(len(by_label) == len(jobs), 'duplicate job label')
    recorded_admissions = Counter((j['admission']['build_id'], j['admission']['attempt_id'], j['admission']['was_existing']) for j in jobs)
    require(recorded_admissions == admissions, 'raw admission multiplicity/replay flags differ from jobs')
    historical_pending = {'controller_kill-1-queued-0', 'controller_kill-1-queued-1'} if previous == 'controller_gap' else set()
    for job in jobs:
        admission = job.get('admission', {})
        require((admission.get('build_id'), admission.get('attempt_id'), admission.get('was_existing')) in admissions, 'admission not observed in raw HTTP')
        if 'last_page' in job:
            raw = histories[admission['build_id']]
            require(job['chunks'] == raw['chunks'] and job['last_page'] == raw['last'] and job['next_sequence'] == raw['cursor'],
                    'job summary differs from raw feedback')
            require(not raw['last']['has_more'] and (raw['last']['is_terminal'] or raw['last']['status'] == 'reconciliation_required'
                    or job['label'] in historical_pending and raw['last']['status'] == 'queued' and not raw['chunks']),
                    'job evidence not drained')
    load = [by_label['load-'+str(i)] for i in range(32)]
    require(len({j['admission']['build_id'] for j in load}) == 32 and all(j.get('passed') is True for j in load),
            '32 distinct successful load jobs missing')
    racers = [by_label['same-'+str(i)] for i in range(16)]
    require(len({(j['admission']['build_id'], j['admission']['attempt_id']) for j in racers}) == 1
            and sum(j['admission']['was_existing'] is False for j in racers) == 1,
            'idempotency race identities differ')
    for job in jobs:
        if job['fixture'] == 'simple' and job['label'] not in historical_pending:
            raw = histories[job['admission']['build_id']]
            require(raw['last']['status'] in {'success', 'succeeded'} and
                    sum(c['body'] == 'FG_LUIGI_CONTROL' for c in raw['chunks']) == 1,
                    'control did not complete exactly once')
    failed = [s for s in scenarios if s['passed'] is not True]
    if previous:
        require(receipt['passed'] is False and failed, 'v1 known failure receipt missing')
        require(receipt['unexpected_failures'] == len(failed), 'v1 unexplained failure count')
        for scenario in failed:
            if previous == 'controller_gap':
                historical_controller_gap(receipt, histories, records)
            else:
                old_cap_failure(scenario, jobs)
        require(scenarios[-1]['label'] == 'final_healthy_control' and scenarios[-1]['passed'] is True,
                'v1 final healthy control missing')
        require(all(j.get('passed') is True or j['label'] in {s['label'] for s in failed} | historical_pending for j in jobs),
                'v1 unrelated failed job')
    else:
        required = list(PREREQUISITES) + [kind+'-'+str(i) for kind in FAULTS for i in range(1, 4)] + ['final_healthy_control']
        require(labels == required and len(scenarios) == 21, 'newest full21 scenario campaign missing')
        require(receipt['passed'] is True and not failed and receipt['unexpected_failures'] == 0
                and not receipt.get('remaining_faults_not_run'), 'v2 adversarial campaign failed')
        require(receipt['declaration']['fault_repetitions'] == 3 and receipt['declaration']['faults'] == list(FAULTS),
                'v2 fault declaration differs')
        require(all(j.get('passed') is True for j in jobs), 'v2 job failed')
    passed_labels = {s['label'] for s in scenarios if s['passed'] is True}
    for fixture in ('shell', 'timeout', 'output-cap', 'artifact-cap', 'scratch-enospc'):
        if fixture not in passed_labels:
            continue
        driver.Campaign.validate_failure(by_label[fixture], fixture)
        require(by_label[fixture+'-recovery-control']['passed'] is True, 'post-refusal healthy control missing')
    for scenario in scenarios:
        if scenario['passed'] is not True:
            continue
        label, result = scenario['label'], scenario['result']
        if label in {kind+'-'+str(i) for kind in FAULTS for i in range(1, 4)}:
            require(result['marker_observed_before_fault'] is True and result['queued_controls_succeeded'] == 2
                    and len(set(result['verified_queued_builds'])) == 2, 'fault lost queued controls')
            require(result['status_before_injection'] not in TERMINAL | {'reconciliation_required'}, 'fault was not injected into active work')
            target_job = by_label[label]
            require(target_job['admission']['build_id'] == result['build_id']
                    and target_job['last_page']['status'] == result['status'], 'fault result differs from raw target')
            require(sum(c['body'] == 'FG_LUIGI_FAULT_STARTED' for c in target_job['chunks']) == 1,
                    'fault execution replayed or start marker missing')
            control_ids = [by_label[label+'-queued-'+str(i)]['admission']['build_id'] for i in range(2)]
            require(control_ids == result['verified_queued_builds'] and all('queued' in histories[c]['statuses'] for c in control_ids),
                    'fault controls were not observed queued')
            diagnostics = driver.Campaign.diagnostics(target_job)
            if not label.startswith('postgres_outage'):
                require(result['status'] in {'failure', 'failed', 'aborted', 'reconciliation_required'} and diagnostics,
                        'interrupted fault target did not fail with evidence')
            elif result['status'] in {'success', 'succeeded'}:
                require(sum(c['body'] == 'FG_LUIGI_FAULT_COMPLETED' for c in target_job['chunks']) == 1,
                        'outage success lacks completion evidence')
            else:
                require(diagnostics, 'outage refusal lacks diagnostics')
            if label.startswith('cancel'):
                require(target_job['last_page']['cancellation_requested'] is True, 'cancellation flag missing')
            if label.startswith('runner_kill'):
                require(result['injection']['container_id'] == expected_cid and result['injection']['pidfd'] is True
                        and result['injection']['delivered'] is True, 'runner kill not attested')
            elif label.startswith(('controller_kill', 'postgres_outage')):
                cid = expected_cid if label.startswith('controller_kill') else expected_pg
                before, after = result['injection']['before'], result['injection']['after']
                require(before['id'] == after['id'] == cid and before['pid'] != after['pid'] and after['running'] is True,
                        'fault restart identity differs')
    if 'artifact-cap' in passed_labels:
        artifact = next(s['result'] for s in scenarios if s['label'] == 'artifact-cap')
        require(len(artifact['artifact_absence_inventory']) == 2 and all(r['absent'] is True for r in artifact['artifact_absence_inventory']),
                'oversized artifact absence not established')
    if 'scratch-enospc' in passed_labels:
        scratch = next(s['result'] for s in scenarios if s['label'] == 'scratch-enospc')
        require(scratch['scratch_cleanup_confirmed'] is True, 'scratch cleanup missing')
    latencies = [j['observed_ms'] for j in jobs if j['label'].startswith('load-') and j.get('passed')]
    return {'receipt': relative, 'controller_container_id': expected_cid, 'passed': receipt['passed'],
            'accepted_historical_failure': bool(previous), 'historical_failure_kind': previous or None,
            'unobserved_after_queued_jobs': sorted(historical_pending), 'failed_scenarios': [s['label'] for s in failed],
            'scenarios': len(scenarios), 'jobs': len(jobs), 'load_latency': percentiles(latencies),
            'state_peak_bytes': max(number(s[k]['state_regular_file_bytes']) for s in scenarios for k in ('resources_before', 'resources_after') if k in s)}


def recovery_provenance(receipt, pair, workload, baseline):
    require(pair.get('database_inventory', {}).get('row_encoding') == 'sorted-complete-json-v1',
            'recovery inventory does not encode complete database rows')
    latest = baseline['runs'][-1]
    deployment = baseline['declaration']['deployment']['deployment']
    require(latest['kind'] == 'corrected' and latest['passed'] is True, 'baseline recovery source is not corrected')
    expected = {'organization': deployment['organization'], 'project': deployment['project'],
                'build': latest['admission']['build_id'], 'attempt': latest['admission']['attempt_id'],
                'junit_sha256': latest['junit_sha256']}
    require(receipt['workload'] == workload and all(workload.get(key) == value for key, value in expected.items())
            and receipt['previous_artifact_sha256'] == expected['junit_sha256'],
            'restored workload does not identify the original baseline artifact')
    if 'original_workload_tool_sha256' in receipt:
        require(receipt['original_workload_tool_sha256'] == baseline['declaration']['tool_sha256'],
                'restored original workload tool differs from baseline')


def recover(evidence, checker, deployment, baseline, protected_ids, expected_tool):
    prefix = 'backups/fogell_luigi_restore_20260923/'
    receipt = evidence.json(prefix + 'receipt.json')
    require(receipt['passed'] is True and not receipt['cleanup_errors'] and receipt['original_service_restored'] is True,
            'recovery/cleanup/original service failed')
    require(receipt['tool_sha256'] == expected_tool, 'recovery runner differs from qualified tool')
    require(receipt['source_database'] == 'fogell' and receipt['target_database'] == 'fogell_luigi_restore_20260923', 'recovery database differs')
    require(receipt['controller_container_id'] == deployment['controller_container_id']
            and receipt['postgres_container_id'] == deployment['postgres_container_id']
            and receipt['source_commit'] == deployment['source_commit'], 'recovery used another deployment')
    require(0 < number(receipt['recovery_elapsed_seconds']) <= 120
            and receipt['lost_backed_up_rows'] == receipt['lost_backed_up_files'] == 0, 'recovery RTO/RPO failed')
    protected(receipt['protected_before'], protected_ids)
    protected(receipt['protected_after'], protected_ids)
    require(receipt['protected_before'] == receipt['protected_after'], 'recovery changed protected services')
    require(receipt['stale_authority'] == {'passed': True, 'stale_log_rejected': True, 'stale_terminal_rejected': True},
            'stale authority was not rejected')
    seed = evidence.json(prefix + 'stale-authority.json')
    require(receipt['activation']['restore_epoch'] > seed['epoch'], 'restore epoch did not advance')
    require(receipt['source_seed_cleanup']['passed'] is True, 'original source probe was not reconciled')
    require({r['control'] for r in receipt['controls'] if r['rejected'] is True} ==
            {'altered-database', 'changed-state-file', 'missing-state-file'}, 'backup corruption controls missing')
    pair = evidence.json(prefix + 'pair-manifest.json')
    require(pair['source_commit'] == deployment['source_commit'] and pair['tool_sha256'] == receipt['tool_sha256']
            and pair['release_manifest_sha256'] == receipt['release_manifest_sha256'], 'pair identity differs')
    workload = evidence.json(prefix + 'workload.json')
    recovery_provenance(receipt, pair, workload, baseline)
    original = Path(deployment['deployment_root'])
    retained = Path(receipt['control_receipt_path'])
    require(retained.is_relative_to(original), 'recovery control path outside deployment')
    relative = str(retained.relative_to(original))
    control = evidence.json(relative)
    require(evidence.hashes[relative] == receipt['control_receipt_sha256'], 'recovery control receipt changed')
    checker.verify_artifacts(evidence.path(relative), control)
    require(control['passed'] is True and len(control['runs']) == 2
            and [r['kind'] for r in control['runs']] == ['failed', 'corrected'], 'recovery correction pair incomplete')
    for run in control['runs']:
        checker.validate_run(run, 41)
        require(run['tool_sha256'] == receipt['tool_sha256'], 'recovery used wrong tool')
    require([r['admission']['build_id'] for r in control['runs']] == receipt['new_builds'], 'recovery build identities differ')
    return {'receipt': prefix+'receipt.json', 'passed': True, 'recovery_seconds': receipt['recovery_elapsed_seconds'],
            'original_service_downtime_seconds': receipt['original_service_downtime_seconds'],
            'lost_backed_up_rows': 0, 'lost_backed_up_files': 0, 'stale_authority_rejected': True,
            'offline_reverified': 'retained baseline/control source, JUnit and SDK artifacts plus receipt identity links',
            'adapter_attested_only': 'complete database/state equality and original service readiness; private backup payloads excluded from export'}


def setup_failure(evidence, relative, receipt, identity, protected_ids):
    require(receipt.get('passed') is False and isinstance(receipt.get('error'), str) and receipt['error'],
            'empty adversarial run is not a retained setup failure')
    require(receipt.get('jobs') == [] and receipt.get('scenarios') == [], 'setup failure executed campaign work')
    target = receipt['declaration']['target']
    require(all(target.get(key) == identity[key] for key in ('controller_container_id', 'postgres_container_id'))
            and set(target['protected_container_ids']) == set(protected_ids), 'setup failure target identity differs')
    require(target['service_unit'] == 'fogell.service' and target['postgres_service_unit'] == 'fogell-postgres.service'
            and target['url'] == 'http://127.0.0.1:46206', 'setup failure target scope differs')
    owned = {identity['controller_container_id'], identity['postgres_container_id'], *protected_ids}
    require(isinstance(receipt.get('commands'), list), 'setup command evidence missing')
    for command in receipt['commands']:
        argv = command['argv']
        inspect = (isinstance(argv, list) and len(argv) == 5 and argv[:3] == ['podman', 'inspect', '--format']
                   and isinstance(argv[3], str) and argv[4] in owned)
        show = argv in [['systemctl', '--user', 'show', unit, '--property=ExecStart', '--value']
                        for unit in (target['service_unit'], target['postgres_service_unit'])]
        require(inspect or show, 'setup failure contains unapproved or mutating command')
    raw_path = str(Path(relative).parent / 'http.ndjson')
    records = evidence.lines(raw_path) if (evidence.root / raw_path).exists() else []
    for row in records:
        require(row.get('method') == 'GET' and row.get('url') == target['url'] + '/health/ready'
                and row.get('request_bytes') == 0 and row.get('request_sha256') is None,
                'setup failure contains mutating or non-readiness HTTP')
    return {'receipt': relative, 'receipt_sha256': evidence.hashes[relative],
            'controller_container_id': identity['controller_container_id'], 'passed': False,
            'classification': 'read_only_setup_failure; no jobs or scenarios executed',
            'error': receipt['error'], 'commands': len(receipt['commands']), 'readiness_requests': len(records)}


def adversarial_receipts(evidence):
    for path in sorted((evidence.root/'campaigns').rglob('receipt.json')):
        relative = str(path.relative_to(evidence.root))
        receipt = evidence.json(relative)
        declaration = receipt.get('declaration', {})
        if path.parent.name.startswith('adversarial') or 'faults' in declaration or 'fault_repetitions' in declaration:
            yield path, receipt


def find_adversarial(evidence, identity, protected_ids):
    matches, setup_failures = [], []
    controller = identity['controller_container_id']
    for path, receipt in adversarial_receipts(evidence):
        relative = str(path.relative_to(evidence.root))
        if receipt.get('declaration', {}).get('target', {}).get('controller_container_id') == controller:
            if receipt.get('jobs') == [] and receipt.get('scenarios') == []:
                setup_failures.append(setup_failure(evidence, relative, receipt, identity, protected_ids))
            else:
                # Failed executed campaigns count too: never select only a later
                # passing retry while silently discarding measured failures.
                matches.append(str(path.parent.relative_to(evidence.root)))
    require(len(matches) == 1, 'missing or ambiguous executed adversarial campaign for controller: ' + controller)
    return matches[0], setup_failures


def upgrade_history(evidence, old, new, expected_protected):
    matches = []
    for path in sorted((evidence.root/'campaigns').glob('upgrade-*.json')):
        relative = str(path.relative_to(evidence.root))
        receipt = evidence.json(relative)
        if receipt.get('old_deployment', {}).get('controller_container_id') == old['controller_container_id'] and receipt.get('new_deployment', {}).get('controller_container_id') == new['controller_container_id']:
            require(receipt['passed'] is True and not receipt['cleanup_errors'], 'controller upgrade failed')
            require(receipt['protected_before'] == receipt['protected_after'], 'upgrade changed protected services')
            rows = receipt['protected_before']
            require(isinstance(rows, list) and all(isinstance(row, dict) and row.get('running') is True for row in rows),
                    'upgrade protected observations must be running identity dictionaries')
            # Compare original RFC3339 strings verbatim, retaining subsecond
            # precision; plain Go-template display timestamps are not accepted.
            protected(rows, expected_protected)
            for key in old:
                require(receipt['old_deployment'][key] == old[key] and receipt['new_deployment'][key] == new[key],
                        'upgrade deployment history differs')
            matches.append(relative)
    require(len(matches) == 1, 'missing or ambiguous version upgrade history')
    return {'receipt': matches[0], 'passed': True}


def validate_handoff(receipt, identity, qualification_digest, expected_protected):
    require(receipt.get('passed') is True and receipt.get('phase') == 'post_qualification',
            'healthy post-qualification handoff missing')
    require(all(receipt.get(key) == value for key, value in identity.items()), 'handoff deployment identity differs')
    require(receipt.get('qualification_receipt_sha256') == qualification_digest,
            'handoff does not bind completed qualification receipt')
    require(receipt.get('ready_status') == 200, 'handoff controller not ready')
    for name in ('controller', 'postgres'):
        service = receipt['services'][name]
        require(service.get('active') is True and service.get('enabled') is True,
                'handoff service is not active and enabled: ' + name)
    protected(receipt['protected'], expected_protected)
    require(number(receipt['state_content_bytes']) < 2147483648
            and number(receipt['host_free_bytes']) > 21474836480, 'handoff storage guard exceeded')
    recorded = datetime.fromisoformat(receipt['recorded_at_utc'].replace('Z', '+00:00'))
    require(recorded.tzinfo is not None and recorded.utcoffset().total_seconds() == 0,
            'handoff timestamp must be explicitly UTC')
    # Pilot receipts have no UTC completion timestamp. The completed receipt
    # hash plus explicit phase attest ordering; copied filesystem mtimes cannot.


def version_history(evidence, first, latest, protected_ids):
    """Require the entire retained v1 -> v2 -> v3 chain, including failed trials."""
    upgrades = [(str(p.relative_to(evidence.root)), evidence.json(str(p.relative_to(evidence.root))))
                for p in sorted((evidence.root/'campaigns').glob('upgrade-*.json'))]
    require(len(upgrades) == 2, 'expected exactly two retained upgrades for three-version history')
    identities, transitions, used = [first], [], set()
    for _ in range(2):
        current = identities[-1]
        candidates = [(name, r) for name, r in upgrades
                      if r.get('old_deployment', {}).get('controller_container_id') == current['controller_container_id']]
        require(len(candidates) == 1, 'upgrade chain is missing or branches')
        name, receipt = candidates[0]
        following = {key: receipt['new_deployment'][key] for key in first}
        require(name not in used and following['controller_container_id'] not in {i['controller_container_id'] for i in identities},
                'upgrade chain repeats container')
        require(following['source_commit'] not in {i['source_commit'] for i in identities}
                and following['postgres_container_id'] == first['postgres_container_id'], 'upgrade version or database identity differs')
        transitions.append(upgrade_history(evidence, current, following, protected_ids))
        identities.append(following)
        used.add(name)
    require(identities[-1] == latest and len(used) == len(upgrades), 'upgrade chain does not reach qualified deployment')
    known = {identity['controller_container_id'] for identity in identities}
    for _, receipt in adversarial_receipts(evidence):
        require(receipt.get('declaration', {}).get('target', {}).get('controller_container_id') in known,
                'unaccounted adversarial campaign version')
    return identities, transitions


def local_gate(raw, expected_commit):
    value = json.loads(raw)
    require(value.get('passed') is True and value.get('exit_code') == 0, 'local gate receipt failed')
    require(value.get('source_commit') == expected_commit, 'local gate lacks matching qualified source commit')
    return {'sha256': sha(raw), 'receipt': value, 'source_commit': expected_commit,
            'scope': 'provided passing local gate receipt explicitly bound to qualified source commit'}


def summarize(root):
    evidence = Evidence(root)
    checker = load_module('luigi_pilot_verifier', 'prove-self-hosted-pilot.py')
    driver = load_module('luigi_fault_verifier', 'prove-luigi-adversarial.py')
    deployment = evidence.json('deployment.json')
    initial = evidence.json('protected-before.json')
    protected_ids = {r['id']: r['started_at'] for r in initial}
    protected(initial, protected_ids)
    require(set(deployment['protected_container_ids']) == set(protected_ids), 'deployment protected IDs differ')
    baseline, baseline_summary = pilot(evidence, 'pilot', 100, checker)
    qualification, qualification_summary = pilot(evidence, 'qualification', 30, checker)
    old, new = baseline_summary['identity'], qualification_summary['identity']
    require(old['controller_container_id'] != new['controller_container_id'], 'versions share controller identity')
    require(old['source_commit'] != new['source_commit'], 'versions share source commit')
    require(baseline_summary['tool_sha256'] != qualification_summary['tool_sha256'], 'versions share runner tool identity')
    require(old['postgres_container_id'] == new['postgres_container_id'] == deployment['postgres_container_id'], 'versions use different PostgreSQL')
    require(all(new[k] == deployment[k] for k in new), 'qualification does not describe current deployment')
    identities, history = version_history(evidence, old, new, protected_ids)
    middle = identities[1]
    prior_path, prior_setup = find_adversarial(evidence, old, protected_ids)
    middle_path, middle_setup = find_adversarial(evidence, middle, protected_ids)
    current_path, current_setup = find_adversarial(evidence, new, protected_ids)
    prior = adversarial(evidence, prior_path, old['controller_container_id'], old['postgres_container_id'], protected_ids, 'cap_gap', driver)
    intermediate = adversarial(evidence, middle_path, middle['controller_container_id'], middle['postgres_container_id'], protected_ids, 'controller_gap', driver)
    current = adversarial(evidence, current_path, new['controller_container_id'], new['postgres_container_id'], protected_ids, False, driver)
    recovery = recover(evidence, checker, deployment, baseline, protected_ids, qualification_summary['tool_sha256'])
    handoff = evidence.json('campaigns/handoff.json')
    validate_handoff(handoff, new, evidence.hashes['campaigns/qualification/receipt.json'], protected_ids)
    for path in sorted(evidence.root.rglob('receipt.json')):
        evidence.data(str(path.relative_to(evidence.root)))
    return {'schema_version': 2, 'passed': True, 'verifier_sha256': sha(Path(__file__).read_bytes()),
            'adversarial_setup_failures': {'count': len(prior_setup) + len(middle_setup) + len(current_setup),
                                           'baseline_v1': prior_setup, 'intermediate_v2': middle_setup, 'qualification_v3': current_setup},
            'baseline_v1': baseline_summary, 'historical_adversarial_v1': prior,
            'historical_adversarial_v2': intermediate, 'historical_failed_executed_campaigns': 2,
            'qualification_v3': qualification_summary, 'adversarial_v3': current, 'recovery_v3': recovery,
            'version_identities': identities, 'upgrades': history,
            'handoff': {'receipt': 'campaigns/handoff.json', 'observation': handoff,
                        'ordering_evidence': 'post_qualification phase plus SHA-256 of verified completed qualification receipt; no file-mtime inference'},
            'receipt_and_observation_sha256': evidence.hashes,
            'scope': 'separate versioned conditions; no combined latency distribution or production SLA claim'}


def self_test():
    controls = []
    def rejects(label, operation):
        try:
            operation()
        except (ValueError, KeyError):
            controls.append(label)
        else:
            raise AssertionError('accepted known bad input: ' + label)
    require(percentiles([9, 1, 3, 2]) == {'observations': 4, 'p50_ms': 2, 'p95_ms': 9}, 'nearest-rank percentile differs')
    controls.append('measured_nearest_rank')
    build = '11111111-1111-1111-1111-111111111111'
    page = {'schema_version': 1, 'build_id': build, 'status': 'failure', 'is_terminal': True,
            'has_more': False, 'cancellation_requested': False, 'truncated': False,
            'from_sequence': 0, 'next_sequence': 1, 'chunks': [{'sequence': 0, 'body': 'reason', 'truncated': False}]}
    row = {'method': 'GET', 'status': 200, 'url': 'http://127.0.0.1/'+build+'/feedback?from=0', 'body': json.dumps(page)}
    require(replay_feedback([row])[0][build]['cursor'] == 1, 'valid raw feedback refused')
    admission = {'build_id': build, 'attempt_id': build, 'was_existing': True}
    admitted = {'method': 'POST', 'status': 200, 'body': json.dumps(admission)}
    require(replay_feedback([admitted, admitted])[1][(build, build, True)] == 2, 'raw admission multiplicity lost')
    controls.append('idempotent_admission_multiplicity')
    for key, value in [('is_terminal', False), ('next_sequence', 2), ('from_sequence', 1), ('has_more', 1)]:
        changed = dict(page, **{key: value})
        rejects('raw_' + key, lambda changed=changed: replay_feedback([dict(row, body=json.dumps(changed))]))
    changed = copy.deepcopy(page)
    changed['chunks'][0]['sequence'] = 1
    rejects('raw_skipped_chunk', lambda: replay_feedback([dict(row, body=json.dumps(changed))]))
    for label, sequences in [('missing_first', [1]), ('missing_middle', [0, 2])]:
        changed = copy.deepcopy(page)
        changed['chunks'] = [{'sequence': seq, 'body': 'reason', 'truncated': False} for seq in sequences]
        changed['next_sequence'] = sequences[-1] + 1
        rejects('raw_' + label, lambda changed=changed: replay_feedback([dict(row, body=json.dumps(changed))]))
    declaration = {'postgres_outage_hold_seconds': 20,
                   'target': {'postgres_service_unit': 'fogell-postgres.service', 'url': 'http://127.0.0.1:46206'}}
    hold = {'postgres_outage_hold': {'start_campaign_ms': 1000, 'end_campaign_ms': 21000, 'hold_ms': 20000}}
    raw = [{'method': 'GET', 'url': declaration['target']['url'] + '/health/ready',
            'status': 503, 'campaign_ms': 500, 'elapsed_ms': 100}]
    commands = [{'argv': ['systemctl', '--user', action, 'fogell-postgres.service'],
                 'exit_code': 0, 'error': None, 'campaign_ms': at, 'elapsed_ms': 100}
                for action, at in [('stop', 0), ('start', 21001)]]
    postgres_hold(declaration, hold, raw, commands)
    controls.append('postgres_hold_rebound_to_readiness_and_commands')
    rejects('postgres_wrong_declared_hold', lambda: postgres_hold(dict(declaration, postgres_outage_hold_seconds=15), hold, raw, commands))
    shorter = {'postgres_outage_hold': dict(hold['postgres_outage_hold'], hold_ms=19999, end_campaign_ms=20999)}
    rejects('postgres_short_hold', lambda: postgres_hold(declaration, shorter, raw, commands))
    inconsistent = {'postgres_outage_hold': dict(hold['postgres_outage_hold'], end_campaign_ms=21001)}
    rejects('postgres_inconsistent_hold_clock', lambda: postgres_hold(declaration, inconsistent, raw, commands))
    rejects('postgres_missing_raw503', lambda: postgres_hold(declaration, hold, [], commands))
    rejects('postgres_early_restart', lambda: postgres_hold(declaration, hold, raw, commands + [dict(commands[-1], campaign_ms=20000)]))
    rejects('postgres_missing_restore', lambda: postgres_hold(declaration, hold, raw, commands[:1]))
    scenario = {'label': 'output-cap', 'error': 'ValueError:output_limit_reason_missing'}
    job = {'label': 'output-cap', 'last_page': {'status': 'failure'}, 'chunks': [
        {'body': 'runner-failure: OUTPUT_LIMIT_EXCEEDED', 'diagnostic': None},
        {'body': '', 'diagnostic': {'category': 'infrastructure', 'message': 'RUN_FAILED: runner could not complete'}}]}
    old_cap_failure(scenario, [job])
    controls.append('observed_historical_cap_gap')
    rejects('historical_other_failure', lambda: old_cap_failure(dict(scenario, error='ValueError:other'), [job]))
    changed = copy.deepcopy(job); changed['chunks'].pop(0)
    rejects('historical_no_raw_cap', lambda: old_cap_failure(scenario, [changed]))
    changed = copy.deepcopy(job); changed['chunks'][1]['diagnostic']['message'] = 'OUTPUT_LIMIT_EXCEEDED'
    rejects('historical_actual_typed_cap', lambda: old_cap_failure(scenario, [changed]))
    labels = list(PREREQUISITES) + [kind+'-'+str(i) for kind in FAULTS for i in range(1, 4)] + ['final_healthy_control']
    receipt = {'scenarios': [{'label': label, 'passed': True} for label in labels]}
    scenario_contract(receipt, False)
    controls.append('complete21_scenario_shape')
    changed = copy.deepcopy(receipt); changed['scenarios'].pop(10)
    rejects('missing_fault_repetition', lambda: scenario_contract(changed, False))
    changed = copy.deepcopy(receipt); changed['scenarios'].append(changed['scenarios'][0])
    rejects('duplicate_scenario', lambda: scenario_contract(changed, False))
    historical_labels = labels[:labels.index('controller_kill-1')+1] + ['final_healthy_control']
    historical = {'scenarios': [{'label': label, 'passed': label != 'controller_kill-1'} for label in historical_labels]}
    scenario_contract(historical, 'controller_gap')
    controls.append('historical_controller_prefix_retained')
    rejects('historical_controller_not_latest_pass', lambda: scenario_contract(historical, False))
    changed = copy.deepcopy(historical); changed['scenarios'][0]['passed'] = False
    rejects('historical_unrelated_failure', lambda: scenario_contract(changed, 'controller_gap'))
    cid = 'a'*64
    target = {'controller_container_id': cid, 'service_unit': 'fogell.service', 'url': 'http://127.0.0.1:46206'}
    fault_labels = ['controller_kill-1', 'controller_kill-1-queued-0', 'controller_kill-1-queued-1']
    gap = {'declaration': {'target': target}, 'jobs': [{'label': label, 'passed': False, 'admission': {'build_id': str(i)}}
            for i, label in enumerate(fault_labels)],
           'scenarios': [{'label': 'controller_kill-1', 'passed': False, 'error': 'ValueError:fault_reason_missing'}]}
    def cmd(argv, when, stdout=''):
        return {'argv': argv, 'exit_code': 0, 'error': None, 'campaign_ms': when, 'elapsed_ms': 1, 'stdout': stdout}
    inspect = ['podman', 'inspect', '--format', '{{.Id}} {{.State.Running}} {{.State.Pid}}', cid]
    gap['commands'] = [cmd(inspect, 5, cid+' true 100'), cmd(['podman', 'kill', '--signal', 'KILL', cid], 10),
                       cmd(['systemctl', '--user', 'restart', 'fogell.service'], 12), cmd(inspect, 20, cid+' true 200')]
    histories = {'0': {'last': {'status': 'reconciliation_required', 'is_terminal': False},
                       'statuses': ['running', 'reconciliation_required'],
                       'chunks': [{'body': 'FG_LUIGI_FAULT_STARTED', 'diagnostic': None}]},
                 **{str(i): {'last': {'status': 'queued'}, 'statuses': ['queued'], 'chunks': []} for i in (1, 2)}}
    raw = [{'method': 'GET', 'status': 200, 'url': target['url']+'/'+str(i)+'/feedback?from=0',
            'campaign_ms': 2, 'elapsed_ms': 1, 'body': json.dumps({'status': 'running' if i == 0 else 'queued'})} for i in range(3)]
    raw.append({'method': 'GET', 'status': 200, 'url': target['url']+'/health/ready', 'campaign_ms': 22, 'elapsed_ms': 1})
    historical_controller_gap(gap, histories, raw)
    controls.append('historical_controller_gap_bound_to_raw_and_owned_restart')
    changed = copy.deepcopy(histories); changed['0']['chunks'][0]['diagnostic'] = {'message': 'LEASE_EXPIRED'}
    rejects('historical_controller_reason_present', lambda: historical_controller_gap(gap, changed, raw))
    changed = copy.deepcopy(histories); changed['0']['last']['status'] = 'success'
    rejects('historical_controller_not_reconciliation', lambda: historical_controller_gap(gap, changed, raw))
    changed = copy.deepcopy(gap); changed['commands'][1]['argv'][-1] = 'b'*64
    rejects('historical_controller_wrong_kill_identity', lambda: historical_controller_gap(changed, histories, raw))
    changed = copy.deepcopy(gap); changed['commands'][-1]['stdout'] = cid+' true 100'
    rejects('historical_controller_pid_unchanged', lambda: historical_controller_gap(changed, histories, raw))
    changed = copy.deepcopy(gap); changed['jobs'][1]['passed'] = True
    rejects('historical_queued_control_false_pass', lambda: historical_controller_gap(changed, histories, raw))
    rejects('historical_missing_prefault_queue', lambda: historical_controller_gap(gap, histories, raw[:2]+raw[3:]))
    rejects('historical_missing_postrestart_ready', lambda: historical_controller_gap(gap, histories, raw[:-1]))
    rejects('nan_latency', lambda: percentiles([float('nan')]))
    original_workload = {'organization': 'org-original', 'project': 'project-original',
                         'build': 'build-original', 'attempt': 'attempt-original', 'junit_sha256': 'a'*64}
    original_baseline = {'declaration': {'tool_sha256': 'b'*64, 'deployment': {'deployment': {
        'organization': original_workload['organization'], 'project': original_workload['project']}}},
        'runs': [{'kind': 'corrected', 'passed': True, 'junit_sha256': original_workload['junit_sha256'],
                  'admission': {'build_id': original_workload['build'], 'attempt_id': original_workload['attempt']}}]}
    recovery_receipt = {'workload': original_workload, 'previous_artifact_sha256': 'a'*64,
                        'original_workload_tool_sha256': 'b'*64}
    pair = {'database_inventory': {'row_encoding': 'sorted-complete-json-v1'}}
    recovery_provenance(recovery_receipt, pair, original_workload, original_baseline)
    controls.append('restored_artifact_exact_baseline_identity_and_complete_rows')
    for key in ('organization', 'project', 'build', 'attempt'):
        changed = dict(original_workload, **{key: 'another-valid-identity'})
        changed_receipt = dict(recovery_receipt, workload=changed)
        rejects('recovery_same_hash_wrong_'+key,
                lambda changed=changed, changed_receipt=changed_receipt:
                recovery_provenance(changed_receipt, pair, changed, original_baseline))
    rejects('recovery_wrong_original_tool', lambda: recovery_provenance(
        dict(recovery_receipt, original_workload_tool_sha256='c'*64), pair, original_workload, original_baseline))
    for encoding in (None, 'insert-line-v1'):
        changed_pair = {'database_inventory': {} if encoding is None else {'row_encoding': encoding}}
        rejects('recovery_incomplete_rows_'+str(encoding), lambda changed_pair=changed_pair:
                recovery_provenance(recovery_receipt, changed_pair, original_workload, original_baseline))
    identity = {'source_commit': 'a'*40, 'controller_container_id': 'b'*64, 'postgres_container_id': 'c'*64}
    expected_protected = {str(i)*64: '2026-09-23T00:00:00Z' for i in range(1, 6)}
    handoff = dict(identity, passed=True, phase='post_qualification', qualification_receipt_sha256='d'*64,
                   ready_status=200, services={name: {'active': True, 'enabled': True} for name in ('controller', 'postgres')},
                   protected=[{'id': key, 'started_at': value, 'running': True} for key, value in expected_protected.items()],
                   state_content_bytes=100, host_free_bytes=30*1024**3, recorded_at_utc='2026-09-23T01:00:00Z')
    validate_handoff(handoff, identity, 'd'*64, expected_protected)
    controls.append('qualified_healthy_handoff')
    for key, value in [('phase', 'before_tests'), ('qualification_receipt_sha256', 'e'*64),
                       ('controller_container_id', 'f'*64), ('ready_status', 503),
                       ('recorded_at_utc', '2026-09-23T01:00:00')]:
        changed = copy.deepcopy(handoff); changed[key] = value
        rejects('handoff_'+key, lambda changed=changed: validate_handoff(changed, identity, 'd'*64, expected_protected))
    changed = copy.deepcopy(handoff); changed['services']['controller']['enabled'] = False
    rejects('handoff_disabled_service', lambda: validate_handoff(changed, identity, 'd'*64, expected_protected))
    changed = copy.deepcopy(handoff); changed['protected'].pop()
    rejects('handoff_missing_protected', lambda: validate_handoff(changed, identity, 'd'*64, expected_protected))
    gate = {'passed': True, 'exit_code': 0, 'source_commit': identity['source_commit']}
    local_gate(json.dumps(gate).encode(), identity['source_commit'])
    controls.append('gate_commit_bound')
    rejects('gate_wrong_commit', lambda: local_gate(json.dumps(dict(gate, source_commit='f'*40)).encode(), identity['source_commit']))
    gate.pop('source_commit')
    rejects('gate_missing_commit', lambda: local_gate(json.dumps(gate).encode(), identity['source_commit']))
    with tempfile.TemporaryDirectory(prefix='fogell-upgrade-selftest-') as directory:
        root = Path(directory)
        (root/'campaigns').mkdir()
        path = root/'campaigns/upgrade-v2.json'
        before = {'controller_container_id': 'a'*64, 'postgres_container_id': 'b'*64, 'source_commit': 'a'*40}
        after = dict(before, controller_container_id='c'*64, source_commit='c'*40)
        exact = {str(i)*64: '2026-09-01T13:47:54.123456789Z' for i in range(1, 6)}
        rows = [{'id': key, 'started_at': value, 'running': True} for key, value in exact.items()]
        receipt = {'passed': True, 'cleanup_errors': [], 'old_deployment': before, 'new_deployment': after,
                   'protected_before': rows, 'protected_after': copy.deepcopy(rows)}
        def verify(value):
            path.write_text(json.dumps(value))
            return upgrade_history(Evidence(root), before, after, exact)
        require(verify(receipt)['passed'] is True, 'RFC3339 dictionary upgrade refused')
        controls.append('upgrade_exact_rfc3339_identity_dictionaries')
        for label, value in [('display_timestamp', '2026-09-01 13:47:54.123456789 +0000 UTC'),
                             ('lost_subsecond_precision', '2026-09-01T13:47:54.123456Z')]:
            changed = copy.deepcopy(receipt)
            changed['protected_before'][0]['started_at'] = value
            changed['protected_after'] = copy.deepcopy(changed['protected_before'])
            rejects('upgrade_' + label, lambda changed=changed: verify(changed))
        changed = copy.deepcopy(receipt)
        changed['protected_before'][0]['running'] = False
        changed['protected_after'] = copy.deepcopy(changed['protected_before'])
        rejects('upgrade_stopped_protected', lambda: verify(changed))
        changed = copy.deepcopy(receipt)
        changed['protected_before'] = changed['protected_after'] = ['id timestamp true'] * 5
        rejects('upgrade_legacy_string_projection', lambda: verify(changed))
    with tempfile.TemporaryDirectory(prefix='fogell-chain-selftest-') as directory:
        root = Path(directory); (root/'campaigns').mkdir()
        versions = [{'controller_container_id': c*64, 'postgres_container_id': 'f'*64, 'source_commit': c*40} for c in 'abc']
        exact = {str(i)*64: '2026-09-01T13:47:54.123456789Z' for i in range(1, 6)}
        rows = [{'id': key, 'started_at': value, 'running': True} for key, value in exact.items()]
        upgrades = [{'passed': True, 'cleanup_errors': [], 'old_deployment': versions[i], 'new_deployment': versions[i+1],
                     'protected_before': rows, 'protected_after': rows} for i in range(2)]
        def write():
            for i, value in enumerate(upgrades): (root/('campaigns/upgrade-'+str(i)+'.json')).write_text(json.dumps(value))
        write()
        found, transitions = version_history(Evidence(root), versions[0], versions[2], exact)
        require(found == versions and len(transitions) == 2, 'valid three-version chain refused')
        controls.append('complete_three_version_upgrade_chain')
        upgrades[1]['old_deployment'] = versions[0]; write()
        rejects('upgrade_chain_branch', lambda: version_history(Evidence(root), versions[0], versions[2], exact))
        upgrades[1]['old_deployment'] = versions[1]; upgrades[1]['passed'] = False; write()
        rejects('upgrade_chain_failed_transition', lambda: version_history(Evidence(root), versions[0], versions[2], exact))
        upgrades[1]['passed'] = True; write()
        extra = root/'campaigns/upgrade-unmatched.json'; extra.write_text(json.dumps(upgrades[0]))
        rejects('upgrade_chain_extra_receipt', lambda: version_history(Evidence(root), versions[0], versions[2], exact)); extra.unlink()
        unknown = root/'campaigns/adversarial-unknown'; unknown.mkdir()
        (unknown/'receipt.json').write_text(json.dumps({'declaration': {'target': {'controller_container_id': 'd'*64}}}))
        rejects('unaccounted_executed_campaign_version', lambda: version_history(Evidence(root), versions[0], versions[2], exact))
        renamed = root/'campaigns/renamed-trial'
        unknown.rename(renamed)
        (renamed/'receipt.json').write_text(json.dumps({'declaration': {'fault_repetitions': 3,
            'target': {'controller_container_id': 'd'*64}}}))
        rejects('renamed_campaign_version_not_ignored', lambda: version_history(Evidence(root), versions[0], versions[2], exact))
    with tempfile.TemporaryDirectory(prefix='fogell-setup-selftest-') as directory:
        root = Path(directory)
        setup_dir, run_dir = root/'campaigns/adversarial-v1', root/'campaigns/adversarial-v1-run2'
        setup_dir.mkdir(parents=True); run_dir.mkdir()
        target = {'controller_container_id': 'a'*64, 'postgres_container_id': 'b'*64,
                  'protected_container_ids': [str(i)*64 for i in range(1, 6)],
                  'service_unit': 'fogell.service', 'postgres_service_unit': 'fogell-postgres.service',
                  'url': 'http://127.0.0.1:46206'}
        setup = {'passed': False, 'error': 'ValueError:command_failed:podman', 'jobs': [], 'scenarios': [],
                 'declaration': {'target': target},
                 'commands': [{'argv': ['podman', 'inspect', '--format', '{{json .Id}}', '1'*64], 'exit_code': 125}]}
        raw = {'method': 'GET', 'url': target['url']+'/health/ready', 'status': 200,
               'request_bytes': 0, 'request_sha256': None}
        def write():
            (setup_dir/'receipt.json').write_text(json.dumps(setup))
            (setup_dir/'http.ndjson').write_text(json.dumps(raw)+'\n')
        write()
        executed = dict(setup, jobs=[{'label': 'actual workload'}])
        (run_dir/'receipt.json').write_text(json.dumps(executed))
        selected, retained = find_adversarial(Evidence(root), target, target['protected_container_ids'])
        require(selected == 'campaigns/adversarial-v1-run2' and len(retained) == 1
                and retained[0]['receipt_sha256'] == sha((setup_dir/'receipt.json').read_bytes()), 'setup failure dropped or executed campaign not selected')
        controls.append('readonly_setup_failure_retained_hashed_separately')
        setup['commands'].append({'argv': ['systemctl', '--user', 'stop', 'fogell.service']}); write()
        rejects('setup_mutating_command', lambda: find_adversarial(Evidence(root), target, target['protected_container_ids']))
        setup['commands'].pop(); raw['method'] = 'POST'; write()
        rejects('setup_mutating_http', lambda: find_adversarial(Evidence(root), target, target['protected_container_ids']))
        raw['method'] = 'GET'; setup['scenarios'] = [{'passed': False}]; write()
        rejects('multiple_executed_failures_not_dropped', lambda: find_adversarial(Evidence(root), target, target['protected_container_ids']))
        setup['scenarios'] = []; setup['passed'] = True; write()
        rejects('empty_passing_run_not_setup_failure', lambda: find_adversarial(Evidence(root), target, target['protected_container_ids']))
    with tempfile.TemporaryDirectory(prefix='fogell-summary-selftest-') as directory:
        root = Path(directory)
        (root/'outside').symlink_to('/etc/passwd')
        rejects('linked_evidence', lambda: Evidence(root))
    print(json.dumps({'passed': True, 'controls': controls, 'live_access': False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, help='extracted export archive root')
    parser.add_argument('--output', type=Path, help='new JSON path outside evidence; default stdout')
    parser.add_argument('--local-validation', type=Path, help='optional passing local gate receipt with explicit matching qualified source_commit')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    require(args.root is not None, '--root required')
    if args.output:
        require(not args.output.resolve().is_relative_to(args.root.resolve()), 'output must be outside evidence')
        require(not args.output.exists(), 'output already exists')
    try:
        result = summarize(args.root)
        if args.local_validation:
            raw = args.local_validation.read_bytes()
            result['local_validation'] = local_gate(raw, result['qualification_v3']['identity']['source_commit'])
    except Exception as error:
        result = {'schema_version': 1, 'passed': False, 'error': type(error).__name__+': '+str(error)}
    output = json.dumps(result, indent=2, allow_nan=False)+'\n'
    if args.output:
        with args.output.open('x') as stream:
            stream.write(output)
    else:
        print(output, end='')
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    sys.dont_write_bytecode = True
    raise SystemExit(main())
