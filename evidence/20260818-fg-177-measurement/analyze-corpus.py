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
import hashlib
import pathlib
import re
import sys


REPOSITORY_ROOT = pathlib.Path(__file__).resolve().parents[2]
PINNED_MANIFEST = REPOSITORY_ROOT / "corpus" / "CORPUS-SHA256SUMS"
PINNED_MANIFEST_SHA256 = "fae0230d5f07227363cccc3764d80b6833b2cbab2b2cc2fcb5baae45db794af5"
PINNED_CORPUS_FILES = 228
HEX_DIGEST = re.compile(r"[0-9a-f]{64}")
CONTROL_HEADER_KEYWORDS = {"for", "if", "while"}
CONTROL_BODY_PREFIX_KEYWORDS = {"do", "else"}
# These two spellings still dispatch Jenkins' native DSL: `this` is the
# explicit script receiver, and `steps` is its step namespace. Receiver names
# such as `script` are deliberately absent: in shared-library code they are
# user-selected aliases, which a lexical corpus scan cannot prove are Jenkins.
JENKINS_DSL_RECEIVERS = ("steps", "this")
IDENTIFIER = r"[A-Za-z_$][\w$]*"
QUALIFIED_IDENTIFIER = rf"{IDENTIFIER}(?:\s*\.\s*{IDENTIFIER})*"
DECLARATION_MODIFIERS = (
    "abstract|default|final|native|private|protected|public|static|strictfp|"
    "synchronized|transient"
)
METHOD_DECLARATION_PREFIX = re.compile(
    rf"(?:^|[;{{}}\n])[ \t\r]*"
    rf"(?:@\s*{QUALIFIED_IDENTIFIER}(?:[ \t]*\([^;{{}}\n]*\))?[ \t\r\n]*)*"
    rf"(?:(?:{DECLARATION_MODIFIERS})[ \t\r\n]+)*"
    rf"(?!(?:assert|case|do|else|for|if|in|new|return|throw|while|yield)\b)"
    rf"(?:def|void|{QUALIFIED_IDENTIFIER}(?:\s*<[^;{{}}\n]+>)?"
    rf"(?:\s*\[\s*\])*)[ \t\r\n]+$"
)


class CorpusVerificationError(ValueError):
    """The supplied corpus is not the repository-pinned measurement input."""


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


EXPRESSION_PREFIX_KEYWORDS = {"assert", "case", "in", "return", "throw", "yield"}


def blank_non_code(source: str) -> str:
    """Blank literal text/comments while retaining executable GString expressions.

    Offsets and newlines never move. Slashy-versus-division uses the preceding
    lexical token: identifiers, numbers and closing delimiters end expressions,
    while prefix keywords and operator/delimiter positions introduce one. Then a
    later unescaped ``/`` closes a slashy GString, including across newlines.
    Dollar-slashy strings may also span lines. In every interpolating form only
    balanced, unescaped ``${...}`` code remains visible to the call scanner. Its
    delimiters become offset-preserving parentheses so nested keys cannot leak,
    while an executable nested step remains independently discoverable.
    """
    out = list(source)
    n = len(source)

    def blank(a: int, b: int) -> None:
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "

    def previous_token(i: int) -> str | None:
        """Return the previous visible lexical token, after prior blanking."""
        i -= 1
        while i >= 0 and out[i].isspace():
            i -= 1
        if i < 0:
            return None
        if out[i].isalnum() or out[i] in "_$":
            end = i + 1
            while i >= 0 and (out[i].isalnum() or out[i] in "_$"):
                i -= 1
            return "".join(out[i + 1 : end])
        if i > 0 and "".join(out[i - 1 : i + 1]) in {"++", "--"}:
            return "".join(out[i - 1 : i + 1])
        return out[i]

    def closes_control_header(i: int) -> bool:
        close = i - 1
        while close >= 0 and out[close].isspace():
            close -= 1
        if close < 0 or out[close] != ")":
            return False

        depth = 1
        cursor = close - 1
        while cursor >= 0:
            if out[cursor] == ")":
                depth += 1
            elif out[cursor] == "(":
                depth -= 1
                if depth == 0:
                    return previous_token(cursor) in CONTROL_HEADER_KEYWORDS
            cursor -= 1
        return False

    def starts_slashy(i: int) -> bool:
        token = previous_token(i)
        if (
            token is None
            or token in EXPRESSION_PREFIX_KEYWORDS
            or token in CONTROL_BODY_PREFIX_KEYWORDS
        ):
            return True
        if token == ")":
            return closes_control_header(i)
        if token in {"++", "--", "]", "}", "'", '\"', "$"}:
            return False
        return not (token[0].isalnum() or token[0] in "_$")

    def scan_comment(i: int) -> int:
        if source.startswith("//", i):
            end = source.find("\n", i + 2)
            end = n if end < 0 else end
        else:
            close = source.find("*/", i + 2)
            end = n if close < 0 else close + 2
        blank(i, end)
        return end

    def scan_literal(i: int, delimiter: str) -> tuple[int, bool]:
        """Blank a non-interpolating single-quoted form, retaining delimiters."""
        j = i + len(delimiter)
        while j < n:
            if source.startswith(delimiter, j):
                return j + len(delimiter), True
            if source[j] == "\\" and j + 1 < n:
                blank(j, j + 2)
                j += 2
            else:
                blank(j, j + 1)
                j += 1
        return n, False

    def scan_interpolation(i: int) -> tuple[int, bool]:
        """Scan code after ``${`` through its balanced closing brace."""
        depth = 1
        while i < n:
            if source.startswith("//", i) or source.startswith("/*", i):
                i = scan_comment(i)
            elif source.startswith("'''", i):
                snapshot = out.copy()
                after, closed = scan_literal(i, "'''")
                if closed:
                    i = after
                else:
                    out[:] = snapshot
                    i += 3
            elif source.startswith('\"\"\"', i):
                snapshot = out.copy()
                after, closed = scan_gstring(i, '\"\"\"')
                if closed:
                    i = after
                else:
                    out[:] = snapshot
                    i += 3
            elif source.startswith("$/", i):
                snapshot = out.copy()
                after, closed = scan_dollar_slashy(i)
                if closed:
                    i = after
                else:
                    out[:] = snapshot
                    i += 2
            elif source[i] == "'":
                snapshot = out.copy()
                after, closed = scan_literal(i, "'")
                if closed:
                    i = after
                else:
                    out[:] = snapshot
                    i += 1
            elif source[i] == '\"':
                snapshot = out.copy()
                after, closed = scan_gstring(i, '\"')
                if closed:
                    i = after
                else:
                    out[:] = snapshot
                    i += 1
            elif source[i] == "/" and starts_slashy(i):
                snapshot = out.copy()
                after, closed = scan_slashy(i)
                if closed:
                    i = after
                else:
                    out[:] = snapshot
                    i += 1
            elif source[i] == "{":
                depth += 1
                i += 1
            elif source[i] == "}":
                depth -= 1
                if depth == 0:
                    out[i] = ")"
                i += 1
                if depth == 0:
                    return i, True
            else:
                i += 1
        return i, False

    def scan_gstring(i: int, delimiter: str) -> tuple[int, bool]:
        """Blank GString text, recursively retaining executable placeholders."""
        j = i + len(delimiter)
        while j < n:
            if source.startswith(delimiter, j):
                return j + len(delimiter), True
            if source[j] == "\\" and j + 1 < n:
                blank(j, j + 2)
                j += 2
            elif source.startswith("${", j):
                snapshot = out.copy()
                blank(j, j + 2)
                out[j + 1] = "("
                after, closed = scan_interpolation(j + 2)
                if closed:
                    j = after
                else:
                    out[:] = snapshot
                    blank(j, j + 2)
                    j += 2
            else:
                blank(j, j + 1)
                j += 1
        return n, False

    def scan_slashy(i: int) -> tuple[int, bool]:
        """Blank a possibly multiline slashy GString through its unescaped close."""
        j = i + 1
        while j < n:
            if source.startswith("${", j):
                snapshot = out.copy()
                blank(j, j + 2)
                out[j + 1] = "("
                after, closed = scan_interpolation(j + 2)
                if closed:
                    j = after
                else:
                    out[:] = snapshot
                    blank(j, j + 2)
                    j += 2
            elif source[j] == "/" and source[j - 1] != "\\":
                # Groovy slashy strings do not use conventional backslash-run
                # parity. Any immediately preceding backslash protects `/`;
                # `\\/` is escaped just as `\/` is. Backslashes otherwise stay
                # ordinary literal text.
                # Mark a proven slashy close as an expression-ending quote in
                # the already-synthetic scanner view. A later slash is then
                # division, while a failed speculative scan restores this byte
                # from its snapshot. Offsets and the source remain unchanged.
                out[j] = "'"
                return j + 1, True
            else:
                blank(j, j + 1)
                j += 1
        return j, False

    def scan_dollar_slashy(i: int) -> tuple[int, bool]:
        """Blank a dollar-slashy GString, respecting its dollar escapes."""
        j = i + 2
        while j < n:
            if source.startswith("/$", j):
                return j + 2, True
            if source.startswith("${", j):
                snapshot = out.copy()
                blank(j, j + 2)
                out[j + 1] = "("
                after, closed = scan_interpolation(j + 2)
                if closed:
                    j = after
                else:
                    out[:] = snapshot
                    blank(j, j + 2)
                    j += 2
            elif source[j] == "$" and j + 1 < n and source[j + 1] in "$/":
                blank(j, j + 2)
                j += 2
            else:
                blank(j, j + 1)
                j += 1
        return j, False

    i = 0
    while i < n:
        if source.startswith("//", i) or source.startswith("/*", i):
            i = scan_comment(i)
        elif source.startswith("'''", i):
            snapshot = out.copy()
            after, closed = scan_literal(i, "'''")
            if closed:
                i = after
            else:
                out[:] = snapshot
                i += 3
        elif source.startswith('\"\"\"', i):
            snapshot = out.copy()
            after, closed = scan_gstring(i, '\"\"\"')
            if closed:
                i = after
            else:
                out[:] = snapshot
                i += 3
        elif source.startswith("$/", i):
            snapshot = out.copy()
            after, closed = scan_dollar_slashy(i)
            if closed:
                i = after
            else:
                out[:] = snapshot
                i += 2
        elif source[i] == "'":
            snapshot = out.copy()
            after, closed = scan_literal(i, "'")
            if closed:
                i = after
            else:
                out[:] = snapshot
                i += 1
        elif source[i] == '\"':
            snapshot = out.copy()
            after, closed = scan_gstring(i, '\"')
            if closed:
                i = after
            else:
                out[:] = snapshot
                i += 1
        elif source[i] == "/" and starts_slashy(i):
            snapshot = out.copy()
            after, closed = scan_slashy(i)
            if closed:
                i = after
            else:
                out[:] = snapshot
                i += 1
        else:
            i += 1
    return "".join(out)


def matching_paren(code: str, opening: int) -> int | None:
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


def quoted_literal_end(code: str, source: str, start: int, end: int) -> int | None:
    """Find a retained quote's close without confusing nested interpolation code."""
    delimiter = source[start]
    if source.startswith(delimiter * 3, start):
        delimiter *= 3
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack: list[str] = []
    i = start + len(delimiter)
    while i < end:
        if not stack and code.startswith(delimiter, i):
            return i + len(delimiter)
        char = code[i]
        if char in pairs:
            stack.append(pairs[char])
        elif char in ")]}":
            if stack and char == stack[-1]:
                stack.pop()
        i += 1
    return None


def static_quoted_key(source: str, start: int, end: int) -> str | None:
    """Decode a static single/double Groovy map key, including triple forms."""
    quote = source[start]
    delimiter = quote * 3 if source.startswith(quote * 3, start) else quote
    escapes = {
        "b": "\b",
        "t": "\t",
        "n": "\n",
        "f": "\f",
        "r": "\r",
        "\\": "\\",
        "'": "'",
        '"': '"',
        "$": "$",
        "/": "/",
    }
    value: list[str] = []
    i = start + len(delimiter)
    content_end = end - len(delimiter)
    while i < content_end:
        char = source[i]
        if len(delimiter) == 1 and char in "\r\n":
            return None
        if char == "\\":
            i += 1
            if i >= content_end:
                return None
            escaped = source[i]
            if escaped == "u":
                while i < content_end and source[i] == "u":
                    i += 1
                digits = source[i : i + 4]
                if len(digits) != 4 or any(c not in "0123456789abcdefABCDEF" for c in digits):
                    return None
                decoded = chr(int(digits, 16))
                i += 4
                if (
                    quote == '"'
                    and decoded == "$"
                    and i < content_end
                    and (source[i] == "{" or source[i].isalpha() or source[i] == "_")
                ):
                    return None
                value.append(decoded)
                continue
            decoded = escapes.get(escaped)
            if decoded is None:
                return None
            value.append(decoded)
            i += 1
            continue
        if (
            quote == '"'
            and char == "$"
            and i + 1 < content_end
            and (
                source[i + 1] == "{"
                or source[i + 1].isalpha()
                or source[i + 1] == "_"
            )
        ):
            return None
        value.append(char)
        i += 1
    return "".join(value)


def tsv_key(key: str) -> str:
    """Losslessly keep control-bearing static keys inside one TSV field."""
    escaped: list[str] = []
    for char in key:
        if char == "\\":
            escaped.append("\\\\")
        elif char == "\n":
            escaped.append("\\n")
        elif char == "\r":
            escaped.append("\\r")
        elif char == "\t":
            escaped.append("\\t")
        elif char.isprintable():
            escaped.append(char)
        else:
            codepoint = ord(char)
            if codepoint <= 0xFF:
                escaped.append(f"\\x{codepoint:02x}")
            elif codepoint <= 0xFFFF:
                escaped.append(f"\\u{codepoint:04x}")
            else:
                escaped.append(f"\\U{codepoint:08x}")
    return "".join(escaped)


def top_level_keys(
    code: str, source: str, start: int, end: int
) -> list[tuple[str, int]]:
    keys: list[tuple[str, int]] = []
    stack: list[str] = []
    # A colon closes the nearest top-level ternary before it can be a map-key
    # separator. Nested delimiters have their own expression context and are
    # skipped wholesale; Elvis and safe-navigation question marks are not
    # ternary openers.
    pending_ternaries = 0
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
        elif not stack and c in "'\"":
            literal_end = quoted_literal_end(code, source, i, end)
            if literal_end is None:
                i += 1
                continue
            k = literal_end
            while k < end and code[k].isspace():
                k += 1
            if k < end and code[k] == ":":
                if pending_ternaries:
                    pending_ternaries -= 1
                else:
                    key = static_quoted_key(source, i, literal_end)
                    if key is not None:
                        keys.append((key, i))
                i = k + 1
            else:
                i = literal_end
        elif not stack and (c.isalpha() or c in "_$"):
            j = i + 1
            while j < end and (code[j].isalnum() or code[j] in "_$"):
                j += 1
            k = j
            while k < end and code[k].isspace():
                k += 1
            if k < end and code[k] == ":":
                if pending_ternaries:
                    pending_ternaries -= 1
                else:
                    keys.append((code[i:j], i))
                i = k + 1
            else:
                i = j
        elif not stack and c == "?":
            next_nonspace = i + 1
            while next_nonspace < end and code[next_nonspace].isspace():
                next_nonspace += 1
            if i + 1 < end and code[i + 1] in ".[":
                i += 2
            elif next_nonspace < end and code[next_nonspace] == ":":
                i = next_nonspace + 1
            else:
                pending_ternaries += 1
                i += 1
        elif not stack and c == ":" and pending_ternaries:
            pending_ternaries -= 1
            i += 1
        else:
            i += 1
    return keys


def enclosing_open_brace(code: str, offset: int) -> int | None:
    """Return the nearest still-open brace containing ``offset``."""
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack: list[tuple[str, str, int]] = []
    for i, char in enumerate(code[:offset]):
        if char in pairs:
            stack.append((char, pairs[char], i))
        elif char in ")]}":
            if stack and char == stack[-1][1]:
                stack.pop()
    for opening, _, position in reversed(stack):
        if opening == "{":
            return position
    return None


def top_level_delimiter(code: str, start: int, end: int, delimiter: str) -> int | None:
    """Find a delimiter outside nested parentheses, brackets, and braces."""
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack: list[str] = []
    i = start
    while i < end:
        char = code[i]
        if char in pairs:
            stack.append(pairs[char])
        elif char in ")]}":
            if stack and char == stack[-1]:
                stack.pop()
        elif not stack and code.startswith(delimiter, i):
            return i
        i += 1
    return None


def is_closure_parameter(code: str, step_start: int, step_end: int) -> bool:
    """Recognise a vocabulary token declared before a closure's ``->``."""
    brace = enclosing_open_brace(code, step_start)
    if brace is None:
        return False
    close = matching_paren(code, brace)
    if close is None:
        return False
    arrow = top_level_delimiter(code, brace + 1, close, "->")
    if arrow is None or step_start >= arrow:
        return False

    header_start = brace + 1
    header_end = arrow
    while header_start < header_end and code[header_start].isspace():
        header_start += 1
    while header_end > header_start and code[header_end - 1].isspace():
        header_end -= 1
    if header_start < header_end and code[header_start] == "(":
        parenthesized_end = matching_paren(code, header_start)
        if parenthesized_end == header_end - 1:
            header_start += 1
            header_end -= 1

    segment_start = header_start
    while segment_start < header_end:
        comma = top_level_delimiter(code, segment_start, header_end, ",")
        segment_end = header_end if comma is None else comma
        if segment_start <= step_start < segment_end:
            equals = top_level_delimiter(code, segment_start, segment_end, "=")
            declaration_end = segment_end if equals is None else equals
            identifiers = list(
                re.finditer(IDENTIFIER, code[segment_start:declaration_end])
            )
            if not identifiers:
                return False
            declared = identifiers[-1]
            return (
                segment_start + declared.start() == step_start
                and segment_start + declared.end() == step_end
            )
        if comma is None:
            break
        segment_start = comma + 1
    return False


def is_method_declaration(
    code: str,
    match: re.Match[str],
    opening: int,
    closing: int,
) -> bool:
    """Recognise an unqualified, declaration-shaped method signature."""
    if match.group("receiver") is not None:
        return False
    suffix = code[closing + 1 :]
    if re.match(
        rf"\s*(?:throws\s+{QUALIFIED_IDENTIFIER}"
        rf"(?:\s*,\s*{QUALIFIED_IDENTIFIER})*)?\s*(?:\{{|;)",
        suffix,
    ) is None:
        return False
    return METHOD_DECLARATION_PREFIX.search(code[: match.start("step")]) is not None


def calls(source: str) -> list[tuple[str, list[tuple[str, int]], int]]:
    code = blank_non_code(source)
    receivers = "|".join(map(re.escape, JENKINS_DSL_RECEIVERS))
    steps = "|".join(map(re.escape, STEPS))
    pattern = re.compile(
        rf"(?<![.$\w])(?:(?P<receiver>{receivers})\s*(?:\?\.|\.)\s*)?"
        rf"(?P<step>{steps})\b"
    )
    found: list[tuple[str, list[tuple[str, int]], int]] = []
    for match in pattern.finditer(code):
        step = match.group("step")
        step_start = match.start("step")
        cursor = match.end("step")
        # `this.&sh` and `this::sh` name a method; the step token is not itself
        # an invocation even when the resulting method value is later called.
        if code[max(0, step_start - 2) : step_start] in {".&", "::"}:
            continue
        if is_closure_parameter(code, step_start, match.end("step")):
            continue
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
        if cursor >= len(code) or code[cursor] in "\n,)]};=:":
            continue
        # Property/method-pointer use of a vocabulary word is not an invocation.
        # A real call followed by chaining reaches this check at its opening
        # parenthesis or command argument, so `this.sh(...).trim()` still counts.
        if code[cursor] in ".?&" or code.startswith(("*.", "::"), cursor):
            continue
        if code[cursor] == "(":
            end = matching_paren(code, cursor)
            if end is None:
                continue
            if is_method_declaration(code, match, cursor, end):
                continue
            arg_start, arg_end = cursor + 1, end
        else:
            arg_start, arg_end = cursor, command_end(code, cursor)
        named = top_level_keys(code, source, arg_start, arg_end)
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
        map_start, map_end = stripped_start, stripped_end
        while map_start < map_end and code[map_start] == "(":
            close = matching_paren(code, map_start)
            if close != map_end - 1:
                break
            map_start += 1
            map_end -= 1
            while map_start < map_end and code[map_start].isspace():
                map_start += 1
            while map_end > map_start and code[map_end - 1].isspace():
                map_end -= 1
        if step != "checkout" and map_start < map_end and code[map_start] == "[":
            close = matching_paren(code, map_start)
            if close == map_end - 1:
                named = top_level_keys(code, source, map_start + 1, close)
        found.append((step, named, step_start))
    return found


def line_number(source: str, offset: int) -> int:
    return source.count("\n", 0, offset) + 1


def load_manifest(
    path: pathlib.Path, *, expected_digest: str | None = None
) -> dict[str, str]:
    try:
        manifest_bytes = path.read_bytes()
    except OSError as error:
        raise CorpusVerificationError(f"cannot read manifest {path}: {error}") from error
    observed_digest = hashlib.sha256(manifest_bytes).hexdigest()
    if expected_digest is not None and observed_digest != expected_digest:
        raise CorpusVerificationError(
            f"pinned manifest digest mismatch: expected {expected_digest}, got {observed_digest}"
        )
    try:
        lines = manifest_bytes.decode("utf-8").splitlines()
    except UnicodeDecodeError as error:
        raise CorpusVerificationError(f"manifest {path} is not UTF-8: {error}") from error

    entries: dict[str, str] = {}
    for line_number_, line in enumerate(lines, start=1):
        parts = line.split("  ", 1)
        if len(parts) != 2 or HEX_DIGEST.fullmatch(parts[0]) is None:
            raise CorpusVerificationError(
                f"manifest {path} has an invalid line {line_number_}"
            )
        digest, name = parts
        if not name or pathlib.Path(name).name != name or not name.endswith(".Jenkinsfile"):
            raise CorpusVerificationError(
                f"manifest {path} has an invalid filename on line {line_number_}: {name!r}"
            )
        if name in entries:
            raise CorpusVerificationError(f"manifest {path} repeats {name}")
        entries[name] = digest
    if not entries:
        raise CorpusVerificationError(f"manifest {path} is empty")
    return entries


def verified_corpus_paths(
    corpus: pathlib.Path,
    manifest: pathlib.Path,
    *,
    expected_manifest_digest: str,
    expected_file_count: int,
) -> list[pathlib.Path]:
    entries = load_manifest(
        manifest,
        expected_digest=expected_manifest_digest,
    )
    if len(entries) != expected_file_count:
        raise CorpusVerificationError(
            f"manifest has {len(entries)} entries; expected {expected_file_count}"
        )
    if not corpus.is_dir():
        raise CorpusVerificationError(f"corpus directory does not exist: {corpus}")

    try:
        on_disk = sorted(corpus.iterdir())
    except OSError as error:
        raise CorpusVerificationError(f"cannot list corpus {corpus}: {error}") from error
    actual = {path.name for path in on_disk}
    expected = set(entries)
    missing = sorted(expected - actual)
    unexpected = sorted(actual - expected)
    if missing or unexpected:
        details = []
        if missing:
            details.append(f"missing {len(missing)}: {', '.join(missing[:3])}")
        if unexpected:
            details.append(f"unexpected {len(unexpected)}: {', '.join(unexpected[:3])}")
        raise CorpusVerificationError("corpus filename set mismatch; " + "; ".join(details))

    paths = [corpus / name for name in sorted(entries)]
    for path in paths:
        try:
            content = path.read_bytes()
        except OSError as error:
            raise CorpusVerificationError(
                f"cannot read corpus file {path.name}: {error}"
            ) from error
        observed = hashlib.sha256(content).hexdigest()
        if observed != entries[path.name]:
            raise CorpusVerificationError(
                f"corpus digest mismatch for {path.name}: expected {entries[path.name]}, got {observed}"
            )
    return paths


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus", type=pathlib.Path)
    args = parser.parse_args()
    try:
        paths = verified_corpus_paths(
            args.corpus,
            PINNED_MANIFEST,
            expected_manifest_digest=PINNED_MANIFEST_SHA256,
            expected_file_count=PINNED_CORPUS_FILES,
        )
    except CorpusVerificationError as error:
        print(f"ERROR: corpus verification failed: {error}", file=sys.stderr)
        return 2
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
                f"{pair[0]}\t{tsv_key(pair[1])}\t{counts[pair]}\t{len(files[pair])}\t"
                + ",".join(samples[pair])
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
