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


if __name__ == "__main__":
    unittest.main()
