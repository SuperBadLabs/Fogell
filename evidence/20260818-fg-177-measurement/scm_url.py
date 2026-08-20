#!/usr/bin/env python3
"""Strict, secret-safe SCM URL validation shared by FG-177 evidence tools."""
from __future__ import annotations
import re
import sys
import urllib.parse

ALLOWED_SCHEMES = {"file", "git", "http", "https", "ssh"}
PERCENT = re.compile(r"%([0-9A-Fa-f]{2})")
UNSAFE_DECODED = set(":/?#[]@;")

class UnsafeScmUrl(ValueError):
    pass

def _refuse() -> None:
    # Never include caller-controlled text: a malformed URL may contain secrets.
    raise UnsafeScmUrl("FOGELL_SCM_URL is not a strict credential-free URL")

def _decode_path(value: str, *, allow_space: bool) -> str:
    rendered: list[str] = []
    index = 0
    while index < len(value):
        char = value[index]
        if char != "%":
            code = ord(char)
            if code > 0x7f or code < 0x20 or code == 0x7f:
                _refuse()
            if char == " " and not allow_space:
                _refuse()
            rendered.append(char)
            index += 1
            continue
        data = bytearray()
        while index < len(value) and value[index] == "%":
            match = PERCENT.match(value, index)
            if match is None:
                _refuse()
            data.append(int(match.group(1), 16))
            index += 3
        try:
            decoded = data.decode("utf-8", errors="strict")
        except UnicodeDecodeError:
            _refuse()
        for decoded_char in decoded:
            code = ord(decoded_char)
            if code > 0x7f or code < 0x20 or code == 0x7f or decoded_char in UNSAFE_DECODED:
                _refuse()
            if decoded_char == " " and not allow_space:
                _refuse()
        rendered.append(decoded)
    return "".join(rendered)

def canonical_scm_url(value: str) -> str:
    if not value or any(
        ord(char) > 0x7f or ord(char) < 0x20 or ord(char) == 0x7f for char in value
    ):
        _refuse()
    try:
        parsed = urllib.parse.urlsplit(value, allow_fragments=True)
        scheme = parsed.scheme.lower()
        if scheme not in ALLOWED_SCHEMES or parsed.query or parsed.fragment:
            _refuse()
        if parsed.username is not None or parsed.password is not None or "@" in parsed.netloc:
            _refuse()
        _ = parsed.hostname
        _ = parsed.port
    except (UnicodeError, ValueError):
        _refuse()
    if any(delimiter in parsed.path for delimiter in ("@", ":", ";")):
        _refuse()
    decoded_path = _decode_path(parsed.path, allow_space=scheme == "file")
    if scheme == "file":
        if parsed.netloc not in {"", "localhost"} or not decoded_path.startswith("/"):
            _refuse()
        path = urllib.parse.quote(decoded_path, safe="/-._~")
        return urllib.parse.urlunsplit((scheme, parsed.netloc, path, "", ""))
    if not parsed.netloc or parsed.hostname is None or not parsed.path:
        _refuse()
    if "%" in parsed.netloc or "%" in parsed.path:
        _refuse()
    if any(char.isspace() for char in parsed.netloc + parsed.path):
        _refuse()
    return urllib.parse.urlunsplit((scheme, parsed.netloc, parsed.path, "", ""))

def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: scm_url.py URL", file=sys.stderr)
        return 2
    try:
        print(canonical_scm_url(argv[1]))
    except UnsafeScmUrl as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    return 0

if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
