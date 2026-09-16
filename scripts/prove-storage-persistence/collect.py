#!/usr/bin/env python3
"""Collect non-secret supplemental evidence after the persistence campaign."""

import argparse
import hashlib
import json
import os
import pathlib
import tempfile
import time

import vm


PROTECTED_NAMES = {"ctrl", "ag1", "jenkins-bench", "mcloving-faceoff2", "jenkins-lab"}


def fail(message):
    raise RuntimeError(message)


def collector_record(test, **values):
    row = {"test": test, "time": time.time(), **values}
    with (vm.ROOT / "collector-events.jsonl").open("a", encoding="utf-8") as output:
        output.write(json.dumps(row, sort_keys=True) + "\n")
    return row


def protected_projection(rows):
    selected = []
    for row in rows:
        names = row.get("Names") or []
        name = names[0] if isinstance(names, list) and names else None
        if name in PROTECTED_NAMES:
            selected.append({
                "id": row.get("Id"), "image": row.get("Image"), "image_id": row.get("ImageID"),
                "mounts": row.get("Mounts"), "name": name, "networks": row.get("Networks"),
                "pid": row.get("Pid"), "ports": row.get("Ports"), "restarts": row.get("Restarts"),
                "started_at": row.get("StartedAt"), "state": row.get("State"),
            })
    selected.sort(key=lambda item: item["name"])
    if {item["name"] for item in selected} != PROTECTED_NAMES:
        fail("protected service snapshot does not contain exactly the five protected services")
    return selected


def host_protected():
    output = vm.run(["podman", "ps", "-a", "--format", "json"]).stdout
    try:
        rows = json.loads(output)
    except json.JSONDecodeError as error:
        fail(f"cannot parse host protected-service snapshot: {error}")
    if not isinstance(rows, list):
        fail("host protected-service snapshot is not a list")
    return protected_projection(rows)


def guest_evidence():
    code = r'''
import hashlib,json,pathlib,subprocess
manifest=pathlib.Path("/home/ubuntu/fogell-bundle/binary-manifest.sha256")
if not manifest.is_file(): raise SystemExit("missing guest binary manifest")
entries=[]
for line in manifest.read_text(encoding="utf-8").splitlines():
 digest,relative=line.split("  ",1)
 if not relative.startswith("app/"): raise SystemExit("manifest contains non-app path")
 deployed=pathlib.Path("/opt/fogell")/relative.removeprefix("app/")
 if not deployed.is_file(): raise SystemExit("deployed manifest file missing: "+relative)
 actual=hashlib.sha256(deployed.read_bytes()).hexdigest()
 entries.append({"path":relative,"expected_sha256":digest,"actual_sha256":actual,"match":digest==actual})
if len(entries)!=89 or not all(x["match"] for x in entries): raise SystemExit("deployed binary manifest mismatch")
receipt_paths=sorted(pathlib.Path("/srv/fogell/state/recovery-archive").glob("cycle-*/*.json"))
receipt_paths+=sorted(pathlib.Path("/srv/fogell/state/storage-pool-receipts").glob("*.json"))
if len(receipt_paths)!=6: raise SystemExit("expected exactly six recovery receipts")
receipts=[]
for path in receipt_paths:
 raw=path.read_text(encoding="utf-8")
 json.loads(raw)
 receipts.append({"path":str(path),"sha256":hashlib.sha256(raw.encode("utf-8")).hexdigest(),"rawtext":raw})
mounts=json.loads(subprocess.run(["findmnt","--json","-o","TARGET,SOURCE,FSTYPE,OPTIONS,UUID"],check=True,text=True,capture_output=True).stdout)
pg=subprocess.run(["sudo","-u","postgres","psql","-X","-At","-d","fogell","-c","SHOW fsync; SHOW synchronous_commit; SHOW full_page_writes; SHOW server_version;"],check=True,text=True,capture_output=True).stdout.strip().splitlines()
dotnet=subprocess.run(["/opt/fogell/dotnet/dotnet","--list-runtimes"],check=True,text=True,capture_output=True).stdout.strip().splitlines()
print(json.dumps({"binary_manifest":entries,"dotnet_runtimes":dotnet,"helper_sha256":hashlib.sha256(pathlib.Path("/opt/fogell/storage-pool.py").read_bytes()).hexdigest(),"kernel":subprocess.run(["uname","-r"],check=True,text=True,capture_output=True).stdout.strip(),"mount_topology":mounts,"postgres_durability":pg,"receipts":receipts},sort_keys=True))
'''
    result = vm.guest(["sudo", "python3", "-c", code])
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        fail(f"cannot parse guest evidence: {error}")


def host_disks():
    disks = {}
    for name in vm.DISKS:
        info = os.stat(vm.ROOT / name)
        disks[name] = {"device": info.st_dev, "inode": info.st_ino, "size": info.st_size, "st_blocks": info.st_blocks, "allocated_bytes": info.st_blocks * 512}
    return {
        "df": vm.run(["df", "-B1", "--output=source,size,used,avail,target", str(vm.ROOT)]).stdout.strip(),
        "disks": disks,
    }


def qemu_version():
    return vm.run(["podman", "exec", vm.NAME, "qemu-system-x86_64", "--version"]).stdout.splitlines()[0]


def write_json(path, value):
    descriptor, temporary = tempfile.mkstemp(prefix=".supplemental-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(value, output, sort_keys=True, separators=(",", ":"))
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--protected-before", type=pathlib.Path, required=True)
    args = parser.parse_args()
    before = protected_projection(json.loads(args.protected_before.read_text(encoding="utf-8")))
    original_record = vm.record
    vm.record = collector_record
    booted = False
    try:
        vm.boot("evidence-collection")
        booted = True
        collected = guest_evidence()
        collected["host_disks"] = host_disks()
        collected["qemu_version"] = qemu_version()
        after = host_protected()
        collected["protected_services"] = {"before": before, "after": after, "unchanged": before == after}
        if before != after:
            fail("protected services changed during campaign")
    finally:
        if booted and vm.inspect() and vm.inspect()["State"]["Running"]:
            vm.stop("evidence-collection")
        vm.record = original_record
    write_json(vm.ROOT / "supplemental.json", collected)


if __name__ == "__main__":
    main()
