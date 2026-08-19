#!/usr/bin/env python3
"""Lexically measure top-level named arguments of FG-177's 14 hosted steps.

This is deliberately a corpus measurement, not a Groovy parser.  It blanks quoted
text and comments while preserving offsets, recognises parenthesised and command
calls, and counts only named keys at the call's top argument depth (so e.g. the
``$class`` and ``branches`` keys inside ``checkout([$class: ...])`` are not
misreported as checkout step parameters).
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import re


STEPS = (
    "sh",
    "echo",
    "archiveArtifacts",
    "junit",
    "checkout",
    "deleteDir",
    "git",
    "stash",
    "unstable",
    "unstash",
    "dir",
    "timeout",
    "retry",
    "withEnv",
)


def blank_non_code(source: str) -> str:
    """Blank comments and Groovy quoted strings without moving any character."""
    out = list(source)
    n = len(source)
    i = 0

    def blank(a: int, b: int) -> None:
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        if source.startswith("//", i):
            end = source.find("\n", i + 2)
            end = n if end < 0 else end
            blank(i, end)
            i = end
        elif source.startswith("/*", i):
            end = source.find("*/", i + 2)
            end = n if end < 0 else end + 2
            blank(i, end)
            i = end
        elif source.startswith("'''", i) or source.startswith('\"\"\"', i):
            quote = source[i : i + 3]
            end = source.find(quote, i + 3)
            if end < 0:
                blank(i + 3, n)
                i = n
            else:
                # Keep delimiters: a command-form call such as ``sh '''...'''``
                # must not become whitespace that lets the scanner attach the
                # following statement's named arguments to ``sh``.
                blank(i + 3, end)
                i = end + 3
        elif source[i] in "'\"":
            quote = source[i]
            j = i + 1
            while j < n:
                if source[j] == "\\":
                    j += 2
                elif source[j] == quote:
                    j += 1
                    break
                else:
                    j += 1
            # As above, retain delimiters while blanking the string content.
            content_end = j - 1 if j <= n and j > i and source[j - 1] == quote else min(j, n)
            blank(i + 1, content_end)
            i = j
        else:
            i += 1
    return "".join(out)


def matching_paren(code: str, opening: int) -> int | None:
    depth = 0
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack: list[str] = []
    for i in range(opening, len(code)):
        c = code[i]
        if c in pairs:
            stack.append(pairs[c])
        elif c in ")]}":
            if not stack or c != stack[-1]:
                return None
            stack.pop()
            if not stack:
                return i
    return None


def command_end(code: str, start: int) -> int:
    """Find a conservative end for a no-parentheses Groovy command call."""
    stack: list[str] = []
    pairs = {"(": ")", "[": "]", "{": "}"}
    i = start
    while i < len(code):
        c = code[i]
        if c in pairs:
            # A top-level trailing closure begins after the call arguments.
            if c == "{" and not stack:
                return i
            stack.append(pairs[c])
        elif c in ")]}":
            if not stack:
                return i
            if c == stack[-1]:
                stack.pop()
        elif c == ";" and not stack:
            return i
        elif c == "\n" and not stack:
            previous = code[start:i].rstrip()
            if not previous.endswith(","):
                return i
        i += 1
    return i


def top_level_keys(code: str, start: int, end: int) -> list[tuple[str, int]]:
    keys: list[tuple[str, int]] = []
    stack: list[str] = []
    pairs = {"(": ")", "[": "]", "{": "}"}
    i = start
    while i < end:
        c = code[i]
        if c in pairs:
            stack.append(pairs[c])
            i += 1
        elif c in ")]}":
            if stack and c == stack[-1]:
                stack.pop()
            i += 1
        elif not stack and (c.isalpha() or c in "_$"):
            j = i + 1
            while j < end and (code[j].isalnum() or code[j] in "_$"):
                j += 1
            k = j
            while k < end and code[k].isspace():
                k += 1
            if k < end and code[k] == ":":
                keys.append((code[i:j], i))
            i = j
        else:
            i += 1
    return keys


def calls(source: str) -> list[tuple[str, list[tuple[str, int]], int]]:
    code = blank_non_code(source)
    pattern = re.compile(r"(?<![.$\w])(" + "|".join(map(re.escape, STEPS)) + r")\b")
    found: list[tuple[str, list[tuple[str, int]], int]] = []
    for match in pattern.finditer(code):
        step = match.group(1)
        cursor = match.end()
        # A command call's argument starts on its statement line.  Skipping all
        # whitespace here let a blanked multiline string consume its own closing
        # line and attach the next step to this one.  Parenthesised calls may put
        # the opening paren on the next line, so test that spelling separately.
        while cursor < len(code) and code[cursor] in " \t\r":
            cursor += 1
        paren_cursor = cursor
        while paren_cursor < len(code) and code[paren_cursor].isspace():
            paren_cursor += 1
        if paren_cursor < len(code) and code[paren_cursor] == "(":
            cursor = paren_cursor
        # A vocabulary word used as a named key in some *other* call/map is not
        # itself a step call (e.g. buildPlugin(timeout: 90, ...)).
        if cursor >= len(code) or code[cursor] in "\n};=:":
            continue
        if code[cursor] == "(":
            end = matching_paren(code, cursor)
            if end is None:
                continue
            arg_start, arg_end = cursor + 1, end
        else:
            arg_start, arg_end = cursor, command_end(code, cursor)
        named = top_level_keys(code, arg_start, arg_end)
        # Jenkins' DSL treats a sole Groovy map argument as the named-argument
        # map (`sh([script: 'x', returnStatus: true])`).  `checkout([$class:
        # 'GitSCM', ...])` is the important exception here: that map is the
        # positional value of its required `scm` parameter, so its internal SCM
        # keys must not be attributed to the checkout step descriptor.
        stripped_start = arg_start
        while stripped_start < arg_end and code[stripped_start].isspace():
            stripped_start += 1
        stripped_end = arg_end
        while stripped_end > stripped_start and code[stripped_end - 1].isspace():
            stripped_end -= 1
        if step != "checkout" and stripped_start < stripped_end and code[stripped_start] == "[":
            close = matching_paren(code, stripped_start)
            if close == stripped_end - 1:
                named = top_level_keys(code, stripped_start + 1, close)
        found.append((step, named, match.start()))
    return found


def line_number(source: str, offset: int) -> int:
    return source.count("\n", 0, offset) + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus", type=pathlib.Path)
    args = parser.parse_args()
    paths = sorted(args.corpus.glob("*.Jenkinsfile"))
    counts: collections.Counter[tuple[str, str]] = collections.Counter()
    files: dict[tuple[str, str], set[str]] = collections.defaultdict(set)
    samples: dict[tuple[str, str], list[str]] = collections.defaultdict(list)
    call_counts: collections.Counter[str] = collections.Counter()
    call_files: dict[str, set[str]] = collections.defaultdict(set)

    for path in paths:
        source = path.read_text(errors="replace")
        for step, named, call_offset in calls(source):
            call_counts[step] += 1
            call_files[step].add(path.name)
            for key, offset in named:
                pair = (step, key)
                counts[pair] += 1
                files[pair].add(path.name)
                if len(samples[pair]) < 3:
                    samples[pair].append(f"{path.name}:{line_number(source, offset)}")

    print(f"corpus_files\t{len(paths)}")
    print("step\tcalls\tcall_files")
    for step in STEPS:
        print(f"{step}\t{call_counts[step]}\t{len(call_files[step])}")
    print()
    print("step\tnamed_key\toccurrences\tfiles\tsamples")
    for step in STEPS:
        for pair in sorted((p for p in counts if p[0] == step), key=lambda p: (-counts[p], p[1])):
            print(
                f"{pair[0]}\t{pair[1]}\t{counts[pair]}\t{len(files[pair])}\t"
                + ",".join(samples[pair])
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
