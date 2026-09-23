#!/usr/bin/env python3
"""Portable checker controls; no database or real recovery claim."""
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("recovery", Path(__file__).with_name("prove-paired-recovery.py"))
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


def row(body):
    return json.dumps(["public", "logs", {"body": body, "nullable": None}]).encode()


original = recovery.canonical_rows(row("same first line\noriginal second line"))
altered = recovery.canonical_rows(row("same first line\naltered second line"))
assert original != altered, "multiline cell mutation was omitted"
assert recovery.canonical_rows(row("a") + b"\n" + row("b")) == recovery.canonical_rows(row("b") + b"\n" + row("a"))
assert recovery.canonical_rows(row("a") + b"\n" + row("a")) != recovery.canonical_rows(row("a")), "duplicate row lost"
assert recovery.canonical_rows(row("a\\nb")) != recovery.canonical_rows(row("a\nb")), "escaped newline confused"
assert recovery.canonical_rows(b'["public", "values", {"n": 0.123456789012345678901}]') != recovery.canonical_rows(b'["public", "values", {"n": 0.123456789012345678902}]'), "numeric precision lost"

queries = []
def fake_sql(database, query):
    assert database == "fixture"
    queries.append(query)
    if "pg_class" in query:
        return json.dumps([{"schema": "odd'schema", "name": 'a"table', "kind": "r"},
                           {"schema": "public", "name": "counter", "kind": "S"}])
    return row("complete\ncell").decode()

inventory = recovery.logical_inventory(fake_sql, "fixture")
assert inventory["row_encoding"] == "sorted-complete-json-v1"
assert inventory["row_and_sequence_count"] == 1
assert 'FROM ONLY "odd\'schema"."a""table" t' in queries[1]
assert "'odd''schema'" in queries[1] and "'is_called', t.is_called" in queries[1]
print(json.dumps({"passed": True, "synthetic_checker_controls": True, "real_recovery_proof": False,
                  "controls": ["multiline-cell", "row-order", "duplicate-row", "escaped-newline", "numeric-precision", "quoted-identifiers", "sequence-state"]}))
