#!/usr/bin/env python3
"""FG-269: terminate an uncommitted newest migration, compare and retry.

Only generated fogell_fg269_upgrade_* databases in an explicit owned container
are created or removed. Uses the actual compiled Store migrator for both retries.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent
PREFIX = "fogell_fg269_upgrade_"
LIMIT = 32 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def checked(command, *, data=None, env=None, timeout=30):
    result = subprocess.run(command, input=data, capture_output=True, timeout=timeout, env=env)
    require(len(result.stdout) + len(result.stderr) <= LIMIT, "subprocess output exceeded bound")
    require(result.returncode == 0, "subprocess failed: " + result.stderr.decode(errors="replace")[-2000:])
    return result.stdout


def normalize_dump(data):
    text = data.decode("utf-8")
    return re.sub(r"^\\(un)?restrict [^\n]+$", lambda m: "\\" + (m[1] or "") + "restrict FG269_TRANSPORT_KEY", text, flags=re.M).encode()


def compare(expected, actual):
    require(set(expected) == set(actual), "inventory components differ")
    for name in expected:
        require(expected[name] == actual[name], name + " differs")


def controls(inventory):
    results = []
    for name in inventory:
        bad = dict(inventory)
        bad[name] += b"altered"
        try:
            compare(inventory, bad)
        except ValueError:
            results.append({"component": name, "rejected": True})
        else:
            raise ValueError("known-bad inventory accepted: " + name)
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--container", required=True)
    parser.add_argument("--runtime", choices=("docker", "podman"), default="podman")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--migrator", type=Path, default=ROOT / "tools/Fogell.Retention/bin/Release/net10.0/Fogell.Retention.dll")
    args = parser.parse_args()
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", args.container), "invalid container identity")
    require(0 < args.port < 65536, "invalid PostgreSQL port")
    args.output.mkdir(parents=True, exist_ok=False)
    token = uuid.uuid4().hex
    database = PREFIX + token
    require(re.fullmatch(PREFIX + r"[0-9a-f]{32}", database), "invalid owned database identity")
    application = "fg269_interrupt_" + token
    command = [args.runtime, "exec", "-i", args.container]

    def sql(db, statement, timeout=30):
        return checked(command + ["psql", "-X", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-U", "fogell", "-d", db], data=statement.encode(), timeout=timeout)

    def inventory(label):
        schema = normalize_dump(checked(command + ["pg_dump", "-U", "fogell", "-d", database, "--schema-only", "--quote-all-identifiers"]))
        # INSERT order and pg_dump's random transport guard are not data semantics.
        data = checked(command + ["pg_dump", "-U", "fogell", "-d", database, "--data-only", "--inserts", "--column-inserts", "--rows-per-insert=1", "--exclude-table=schema_migrations"])
        rows = b"\n".join(sorted(line for line in data.splitlines() if line.startswith(b"INSERT INTO "))) + b"\n"
        ledger = sql(database, "SELECT version || E'\\t' || checksum || E'\\t' || applied_at::text FROM schema_migrations ORDER BY version;")
        sequences = sql(database, """SELECT format('SELECT %L || E''\\t'' || last_value::text || E''\\t'' || is_called::text FROM %I.%I',
            n.nspname || '.' || c.relname,n.nspname,c.relname)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind='S' AND n.nspname='public' ORDER BY c.relname;
            \\gexec
            """)
        values = {"schema": schema, "data": rows, "ledger": ledger, "sequences": sequences}
        for name, value in values.items():
            require(bool(value.strip()), "empty " + name + " inventory")
            (args.output / (label + "." + name)).write_bytes(value)
        return values

    migrations = sorted((ROOT / "src/Fogell.Store/migrations").glob("*.sql"))
    require(len(migrations) >= 2, "missing migration history")
    versions = [path.name.split("_")[0] for path in migrations]
    require(versions == [f"{n:04}" for n in range(1, len(migrations) + 1)], "non-contiguous migration history")
    migration_bytes = [path.read_bytes() for path in migrations]
    checksums = [sha(data) for data in migration_bytes]
    latest = versions[-1]
    receipt = {"schema_version": 1, "started_at_utc": datetime.now(timezone.utc).isoformat(),
               "database": database, "container": args.container, "latest": latest,
               "harness_sha256": sha(Path(__file__).read_bytes()),
               "migrations": dict(zip(versions, checksums)), "migrator_sha256": sha(args.migrator.read_bytes()),
               "store_binary_sha256": sha((args.migrator.parent / "Fogell.Store.dll").read_bytes()),
               "passed": False, "cleanup_confirmed": False}
    created = False
    process = None
    try:
        require(sql("fogell", f"SELECT count(*) FROM pg_database WHERE datname='{database}';").strip() == b"0", "owned name collision")
        sql("fogell", f"CREATE DATABASE {database};")
        created = True
        sql(database, "CREATE TABLE schema_migrations(version text PRIMARY KEY,checksum text NOT NULL,applied_at timestamptz NOT NULL DEFAULT clock_timestamp());")
        for version, checksum, payload in zip(versions[:-1], checksums[:-1], migration_bytes[:-1]):
            sql(database, "BEGIN;\n" + payload.decode() + f"\nINSERT INTO schema_migrations(version,checksum) VALUES ('{version}','{checksum}'); COMMIT;")
        sql(database, """
            INSERT INTO organizations(id,slug) VALUES ('10000000-0000-0000-0000-000000000269','fg269-upgrade');
            INSERT INTO projects(id,organization_id,slug) VALUES ('20000000-0000-0000-0000-000000000269','10000000-0000-0000-0000-000000000269','probe');
            INSERT INTO builds(id,organization_id,project_id,number,idempotency_key,status)
              VALUES ('30000000-0000-0000-0000-000000000269','10000000-0000-0000-0000-000000000269','20000000-0000-0000-0000-000000000269',1,'upgrade-probe','failure');
            INSERT INTO nodes(id,organization_id,build_id,name,ordinal,required_trust_pool,required_capabilities,status)
              VALUES ('40000000-0000-0000-0000-000000000269','10000000-0000-0000-0000-000000000269','30000000-0000-0000-0000-000000000269','probe',0,'trusted-linux',ARRAY['linux'],'failure');
            INSERT INTO attempts(id,organization_id,node_id,ordinal,state,fence,result)
              VALUES ('50000000-0000-0000-0000-000000000269','10000000-0000-0000-0000-000000000269','40000000-0000-0000-0000-000000000269',0,'terminal',1,'failure');
            INSERT INTO log_chunks(organization_id,build_id,attempt_id,sequence,build_sequence,body)
              VALUES ('10000000-0000-0000-0000-000000000269','30000000-0000-0000-0000-000000000269','50000000-0000-0000-0000-000000000269',0,0,'durable before upgrade π');
            """)
        before = inventory("before")
        archive = checked(command + ["pg_dump", "-U", "fogell", "-d", database, "--format=custom"])
        (args.output / "before.custom").write_bytes(archive)
        receipt["pre_upgrade_archive_sha256"] = sha(archive)
        interrupted_sql = (f"SET application_name='{application}'; BEGIN;\n" + migration_bytes[-1].decode()
                           + f"\nINSERT INTO schema_migrations(version,checksum) VALUES ('{latest}','{checksums[-1]}');"
                           + f"\nSELECT 'FG269_APPLIED|' || checksum FROM schema_migrations WHERE version='{latest}';"
                           + "\nSELECT pg_sleep(60) /* fg269_uncommitted_barrier */; COMMIT;\n")
        process = subprocess.Popen(command + ["psql", "-X", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-U", "fogell", "-d", database],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        process.stdin.write(interrupted_sql.encode())
        process.stdin.close()
        process.stdin = None
        deadline = time.monotonic() + 15
        pid = None
        while time.monotonic() < deadline:
            found = sql("fogell", f"SELECT pid FROM pg_stat_activity WHERE datname='{database}' AND application_name='{application}' AND wait_event='PgSleep' AND query LIKE '%fg269_uncommitted_barrier%';", timeout=5).strip()
            if found:
                require(re.fullmatch(rb"[0-9]+", found), "ambiguous barrier backend")
                pid = int(found)
                break
            require(process.poll() is None, "migration stopped before uncommitted barrier")
            time.sleep(0.05)
        require(pid is not None, "migration never reached uncommitted barrier")
        terminated = sql("fogell", f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid={pid} AND datname='{database}' AND application_name='{application}';").strip()
        require(terminated == b"t", "owned backend was not terminated")
        stdout, stderr = process.communicate(timeout=10)
        require(process.returncode != 0, "terminated migration unexpectedly completed")
        require(("FG269_APPLIED|" + checksums[-1]).encode() in stdout, "migration and ledger did not reach pre-commit barrier")
        (args.output / "interrupted.stdout").write_bytes(stdout)
        (args.output / "interrupted.stderr").write_bytes(stderr)
        after = inventory("interrupted")
        compare(before, after)
        require(sql(database, f"SELECT count(*) FROM schema_migrations WHERE version='{latest}';").strip() == b"0", "uncommitted ledger survived")
        receipt["terminated_backend"] = pid
        receipt["interrupted_exit"] = process.returncode
        receipt["before_hashes"] = {name: sha(value) for name, value in before.items()}
        receipt["interrupted_hashes"] = {name: sha(value) for name, value in after.items()}
        receipt["controls"] = controls(before)
        env = dict(os.environ, FOGELL_MAINTENANCE_DATABASE_URL=f"Host=127.0.0.1;Port={args.port};Username=fogell;Database={database}")
        retry = checked(["dotnet", str(args.migrator), "migrate"], env=env, timeout=60)
        receipt["retry"] = json.loads(retry)
        require(receipt["retry"].get("migrated") == len(migrations), "embedded migrator count differs")
        final = inventory("retried")
        require(final["data"] == before["data"], "successful upgrade changed seeded workload data")
        require(final["sequences"] == before["sequences"], "successful upgrade changed seeded sequence state")
        expected_ledger = "".join(f"{v}\t{h}\n" for v, h in zip(versions, checksums)).encode()
        require(sql(database, "SELECT version || E'\\t' || checksum FROM schema_migrations ORDER BY version;") == expected_ledger, "embedded migration ledger differs from checked source")
        checked(["dotnet", str(args.migrator), "migrate"], env=env, timeout=60)
        compare(final, inventory("repeated"))
        receipt["retry_hashes"] = {name: sha(value) for name, value in final.items()}
        require(sha(args.migrator.read_bytes()) == receipt["migrator_sha256"], "migrator binary changed during drill")
        require(sha((args.migrator.parent / "Fogell.Store.dll").read_bytes()) == receipt["store_binary_sha256"], "Store binary changed during drill")
        require([sha(path.read_bytes()) for path in migrations] == checksums, "migration sources changed during drill")
        receipt["binary_and_source_stability_confirmed"] = True
        receipt["passed"] = True
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        if process is not None and process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.communicate(timeout=5)
        if created:
            try:
                sql("fogell", f"DROP DATABASE {database} WITH (FORCE);")
                receipt["cleanup_confirmed"] = sql("fogell", f"SELECT count(*) FROM pg_database WHERE datname='{database}';").strip() == b"0"
            except Exception as error:
                receipt["cleanup_error"] = str(error)
                receipt["passed"] = False
        (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
        require(not created or receipt["cleanup_confirmed"], "owned database cleanup not confirmed")
    print(json.dumps({"passed": True, "receipt": str(args.output / "receipt.json"), "cleanup_confirmed": True}))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit("FG-269 UPGRADE REFUSED: " + str(error))
