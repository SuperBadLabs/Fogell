#!/usr/bin/env python3
"""Focused lexical plants for the FG-177 corpus measurement."""

from __future__ import annotations

import importlib.util
import pathlib
import unittest


ANALYZER = pathlib.Path(__file__).with_name("analyze-corpus.py")
SPEC = importlib.util.spec_from_file_location("fg177_analyze_corpus", ANALYZER)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


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


if __name__ == "__main__":
    unittest.main()
