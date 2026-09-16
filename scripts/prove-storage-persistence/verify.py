#!/usr/bin/env python3
"""Verify the fixed schema emitted by campaign.py and vm.py."""

import argparse
import copy
import hashlib
import json
import re
import sys
from pathlib import Path


HEX = re.compile(r"^[0-9a-f]{64}$")
CYCLES = (1, 2, 3)
CUT_LABELS = tuple(
    label
    for cycle in CYCLES
    for label in (f"active-{cycle}", f"receipt-before-clear-{cycle}", f"after-clear-{cycle}")
)
BOOT_LABELS = ("campaign-start", "clean-control-reboot") + tuple(
    label
    for cycle in CYCLES
    for label in (f"active-reboot-{cycle}", f"receipt-reboot-{cycle}", f"idle-reboot-{cycle}")
)
DISKS = {"os.qcow2", "pool.raw", "state.raw", "postgres.raw"}
DRIVES = (
    ("/vm/os.qcow2", "qcow2"),
    ("/vm/pool.raw", "raw"),
    ("/vm/state.raw", "raw"),
    ("/vm/postgres.raw", "raw"),
)
MOUNTS = {
    "/srv/fogell/state": "/dev/vdc",
    "/srv/fogell/state/workspaces": "/dev/vdb",
    "/var/lib/postgresql": "/dev/vdd",
}
POOL_ID = "ext4persistence20260916"
IDLE_SHA256 = hashlib.sha256(bytes(4096)).hexdigest()


class EvidenceError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def load_jsonl(path):
    rows = []
    for number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError as error:
            raise EvidenceError(f"{path}:{number}: invalid JSON: {error.msg}") from error
        require(isinstance(value, dict) and isinstance(value.get("test"), str), f"{path}:{number}: missing test")
        rows.append(value)
    require(rows, f"{path}: no records")
    return rows


def load_json(path):
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise EvidenceError(f"{path}: invalid JSON: {error.msg}") from error
    require(isinstance(value, dict), f"{path}: expected a JSON object")
    return value


def one(rows, test, cycle=None):
    found = [row for row in rows if row["test"] == test and (cycle is None or row.get("cycle") == cycle)]
    suffix = "" if cycle is None else f" cycle {cycle}"
    require(len(found) == 1, f"expected exactly one {test}{suffix}, found {len(found)}")
    return found[0]


def sha(value, description):
    require(isinstance(value, str) and HEX.fullmatch(value), f"{description} is not a SHA-256")


def state(snapshot, dirty, description):
    require(isinstance(snapshot, dict), f"{description}: state is absent")
    pool = snapshot.get(".fogell-pool-state")
    marker = snapshot.get(".fogell-pool-id")
    require(isinstance(pool, dict) and isinstance(marker, dict), f"{description}: metadata entries missing")
    require(pool.get("size") == 4096 and pool.get("mode") == "0o600", f"{description}: state is not 4096/0600")
    require(pool.get("nonzero") is dirty, f"{description}: dirty flag is wrong")
    sha(pool.get("sha256"), f"{description}: state digest")
    if dirty:
        require(pool["sha256"] != IDLE_SHA256, f"{description}: dirty state has the zero-buffer digest")
    else:
        require(pool["sha256"] == IDLE_SHA256, f"{description}: idle state is not the 4096-byte zero buffer")
    require(marker.get("mode") == "0o600", f"{description}: marker is not 0600")
    sha(marker.get("sha256"), f"{description}: marker digest")
    return pool["sha256"]


def success_control(row, name, idle):
    require(row.get("status") == "success" and row.get("bounded_tmpdir") is True, f"{name}: build control failed")
    require(state(row.get("state"), False, name) == state(idle, False, "baseline"), f"{name}: idle state digest changed")
    require(row["state"] == idle, f"{name}: metadata identity changed")


def pressure(row, kind):
    require(row.get("durable_reason") == f"storage_pool_{kind}_pressure", f"{kind}: wrong durable reason")
    require(row.get("state_writable") is True and row.get("queued_without_runner") is True, f"{kind}: control outcome absent")
    require(row.get("relief_status") == "success" and "No space left on device" in str(row.get("kernel_enospc")), f"{kind}: ENOSPC/relief evidence absent")
    exhausted = row.get("exhausted", {})
    key = "available_bytes" if kind == "byte" else "available_inodes"
    require(exhausted.get(key) == 0, f"{kind}: kernel counter did not reach zero")


def receipt_values(receipts, dirty_sha, count, description):
    require(isinstance(receipts, dict) and len(receipts) == count, f"{description}: expected {count} receipts")
    for filename, item in receipts.items():
        require(isinstance(filename, str) and isinstance(item, dict), f"{description}: malformed receipt")
        sha(item.get("sha256"), f"{description}: receipt digest")
        record = item.get("record")
        require(isinstance(record, dict), f"{description}: receipt record absent")
        body = json.dumps(record, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
        digest = hashlib.sha256(body).hexdigest()
        require(item["sha256"] == digest, f"{description}: receipt content/digest mismatch")
        require(filename.endswith(f"-{digest[:16]}.json"), f"{description}: receipt filename digest suffix mismatch")
        require(record.get("action") == "explicit_recovery" and record.get("pool_id") == POOL_ID, f"{description}: receipt identity is wrong")
        require(record.get("old_record_sha256") == dirty_sha, f"{description}: receipt protects another state")
        require(record.get("writers_extinct_attested") == "true", f"{description}: no extinction attestation")


def verify_results(rows):
    topology = one(rows, "durable_topology")
    require(topology.get("postgres_parameters") == "on\non\non", "durable topology lacks PostgreSQL sync settings")
    pool = topology.get("pool", {})
    require(pool.get("total_bytes", 0) > 0 and pool.get("total_inodes", 0) > 0, "durable topology lacks pool counters")

    baseline = one(rows, "baseline_control")
    idle = baseline.get("state")
    state(idle, False, "baseline")
    success_control(baseline, "baseline", idle)
    success_control(one(rows, "before_clean_shutdown"), "before clean shutdown", idle)
    success_control(one(rows, "clean_reboot_control"), "clean reboot", idle)
    pressure(one(rows, "ext4_bytes_pressure"), "byte")
    pressure(one(rows, "ext4_inodes_pressure"), "inode")

    cycle_tests = {
        "active_checkpoint", "dirty_reboot_refused", "recovery_refused",
        "receipt_fsync_checkpoint", "receipt_reboot_refused",
        "explicit_recovery", "recovered_idle_reboot",
    }
    require(all(row.get("cycle") in CYCLES for row in rows if row["test"] in cycle_tests), "unexpected cycle evidence")
    helper_hashes = []
    for cycle in CYCLES:
        active = one(rows, "active_checkpoint", cycle)
        dirty = active.get("state")
        dirty_sha = state(dirty, True, f"cycle {cycle} active")
        require("Fogell.Run.Host" in str(active.get("processes")), f"cycle {cycle}: no active worker process")

        refused = one(rows, "dirty_reboot_refused", cycle)
        require(refused.get("state") == dirty and refused.get("readiness") == 503 and refused.get("no_runner") is True, f"cycle {cycle}: dirty reboot was not refused")
        require(isinstance(refused.get("queued_build"), str) and refused["queued_build"], f"cycle {cycle}: no queued build")

        negatives = [row for row in rows if row["test"] == "recovery_refused" and row.get("cycle") == cycle]
        require(len(negatives) == 2 and {row.get("reason") for row in negatives} == {"missing-attestation", "wrong-hash"}, f"cycle {cycle}: recovery negative controls incomplete")
        for negative in negatives:
            require(negative.get("exit_code", 0) != 0 and negative.get("dirty_sha256") == dirty_sha and negative.get("no_receipt") is True, f"cycle {cycle}: negative recovery did not fail closed")

        checkpoint = one(rows, "receipt_fsync_checkpoint", cycle)
        require(checkpoint.get("state") == dirty, f"cycle {cycle}: receipt checkpoint changed dirty state")
        witness = checkpoint.get("witness", {})
        require(witness.get("successful_fsync") is True, f"cycle {cycle}: receipt fsync was not witnessed")
        sha(witness.get("helper_sha256"), f"cycle {cycle}: helper digest")
        helper_hashes.append(witness["helper_sha256"])
        identity = witness.get("fd_identity", {})
        require(isinstance(identity.get("device"), int) and isinstance(identity.get("inode"), int), f"cycle {cycle}: receipt fd identity absent")
        sealed = checkpoint.get("receipts")
        receipt_values(sealed, dirty_sha, 1, f"cycle {cycle}: sealed receipt")

        reboot_refused = one(rows, "receipt_reboot_refused", cycle)
        require(reboot_refused.get("state") == dirty and reboot_refused.get("receipts") == sealed, f"cycle {cycle}: receipt changed across reboot")
        require(reboot_refused.get("readiness") == 503 and reboot_refused.get("no_runner") is True, f"cycle {cycle}: sealed dirty pool started work")

        recovered = one(rows, "explicit_recovery", cycle)
        require(recovered.get("old_sha256") == dirty_sha and recovered.get("old_writers_extinct") is True, f"cycle {cycle}: correct recovery lacks attestation")
        require(recovered.get("state") == idle, f"cycle {cycle}: clear did not restore exact idle metadata")
        complete = recovered.get("receipts")
        receipt_values(complete, dirty_sha, 2, f"cycle {cycle}: complete receipts")
        require(all(complete.get(name) == value for name, value in sealed.items()), f"cycle {cycle}: sealed receipt changed")

        post = one(rows, "recovered_idle_reboot", cycle)
        require(post.get("state") == idle and post.get("receipts") == complete, f"cycle {cycle}: clear/reboot metadata changed")
        require(post.get("status") == "success" and post.get("queued_build") == refused["queued_build"], f"cycle {cycle}: refused queued build did not succeed")

    require(len(set(helper_hashes)) == 1, "helper digest changed between cutpoints")

    final = one(rows, "campaign_pass")
    require({key: final.get(key) for key in ("active_cuts", "receipt_before_clear_cuts", "after_clear_cuts", "clean_reboots")} == {"active_cuts": 3, "receipt_before_clear_cuts": 3, "after_clear_cuts": 3, "clean_reboots": 1}, "campaign pass counts are wrong")


def qemu_arguments(args, description):
    require(isinstance(args, list) and args[:1] == ["qemu-system-x86_64"], f"{description}: missing QEMU command")
    require(any(args[index:index + 2] == ["-accel", "kvm"] for index in range(len(args))), f"{description}: KVM not selected")
    drives = [args[index + 1] for index, item in enumerate(args[:-1]) if item == "-drive"]
    require(len(drives) == 4, f"{description}: wrong drive count")
    parsed = []
    for drive in drives:
        fields = {}
        for part in drive.split(","):
            key, separator, value = part.partition("=")
            require(separator and key and value and key not in fields, f"{description}: malformed QEMU drive")
            fields[key] = value
        require(
            set(fields) == {"file", "format", "if", "cache", "aio", "discard"},
            f"{description}: unexpected QEMU drive fields",
        )
        require(
            fields["if"] == "virtio"
            and fields["cache"] == "none"
            and fields["aio"] == "native"
            and fields["discard"] == "ignore",
            f"{description}: unsafe QEMU drive arguments",
        )
        parsed.append(fields)
    require([(item["file"], item["format"]) for item in parsed] == list(DRIVES), f"{description}: wrong QEMU disk order, files, or formats")


def flattened_mounts(document):
    roots = document.get("filesystems")
    require(isinstance(roots, list), "mount evidence has no filesystems list")
    result = []

    def visit(item):
        require(isinstance(item, dict), "mount evidence has a non-object filesystem")
        result.append(item)
        children = item.get("children", [])
        require(isinstance(children, list), "mount evidence has non-list filesystem children")
        for child in children:
            visit(child)

    for root in roots:
        visit(root)
    return result


def verify_mounts(document):
    entries = flattened_mounts(document)
    selected = {}
    for target, source in MOUNTS.items():
        matching = [entry for entry in entries if entry.get("target") == target]
        require(len(matching) == 1, f"mount evidence has {len(matching)} entries for {target}")
        entry = matching[0]
        require(entry.get("source") == source and entry.get("fstype") == "ext4", f"mount evidence maps {target} to the wrong source or type")
        require(isinstance(entry.get("uuid"), str) and entry["uuid"], f"mount evidence has no UUID for {target}")
        require(isinstance(entry.get("options"), str) and entry["options"], f"mount evidence has no options for {target}")
        selected[target] = entry
    require(len({entry["source"] for entry in selected.values()}) == len(MOUNTS), "persistent mount sources are shared")
    require(len({entry["uuid"] for entry in selected.values()}) == len(MOUNTS), "persistent mount UUIDs are shared")


def verify_events(rows):
    starts = [row for row in rows if row["test"] == "vm_start"]
    require(starts, "no VM starts")
    identities = None
    for event in starts:
        qemu_arguments(event.get("qemu_args"), f"VM start {event.get('label')}")
        disks = event.get("disks")
        require(isinstance(disks, dict) and set(disks) == DISKS, f"VM start {event.get('label')}: disk identities absent")
        for disk in disks.values():
            require(isinstance(disk.get("device"), int) and isinstance(disk.get("inode"), int), "disk identity malformed")
        require(len({(disk["device"], disk["inode"]) for disk in disks.values()}) == len(DISKS), f"VM start {event.get('label')}: disk files are not distinct")
        if identities is None:
            identities = disks
        else:
            require(disks == identities, "disk identities changed between VM starts")

    expected_starts = [row for row in starts if row.get("label") in BOOT_LABELS]
    require(len(expected_starts) == len(BOOT_LABELS) and {row.get("label") for row in expected_starts} == set(BOOT_LABELS), "missing or duplicate campaign VM starts")
    boot_container_ids = [row.get("container_id") for row in expected_starts]
    require(all(isinstance(value, str) and value for value in boot_container_ids) and len(set(boot_container_ids)) == len(boot_container_ids), "boot container IDs are not distinct")
    boots = [row for row in rows if row["test"] == "guest_boot" and row.get("label") in BOOT_LABELS]
    require(len(boots) == len(BOOT_LABELS) and {row.get("label") for row in boots} == set(BOOT_LABELS), "missing campaign guest boots")
    boot_ids = [row.get("boot_id") for row in boots]
    require(all(isinstance(value, str) and value for value in boot_ids) and len(set(boot_ids)) == len(boot_ids), "guest boot IDs are not distinct")

    cuts = [row for row in rows if row["test"] == "vm_power_cut"]
    require(len(cuts) == 9 and {row.get("label") for row in cuts} == set(CUT_LABELS), "expected nine named power cuts")
    cut_ids = [row.get("container_id") for row in cuts]
    require(all(isinstance(value, str) and value for value in cut_ids) and len(set(cut_ids)) == 9, "power-cut container IDs are not distinct")
    for cut in cuts:
        require(cut.get("exit_code") == 137 and cut.get("disk_identities_unchanged") is True, f"power cut {cut.get('label')} was not SIGKILL/identity-stable")
    starts_by_label = {row["label"]: row for row in expected_starts}
    expected_predecessor = {}
    for cycle in CYCLES:
        expected_predecessor[f"active-{cycle}"] = "clean-control-reboot" if cycle == 1 else f"idle-reboot-{cycle - 1}"
        expected_predecessor[f"receipt-before-clear-{cycle}"] = f"active-reboot-{cycle}"
        expected_predecessor[f"after-clear-{cycle}"] = f"receipt-reboot-{cycle}"
    for cut in cuts:
        predecessor = starts_by_label[expected_predecessor[cut["label"]]]
        require(cut["container_id"] == predecessor["container_id"], f"power cut {cut['label']} does not match its preceding boot")

    clean = [row for row in rows if row["test"] == "vm_clean_shutdown"]
    require(len(clean) == 2 and {row.get("label") for row in clean} == {"clean-control", "campaign-complete"}, "clean shutdown evidence is incomplete")
    require(all(row.get("exit_code") == 0 and row.get("disk_identities_unchanged") is True for row in clean), "clean shutdown invalid")
    clean_predecessors = {"clean-control": "campaign-start", "campaign-complete": "idle-reboot-3"}
    for shutdown in clean:
        predecessor = starts_by_label[clean_predecessors[shutdown["label"]]]
        require(shutdown.get("container_id") == predecessor.get("container_id"), f"clean shutdown {shutdown['label']} does not match its preceding boot")


def verify(results, events, mounts):
    verify_results(results)
    verify_events(events)
    verify_mounts(mounts)


def reject(results, events, mounts, label):
    try:
        verify(results, events, mounts)
    except EvidenceError:
        return
    raise EvidenceError(f"self-test mutation was accepted: {label}")


def self_test(results, events, mounts):
    def changed(mutator, label):
        copied_results, copied_events, copied_mounts = copy.deepcopy(results), copy.deepcopy(events), copy.deepcopy(mounts)
        mutator(copied_results, copied_events, copied_mounts)
        reject(copied_results, copied_events, copied_mounts, label)

    changed(lambda r, e, m: one(r, "active_checkpoint", 1)["state"][".fogell-pool-state"].update(sha256="0" * 64), "dirty SHA")
    changed(lambda r, e, m: one(r, "receipt_fsync_checkpoint", 1).update(receipts={}), "missing receipt")
    changed(lambda r, e, m: one(r, "active_checkpoint", 1)["state"][".fogell-pool-state"].update(nonzero=False), "inactive active state")
    changed(lambda r, e, m: one(r, "dirty_reboot_refused", 1).update(readiness=200), "ready dirty pool")
    changed(lambda r, e, m: one(r, "dirty_reboot_refused", 1).update(no_runner=False), "runner flag")
    changed(lambda r, e, m: one(r, "recovered_idle_reboot", 1).update(queued_build="other-build"), "wrong queued build")
    changed(lambda r, e, m: r.__delitem__(next(index for index, row in enumerate(r) if row.get("cycle") == 3)), "missing cycle record")
    changed(lambda r, e, m: e.__delitem__(next(index for index, row in enumerate(e) if row.get("test") == "vm_power_cut")), "missing cut")
    changed(lambda r, e, m: next(row for row in e if row.get("test") == "vm_power_cut").update(exit_code=0), "wrong SIGKILL exit")
    def unsafe_cache(_, events, __):
        args = next(row for row in events if row.get("test") == "vm_start")["qemu_args"]
        index = next(index for index, item in enumerate(args) if "cache=none" in item)
        args[index] = args[index].replace("cache=none", "cache=unsafe")
    changed(unsafe_cache, "unsafe disk cache")
    def unsafe_discard(_, events, __):
        args = next(row for row in events if row.get("test") == "vm_start")["qemu_args"]
        index = next(index for index, item in enumerate(args) if "discard=ignore" in item)
        args[index] = args[index].replace("discard=ignore", "discard=unmap")
    changed(unsafe_discard, "unsafe disk discard")
    def wrong_idle_sha(rows, _, __):
        for row in rows:
            snapshot = row.get("state")
            if isinstance(snapshot, dict) and snapshot.get(".fogell-pool-state", {}).get("nonzero") is False:
                snapshot[".fogell-pool-state"]["sha256"] = "f" * 64
    changed(wrong_idle_sha, "wrong zero-buffer digest")
    changed(lambda r, e, m: next(iter(one(r, "receipt_fsync_checkpoint", 1)["receipts"].values()))["record"].update(operator_note="tampered"), "receipt body tamper")
    changed(lambda r, e, m: next(iter(one(r, "receipt_fsync_checkpoint", 1)["receipts"].values())).update(sha256="0" * 64), "receipt digest tamper")
    changed(lambda r, e, m: next(entry for entry in flattened_mounts(m) if entry.get("target") == "/srv/fogell/state/workspaces").update(fstype="tmpfs"), "pool not ext4")
    changed(lambda r, e, m: next(entry for entry in flattened_mounts(m) if entry.get("target") == "/var/lib/postgresql").update(uuid=next(entry for entry in flattened_mounts(m) if entry.get("target") == "/srv/fogell/state")["uuid"]), "shared persistent UUID")
    def wrong_drive(_, events, __):
        args = next(row for row in events if row.get("test") == "vm_start")["qemu_args"]
        index = next(index for index, item in enumerate(args) if item.startswith("file=/vm/pool.raw,"))
        args[index] = args[index].replace("file=/vm/pool.raw", "file=/vm/other.raw")
    changed(wrong_drive, "wrong pool drive")
    def swapped_drives(_, events, __):
        args = next(row for row in events if row.get("test") == "vm_start")["qemu_args"]
        pool = next(index for index, item in enumerate(args) if item.startswith("file=/vm/pool.raw,"))
        state = next(index for index, item in enumerate(args) if item.startswith("file=/vm/state.raw,"))
        args[pool], args[state] = args[state], args[pool]
    changed(swapped_drives, "swapped pool and state drives")
    def aliased_disk(_, events, __):
        disks = next(row for row in events if row.get("test") == "vm_start")["disks"]
        disks["state.raw"] = dict(disks["pool.raw"])
    changed(aliased_disk, "aliased state and pool disk")
    changed(lambda r, e, m: e.__delitem__(next(index for index, row in enumerate(e) if row.get("test") == "guest_boot" and row.get("label") == "campaign-start")), "missing initial boot")
    changed(lambda r, e, m: next(row for row in e if row.get("test") == "vm_clean_shutdown" and row.get("label") == "clean-control").update(container_id="wrong-container"), "clean shutdown predecessor")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("results", type=Path)
    parser.add_argument("events", type=Path)
    parser.add_argument("mounts", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        results, events, mounts = load_jsonl(args.results), load_jsonl(args.events), load_json(args.mounts)
        verify(results, events, mounts)
        if args.self_test:
            self_test(results, events, mounts)
    except (EvidenceError, OSError) as error:
        print(f"verify: {error}", file=sys.stderr)
        return 2
    print(json.dumps({"events": len(events), "results": len(results), "self_test": args.self_test, "status": "passed"}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
