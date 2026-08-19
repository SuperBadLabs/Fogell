#!/usr/bin/env python3
"""Focused lexical plants for the FG-177 corpus measurement."""

from __future__ import annotations

import hashlib
import importlib.util
import pathlib
import tempfile
import unittest


ANALYZER = pathlib.Path(__file__).with_name("analyze-corpus.py")
SPEC = importlib.util.spec_from_file_location("fg177_analyze_corpus", ANALYZER)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"cannot load FG-177 analyzer module from {ANALYZER}")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def compact(source: str) -> list[tuple[str, tuple[str, ...]]]:
    return [(step, tuple(key for key, _ in keys)) for step, keys, _ in MODULE.calls(source)]


class AnalyzerPlants(unittest.TestCase):
    def test_slashy_and_dollar_slashy_literal_text_is_not_a_call(self) -> None:
        source = r'''
def divided = 10 / 2
def slashy = /echo(message: 'fake', fogellProbeUnknown: true) \/ tail/
def dollar = $/unstash(name: 'fake', fogellProbeUnknown: true) $/ slash $$ dollar/$
echo(message: 'real', fogellProbeUnknown: true)
'''
        self.assertEqual(
            compact(source),
            [("echo", ("message", "fogellProbeUnknown"))],
        )

    def test_slashy_forms_keep_only_executable_interpolations(self) -> None:
        source = r'''
def slashy = /archiveArtifacts(artifacts: 'fake') ${sh(script: 'real', returnStatus: true)} \/ tail/
def dollar = $/unstash(name: 'fake') ${echo(message: 'real', fogellProbeUnknown: true)} $/ slash $$ dollar/$
'''
        self.assertEqual(
            compact(source),
            [
                ("sh", ("script", "returnStatus")),
                ("echo", ("message", "fogellProbeUnknown")),
            ],
        )

    def test_slashy_starts_follow_lexical_expression_prefixes(self) -> None:
        source = r'''
def assigned = /echo(message: 'assignment fake')/
def returned() { return /unstash(name: 'return fake')/ }
assert /junit(testResults: 'assert fake')/
consume(/checkout(scm: 'paren fake')/, /git(url: 'comma fake')/)
def ternary = ready ? /archiveArtifacts(artifacts: 'yes fake')/ : /stash(name: 'no fake')/
def equality = ready == /unstable(message: 'equality fake')/
def inequality = ready != /sh(script: 'inequality fake')/
echo(message: 'real', fogellProbeUnknown: true)
'''
        self.assertEqual(
            compact(source),
            [("echo", ("message", "fogellProbeUnknown"))],
        )

    def test_unbraced_control_bodies_are_expression_starts(self) -> None:
        source = r'''
if (ready) /echo(message: 'if same-line fake')/
if ((ready && helper(nested(value))))
    /unstash(name: 'if newline fake')/
while ((ready)) /junit(testResults: 'while same-line fake')/
while (ready)
    /git(url: 'while newline fake')/
for (item in (items ?: [])) /stash(name: 'for same-line fake')/
for (item in items)
    /archiveArtifacts(artifacts: 'for newline fake')/
if (ready) /unstable(message: 'if fake')/ else /checkout(scm: 'else fake')/
do /dir(path: 'do fake')/ while (ready)
echo(message: 'real', fogellProbeUnknown: true)
'''
        self.assertEqual(
            compact(source),
            [("echo", ("message", "fogellProbeUnknown"))],
        )

    def test_parenthesized_expression_before_division_is_not_a_control_header(self) -> None:
        source = r'''
def ordinary = (left + nested(right)) / sh(script: 'ordinary divisor', returnStatus: true)
def invoked = helper(left, (right)) / echo(message: 'call divisor')
'''
        self.assertEqual(
            compact(source),
            [
                ("sh", ("script", "returnStatus")),
                ("echo", ("message",)),
            ],
        )

    def test_corpus_verification_fails_closed_and_accepts_exact_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as root_text:
            root = pathlib.Path(root_text)
            corpus = root / "corpus"
            manifest = root / "manifest"
            corpus.mkdir()
            contents = {
                f"case-{index:03}.Jenkinsfile": f"echo(message: '{index}')\n".encode()
                for index in range(228)
            }
            manifest.write_text(
                "".join(f"{digest(value)}  {name}\n" for name, value in contents.items())
            )

            def verify() -> list[pathlib.Path]:
                return MODULE.verified_corpus_paths(
                    corpus, manifest, expected_manifest_digest=digest(manifest.read_bytes()),
                    expected_file_count=228
                )

            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "does not exist"):
                MODULE.verified_corpus_paths(
                    root / "missing", manifest,
                    expected_manifest_digest=digest(manifest.read_bytes()),
                    expected_file_count=228,
                )
            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "missing 228"):
                verify()

            for name, value in contents.items():
                (corpus / name).write_bytes(value)
            paths = verify()
            self.assertEqual(len(paths), 228)

            manifest.write_text("")
            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "is empty"):
                verify()
            manifest.write_text(
                "".join(
                    f"{digest(value)}  {name}\n"
                    for name, value in list(contents.items())[:227]
                )
            )
            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "has 227 entries"):
                verify()
            manifest.write_text(
                "".join(f"{digest(value)}  {name}\n" for name, value in contents.items())
            )

            with self.assertRaisesRegex(
                MODULE.CorpusVerificationError, "pinned manifest digest mismatch"
            ):
                MODULE.verified_corpus_paths(
                    corpus, manifest,
                    expected_manifest_digest="0" * 64,
                    expected_file_count=228,
                )

            (corpus / "case-227.Jenkinsfile").unlink()
            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "missing 1"):
                verify()
            (corpus / "case-227.Jenkinsfile").write_bytes(contents["case-227.Jenkinsfile"])

            (corpus / "unexpected.txt").write_text("not part of the corpus\n")
            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "unexpected 1"):
                verify()
            (corpus / "unexpected.txt").unlink()

            (corpus / "case-000.Jenkinsfile").write_text("changed\n")
            with self.assertRaisesRegex(MODULE.CorpusVerificationError, "digest mismatch"):
                verify()

            (corpus / "case-000.Jenkinsfile").unlink()
            (corpus / "case-000.Jenkinsfile").mkdir()
            with self.assertRaisesRegex(
                MODULE.CorpusVerificationError, "cannot read corpus file"
            ):
                verify()

    def test_slashy_after_prefix_keyword_keeps_only_interpolation_code(self) -> None:
        source = r'''
def rendered() {
    return /literal unstash(name: 'fake')
${echo(message: 'interpolated', fogellProbeUnknown: true)}/
}
sh(script: 'after', returnStatus: true)
'''
        observed = MODULE.calls(source)
        self.assertEqual(
            [(step, tuple(key for key, _ in keys)) for step, keys, _ in observed],
            [
                ("echo", ("message", "fogellProbeUnknown")),
                ("sh", ("script", "returnStatus")),
            ],
        )
        self.assertEqual(
            [MODULE.line_number(source, offset) for _, _, offset in observed],
            [4, 6],
        )

    def test_division_after_expression_end_tokens_remains_code(self) -> None:
        source = r'''
def byIdentifier = value / sh(script: 'identifier divisor', returnStatus: true)
def byNumber = 10 / echo(message: 'number divisor')
def byParen = (value) / junit(testResults: 'paren divisor')
def byBracket = values[0] / checkout(scm: 'bracket divisor')
def byBrace = { value } / git(url: 'brace divisor')
def bySlashy = /left/ / stash(name: 'slashy divisor')
def byPostIncrement = counter++ / unstable(message: 'post-increment divisor')
'''
        self.assertEqual(
            compact(source),
            [
                ("sh", ("script", "returnStatus")),
                ("echo", ("message",)),
                ("junit", ("testResults",)),
                ("checkout", ("scm",)),
                ("git", ("url",)),
                ("stash", ("name",)),
                ("unstable", ("message",)),
            ],
        )

    def test_multiline_slashy_blanks_text_but_keeps_interpolation_and_offsets(self) -> None:
        source = r'''
def quotient = 42 / 6
def slashy = /literal echo(message: 'fake', fogellProbeUnknown: true)
escaped \/ delimiter is still literal
${sh(script: 'real', returnStatus: true)}
literal unstash(name: 'fake')/
echo(message: 'after', fogellProbeUnknown: true)
'''
        observed = MODULE.calls(source)
        self.assertEqual(
            [(step, tuple(key for key, _ in keys)) for step, keys, _ in observed],
            [
                ("sh", ("script", "returnStatus")),
                ("echo", ("message", "fogellProbeUnknown")),
            ],
        )
        self.assertEqual(
            [MODULE.line_number(source, offset) for _, _, offset in observed],
            [5, 7],
        )
        blanked = MODULE.blank_non_code(source)
        self.assertEqual(blanked.count("\n"), source.count("\n"))
        self.assertIn("42 / 6", blanked)

    def test_multiline_dollar_slashy_has_the_same_executable_boundary(self) -> None:
        source = r'''
def dollar = $/
literal archiveArtifacts(artifacts: 'fake')
escaped $/ slash and $$ dollar stay literal
${echo(message: 'real', fogellProbeUnknown: true)}
literal unstable(message: 'fake')
/$
sh(script: 'after', returnStdout: true)
'''
        observed = MODULE.calls(source)
        self.assertEqual(
            [(step, tuple(key for key, _ in keys)) for step, keys, _ in observed],
            [
                ("echo", ("message", "fogellProbeUnknown")),
                ("sh", ("script", "returnStdout")),
            ],
        )
        self.assertEqual(
            [MODULE.line_number(source, offset) for _, _, offset in observed],
            [5, 8],
        )
        self.assertEqual(
            MODULE.blank_non_code(source).count("\n"),
            source.count("\n"),
        )

    def test_slashy_close_uses_immediately_preceding_backslash(self) -> None:
        for count in range(6):
            with self.subTest(backslashes=count):
                run = "\\" * count
                if count == 0:
                    source = """\
def value = /literal/;
echo(message: 'visible-after-close')
sh(script: 'after', returnStatus: true)
"""
                    expected = [
                        ("echo", ("message",)),
                        ("sh", ("script", "returnStatus")),
                    ]
                else:
                    source = f"""\
def value = /literal{run}/
echo(message: 'fake-inside-literal-{count}')
${{sh(script: 'interpolated', returnStatus: true)}}
tail/
echo(message: 'visible-after-literal')
"""
                    expected = [
                        ("sh", ("script", "returnStatus")),
                        ("echo", ("message",)),
                    ]
                self.assertEqual(compact(source), expected)
                self.assertEqual(
                    MODULE.blank_non_code(source).count("\n"), source.count("\n")
                )

    def test_dollar_slashy_close_uses_dollar_parity_not_backslashes(self) -> None:
        for count in range(6):
            with self.subTest(dollars=count):
                run = "$" * count
                if count % 2 == 0:
                    source = f"""\
def value = $/literal{run}/$;
echo(message: 'visible-after-close')
sh(script: 'after', returnStatus: true)
"""
                    expected = [
                        ("echo", ("message",)),
                        ("sh", ("script", "returnStatus")),
                    ]
                else:
                    source = f"""\
def value = $/literal{run}/$
echo(message: 'fake-inside-literal-{count}')
${{sh(script: 'interpolated', returnStatus: true)}}
tail/$
echo(message: 'visible-after-literal')
"""
                    expected = [
                        ("sh", ("script", "returnStatus")),
                        ("echo", ("message",)),
                    ]
                self.assertEqual(compact(source), expected)
                self.assertEqual(
                    MODULE.blank_non_code(source).count("\n"), source.count("\n")
                )

        for count in range(6):
            with self.subTest(dollar_slashy_backslashes=count):
                run = "\\" * count
                source = f"""\
def value = $/literal{run}/$;
echo(message: 'visible-after-close')
"""
                self.assertEqual(compact(source), [("echo", ("message",))])

    def test_nested_gstrings_keep_calls_and_top_level_keys(self) -> None:
        source = r'''
def rendered = "literal junit(testResults: 'fake') ${
    echo(
        message: "inner unstash(name: 'fake') ${sh(script: 'real', returnStatus: true)}",
        fogellProbeUnknown: true)
}"
'''
        self.assertEqual(
            compact(source),
            [
                ("echo", ("message", "fogellProbeUnknown")),
                ("sh", ("script", "returnStatus")),
            ],
        )

    def test_command_form_continues_after_interpolated_argument(self) -> None:
        source = r'''
sh script: "echo ${helper([nested: { echo(message: 'inside') }])}", label: "real ${env.BUILD_TAG}"
'''
        self.assertEqual(
            compact(source),
            [("sh", ("script", "label")), ("echo", ("message",))],
        )

    def test_nested_non_vocabulary_call_keys_do_not_leak_to_outer_call(self) -> None:
        source = r'''
withEnv(["GRADLE_HOME=${tool name: 'GRADLE_3', type: 'GradleInstallation'}"]) { }
'''
        self.assertEqual(compact(source), [("withEnv", ())])

    def test_escaped_placeholder_and_nested_boundaries_stay_literal(self) -> None:
        source = r'''
def rendered = """escaped \${unstable(message: 'fake')}; ${
    retry(count: helper([nested: { echo(message: 'real') }])) {
        sh(script: "nested ${echo(message: 'deeper')}", returnStdout: true)
    }
}"""
'''
        self.assertEqual(
            compact(source),
            [
                ("retry", ("count",)),
                ("echo", ("message",)),
                ("sh", ("script", "returnStdout")),
                ("echo", ("message",)),
            ],
        )

    def test_unclosed_literal_candidates_inside_interpolation_recover(self) -> None:
        candidates = {
            "dollar-slashy": "$/ never closes",
            "slashy": "/ never closes",
            "triple-quote": '\"\"\" never closes',
        }
        for label, candidate in candidates.items():
            with self.subTest(label=label):
                source = f'''\
def rendered = "outer ${{
    def broken = {candidate}
    echo(message: '{label}-recovered', fogellProbeUnknown: true)
}}"
echo(message: 'after', fogellProbeUnknown: true)
'''
                observed = MODULE.calls(source)
                self.assertEqual(
                    [
                        (step, tuple(key for key, _ in keys))
                        for step, keys, _ in observed
                    ],
                    [
                        ("echo", ("message", "fogellProbeUnknown")),
                        ("echo", ("message", "fogellProbeUnknown")),
                    ],
                )
                self.assertEqual(
                    [MODULE.line_number(source, offset) for _, _, offset in observed],
                    [3, 5],
                )
                self.assertEqual(
                    MODULE.blank_non_code(source).count("\n"), source.count("\n")
                )

    def test_hosted_step_named_method_declarations_are_not_calls(self) -> None:
        source = r'''
def sh(String command) { command }
String echo(String message) { message }
public static void retry(
    int count,
    Closure body
) {
    body.call()
}
@Deprecated
protected final def archiveArtifacts(
    Map options
) { options }
private java.util.List<String> withEnv(List<String> values) throws Exception {
    values
}

sh(script: 'real parenthesized', returnStatus: true)
echo message: 'real command'
this.retry(count: 2) { helper() }
steps.archiveArtifacts artifacts: 'real command receiver'
steps.withEnv(['A=B']) { helper() }
def invokeWrapper() {
    return retry(count: 3) { helper() }
}
'''
        self.assertEqual(
            compact(source),
            [
                ("sh", ("script", "returnStatus")),
                ("echo", ("message",)),
                ("retry", ("count",)),
                ("archiveArtifacts", ("artifacts",)),
                ("withEnv", ()),
                ("retry", ("count",)),
            ],
        )

    def test_hosted_step_named_closure_parameters_are_not_calls(self) -> None:
        source = r'''
def one = { echo -> helper(echo) }
def many = { sh, retry -> helper(sh, retry) }
def typed = { String archiveArtifacts, final Closure withEnv ->
    helper(archiveArtifacts, withEnv)
}
def parenthesized = { (junit, checkout) -> helper(junit, checkout) }
def defaulted = { String unstash = 'fallback', int timeout = 5 ->
    helper(unstash, timeout)
}
def actualDefault = { value = sh(script: 'real default', returnStatus: true) -> value }

echo(message: 'real parenthesized')
helper(echo(message: 'real nested parenthesized'))
helper(echo 'real nested command')
unstable message: 'real command'
this.checkout(scm: config)
steps.timeout time: 1, unit: 'SECONDS'
'''
        self.assertEqual(
            compact(source),
            [
                ("sh", ("script", "returnStatus")),
                ("echo", ("message",)),
                ("echo", ("message",)),
                ("echo", ()),
                ("unstable", ("message",)),
                ("checkout", ("scm",)),
                ("timeout", ("time", "unit")),
            ],
        )

    def test_known_jenkins_dsl_receivers_are_counted_once(self) -> None:
        source = r'''
this.sh(script: 'parenthesized', returnStatus: true)
this.sh script: 'command', returnStdout: true
steps.echo(message: 'parenthesized')
steps.echo message: 'command'
steps . echo (message: 'whitespace')
this?.retry(count: 2) { helper() }
steps?.unstash name: 'safe-command'
def rendered = "value ${steps.echo(message: 'gstring')}"
def trimmed = this.sh(script: 'chained call', returnStdout: true).trim()
echo(message: 'unqualified')
'''
        self.assertEqual(
            compact(source),
            [
                ("sh", ("script", "returnStatus")),
                ("sh", ("script", "returnStdout")),
                ("echo", ("message",)),
                ("echo", ("message",)),
                ("echo", ("message",)),
                ("retry", ("count",)),
                ("unstash", ("name",)),
                ("echo", ("message",)),
                ("sh", ("script", "returnStdout")),
                ("echo", ("message",)),
            ],
        )

    def test_unproven_receivers_and_method_references_are_not_steps(self) -> None:
        source = r'''
helper.echo(message: 'helper')
service.retry(count: 2) { helper() }
helper?.sh(script: 'safe helper')
this.helper.echo(message: 'nested helper')
steps.helper.sh(script: 'nested steps helper')
owner.this.sh(script: 'nested this helper')
script.echo message: 'user-selected alias'
def methodPointerOne = this.&sh
def methodPointerTwo = steps.&echo
def barePropertyOne = this.sh
def barePropertyTwo = steps.echo
def chainedPropertyOne = this.sh.&call
def chainedPropertyTwo = steps.echo?.call
def methodReference = this::sh
def invokedPointer = (this.&sh)(script: 'method value, not direct step syntax')
def invokedReference = this::sh(script: 'reference, not direct step syntax')
def spacedPointer = this.sh  .&call
def spacedSafeChain = steps.echo 	 ?.call
def spacedSpread = this.sh  *.call
def spacedReference = steps.echo  ::call
def newlineChain = this.sh
    .&call
echo message: 'unqualified still counted'
'''
        self.assertEqual(compact(source), [("echo", ("message",))])


if __name__ == "__main__":
    unittest.main()
