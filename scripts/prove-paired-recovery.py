#!/usr/bin/env python3
"""FG-269 paired recovery proof for a quiesced, disposable pilot deployment.

Requires a caller-owned PostgreSQL container and private fogell_pilot* databases.
Never overwrites a database/state directory. Retains the restored pair for the
controller smoke test, which must be measured separately before claiming RTO.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical_rows(data):
    # PostgreSQL JSON escapes newlines inside cells. Hash complete parsed rows,
    # not the first physical line of a multiline INSERT statement.
    # Preserve PostgreSQL's canonical jsonb text bytes: converting JSON numbers
    # through Python float could erase distinctions in high-precision numerics.
    rows = [line.strip() for line in data.splitlines() if line.strip()]
    for row in rows:
        json.loads(row)
    return b"\n".join(sorted(rows)), len(rows)


def logical_inventory(sql, database):
    relations = json.loads(sql(database, "SELECT coalesce(json_agg(x ORDER BY x.schema, x.name), '[]'::json) FROM "
        "(SELECT n.nspname AS schema,c.relname AS name,c.relkind AS kind FROM pg_class c "
        "JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind IN ('r','p','S') "
        "AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%') x"))
    def identifier(value):
        return '"' + value.replace('"', '""') + '"'
    def literal(value):
        return "'" + value.replace("'", "''") + "'"
    queries = []
    for relation in relations:
        name = identifier(relation['schema']) + '.' + identifier(relation['name'])
        value = ("jsonb_build_object('last_value', t.last_value, 'is_called', t.is_called)"
                 if relation['kind'] == 'S' else 'to_jsonb(t)')
        queries.append("SELECT jsonb_build_array(" + literal(relation['schema']) + "," +
                       literal(relation['name']) + "," + value + ")::text FROM ONLY " + name + " t")
    payload, count = canonical_rows(sql(database, " UNION ALL ".join(queries)).encode() if queries else b"")
    return {"rows_and_sequences_sha256": sha(payload), "row_and_sequence_count": count,
            "relations_sha256": sha(json.dumps(relations, sort_keys=True, separators=(",", ":")).encode()),
            "row_encoding": "sorted-complete-json-v1"}


def inventory(root):
    require(root.is_dir() and not root.is_symlink(), "state root must be a real directory")
    result = {}
    for path in sorted(root.rglob("*")):
        info = path.lstat()
        require(stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode), "state contains a link or special file")
        result[str(path.relative_to(root))] = {"mode": stat.S_IMODE(info.st_mode),
            "kind": "directory" if path.is_dir() else "file",
            "sha256": None if path.is_dir() else sha(path.read_bytes()),
            "size": 0 if path.is_dir() else info.st_size}
    return result


def verify_pair(manifest, archive, state):
    require(sha(archive.read_bytes()) == manifest["database_archive_sha256"], "database archive integrity differs")
    actual = inventory(state)
    require(actual == manifest["state_inventory"], "paired state inventory differs")


def controls(manifest, archive, state, scratch):
    results = []
    changed = scratch / "altered.custom"
    changed.write_bytes(archive.read_bytes() + b"altered")
    for name, probe_archive, probe_state in [("altered-database", changed, state)]:
        try:
            verify_pair(manifest, probe_archive, probe_state)
        except ValueError:
            results.append({"control": name, "rejected": True})
        else:
            raise ValueError("known-bad pair accepted: " + name)
    changed.unlink()
    bad_state = scratch / "changed-state"
    shutil.copytree(state, bad_state)
    files = [p for p in bad_state.rglob("*") if p.is_file()]
    require(bool(files), "state backup must include real workload files")
    victim = files[0]
    original = victim.read_bytes()
    victim.write_bytes(original + b"altered")
    for name in ("changed-state-file", "missing-state-file"):
        if name == "missing-state-file":
            victim.unlink()
        try:
            verify_pair(manifest, archive, bad_state)
        except ValueError:
            results.append({"control": name, "rejected": True})
        else:
            raise ValueError("known-bad pair accepted: " + name)
    shutil.rmtree(bad_state)
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--container", required=True)
    parser.add_argument("--source-database", required=True)
    parser.add_argument("--target-database", required=True)
    parser.add_argument("--state-root", type=Path, required=True)
    parser.add_argument("--restored-state-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--authority-receipt", type=Path, required=True)
    parser.add_argument("--workload-proof", type=Path, required=True)
    parser.add_argument("--post-restore-command", type=Path, required=True)
    parser.add_argument("--writers-quiesced", action="store_true", required=True)
    args = parser.parse_args()
    require(re.fullmatch(r"[a-f0-9]{64}", args.container), "container must be an immutable ID")
    for name in (args.source_database, args.target_database):
        require(re.fullmatch(r"fogell_pilot[a-z0-9_]*", name), "database is outside private pilot namespace")
    require(args.source_database != args.target_database, "source and target databases are identical")
    require(not args.restored_state_root.exists(), "restored state target must not exist")
    args.output.mkdir()
    receipt = {"schema_version": 1, "passed": False, "restore_target": args.target_database,
               "rto_seconds_target": 120, "rpo_lost_records_target": 0,
               "requires_controller_control": True, "harness_sha256": sha(Path(__file__).read_bytes())}
    receipt["proof_input_sha256"] = {"workload": sha(args.workload_proof.read_bytes()),
                                    "authority": sha(args.authority_receipt.read_bytes()),
                                    "post_restore_command": sha(args.post_restore_command.read_bytes())}
    def command(argv, data=None, timeout=60):
        p = subprocess.run(argv, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
        require(p.returncode == 0, "recovery command failed: " + argv[0])
        return p.stdout
    def pg(argv, data=None):
        return command(["podman", "exec", "-i", args.container] + argv, data)
    def sql(database, text):
        return pg(["psql", "-X", "-q", "-A", "-t", "-U", "fogell", "-d", database,
                   "-v", "ON_ERROR_STOP=1"], text.encode()).decode().strip()
    def logical(database):
        result = logical_inventory(sql, database)
        schema = pg(["pg_dump", "-U", "fogell", "-d", database, "--schema-only"])
        normalized = b"\n".join(line for line in schema.splitlines() if not line.startswith((b"\\restrict ", b"\\unrestrict ")))
        result["schema_sha256"] = sha(normalized)
        return result
    try:
        require(sql("postgres", f"SELECT count(*) FROM pg_database WHERE datname='{args.target_database}'") == "0",
                "restore database already exists")
        active = sql(args.source_database, "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() "
                     "AND pid<>pg_backend_pid() AND backend_type='client backend'")
        require(active == "0", "source database still has client sessions; quiesce writers")
        workload = json.loads(args.workload_proof.read_text())
        identifiers = {k: str(uuid.UUID(workload[k])) for k in ("organization", "project", "build", "attempt")}
        belongs = sql(args.source_database,
            f"SELECT count(*) FROM builds b JOIN nodes n ON n.organization_id=b.organization_id AND n.build_id=b.id "
            f"JOIN attempts a ON a.organization_id=n.organization_id AND a.node_id=n.id "
            f"WHERE b.organization_id='{identifiers['organization']}' AND b.project_id='{identifiers['project']}' "
            f"AND b.id='{identifiers['build']}' AND a.id='{identifiers['attempt']}' AND a.state='terminal' "
            f"AND NOT EXISTS(SELECT 1 FROM build_retention r WHERE r.organization_id=b.organization_id AND r.build_id=b.id)")
        require(belongs == "1", "saved workload does not belong to a retained terminal database attempt")
        artifact_path = (Path("workspaces") / uuid.UUID(identifiers["organization"]).hex /
                         "_artifact-snapshots" / uuid.UUID(identifiers["attempt"]).hex / "reports/domain.xml")
        require(sha((args.state_root / artifact_path).read_bytes()) == workload["junit_sha256"],
                "state root is not the database's measured workload pair")
        source_inventory = inventory(args.state_root)
        archive = args.output / "database.custom"
        archive.write_bytes(pg(["pg_dump", "-U", "fogell", "-d", args.source_database, "--format=custom"]))
        require(archive.stat().st_size > 0, "empty database archive")
        require(bool(pg(["pg_restore", "--list"], archive.read_bytes())), "empty archive inventory")
        backup_state = args.output / "state"
        shutil.copytree(args.state_root, backup_state)
        require(inventory(args.state_root) == source_inventory, "source state changed during backup")
        manifest = {"schema_version": 1, "source_database": args.source_database,
                    "database_archive_sha256": sha(archive.read_bytes()), "state_inventory": source_inventory,
                    "database_inventory": logical(args.source_database)}
        (args.output / "pair-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        verify_pair(manifest, archive, backup_state)
        receipt["controls"] = controls(manifest, archive, backup_state, args.output)
        origin = time.monotonic()
        receipt["recovery_start_monotonic"] = origin
        sql("postgres", f"CREATE DATABASE {args.target_database}")
        pg(["pg_restore", "-U", "fogell", "-d", args.target_database, "--single-transaction", "--exit-on-error"], archive.read_bytes())
        shutil.copytree(backup_state, args.restored_state_root)
        require(logical(args.target_database) == manifest["database_inventory"], "restored database differs")
        require(inventory(args.restored_state_root) == source_inventory, "restored state differs")
        builder = os.environ["FOGELL_MAINTENANCE_DATABASE_URL"]
        require(re.search(r"Database=" + re.escape(args.target_database) + r"(?:;|$)", builder), "activation connection must name target")
        activation = command(["dotnet", str(ROOT / "tools/Fogell.Recovery/bin/Release/net10.0/Fogell.Recovery.dll"),
                              "activate-restore", "--writers-quiesced"])
        receipt["activation"] = json.loads(activation)
        probe = command(["dotnet", "fsi", "--exec", str(ROOT / "scripts/prove-paired-recovery-stale.fsx"),
                         "check", str(args.authority_receipt)])
        receipt["stale_authority"] = json.loads(probe.decode().splitlines()[-1])
        require(receipt["stale_authority"]["passed"] is True, "stale authority check failed")
        receipt["restored_pair_elapsed_seconds"] = time.monotonic() - origin
        require(receipt["restored_pair_elapsed_seconds"] <= 120, "pair restore exceeded recovery target")
        post_command = json.loads(args.post_restore_command.read_text())
        require(isinstance(post_command, list) and post_command and all(isinstance(v, str) for v in post_command),
                "post-restore command must be a nonempty argument vector")
        result = command(post_command, timeout=max(1, 120 - (time.monotonic() - origin)))
        receipt["controller_control"] = json.loads(result)
        require(receipt["controller_control"]["passed"] is True, "restored controller control failed")
        require(receipt["controller_control"]["previous_artifact_sha256"] == workload["junit_sha256"],
                "restored API did not return the original attempt artifact")
        receipt["recovery_elapsed_seconds"] = time.monotonic() - origin
        require(receipt["recovery_elapsed_seconds"] <= 120, "controller recovery exceeded declared RTO")
        receipt["requires_controller_control"] = False
        receipt["lost_backed_up_rows"] = 0
        receipt["lost_backed_up_files"] = 0
        receipt["passed"] = True
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
