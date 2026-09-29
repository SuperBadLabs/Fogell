#!/usr/bin/env python3
"""Run a command as a Linux child subreaper and collect orphaned descendants.

The command inherits this process's stdin, stdout, stderr, and environment. Once
it exits, adopted children are reaped for at most ``--drain-ms`` (default 500
ms). The wrapper exits with the command's exit status.
"""

import argparse
import ctypes
import os
import signal
import subprocess
import sys
import time

PR_SET_CHILD_SUBREAPER = 36
PR_GET_CHILD_SUBREAPER = 37


def enable_subreaper():
    if not sys.platform.startswith("linux"):
        raise RuntimeError("child subreaper support requires Linux")

    libc = ctypes.CDLL(None, use_errno=True)
    prctl = libc.prctl
    prctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong,
                      ctypes.c_ulong, ctypes.c_ulong]
    prctl.restype = ctypes.c_int

    if prctl(PR_SET_CHILD_SUBREAPER, 1, 0, 0, 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))

    enabled = ctypes.c_int()
    if prctl(PR_GET_CHILD_SUBREAPER, ctypes.addressof(enabled), 0, 0, 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))
    if enabled.value != 1:
        raise RuntimeError("kernel did not enable child subreaper mode")


def direct_children():
    """Return direct child PIDs, including adopted orphans, from procfs."""
    path = f"/proc/self/task/{os.getpid()}/children"
    try:
        with open(path, "r", encoding="ascii") as children_file:
            return [int(value) for value in children_file.read().split()]
    except FileNotFoundError as error:
        raise RuntimeError("Linux procfs child list is unavailable") from error


def reap_adopted(main_pid):
    """Reap exited adopted children without stealing Popen's main status."""
    for pid in direct_children():
        if pid == main_pid:
            continue
        try:
            os.waitpid(pid, os.WNOHANG)
        except ChildProcessError:
            # It exited between procfs enumeration and waitpid, or another
            # wait already collected it.
            pass


def run(command, drain_ms):
    enable_subreaper()
    child = subprocess.Popen(command)
    while child.poll() is None:
        reap_adopted(child.pid)
        time.sleep(0.01)

    # Give descendants orphaned at main exit a bounded opportunity to finish.
    # Reset the quiet timer whenever any adopted child remains visible. Reap
    # only children other than the main command, whose status Popen owns.
    deadline = time.monotonic() + drain_ms / 1000.0
    quiet_since = None
    while time.monotonic() < deadline:
        children = direct_children()
        adopted = [pid for pid in children if pid != child.pid]
        reap_adopted(child.pid)
        if adopted:
            quiet_since = None
        elif quiet_since is None:
            quiet_since = time.monotonic()
        elif time.monotonic() - quiet_since >= min(0.1, drain_ms / 1000.0):
            break
        time.sleep(0.01)

    return child.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--drain-ms", type=int, default=500,
                        help="maximum post-command child-reaping window (default: 500)")
    parser.add_argument("command", nargs=argparse.REMAINDER,
                        help="command and arguments, optionally after --")
    args = parser.parse_args()

    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        parser.error("a command is required")
    if args.drain_ms < 0 or args.drain_ms > 10_000:
        parser.error("--drain-ms must be between 0 and 10000")

    try:
        status = run(command, args.drain_ms)
        if status < 0:
            # Preserve signal termination as a signal, not merely as the
            # conventional 128+signal shell code.
            signum = -status
            try:
                signal.signal(signum, signal.SIG_DFL)
            except (OSError, ValueError):
                # SIGKILL and SIGSTOP cannot have handlers installed.
                pass
            os.kill(os.getpid(), signum)
            return 128 + signum
        return status
    except (OSError, RuntimeError) as error:
        print(f"reap-descendants: {error}", file=sys.stderr)
        return 125


if __name__ == "__main__":
    sys.exit(main())
