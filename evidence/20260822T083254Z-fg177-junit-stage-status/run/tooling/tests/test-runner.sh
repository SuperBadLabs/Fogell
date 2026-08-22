#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
python3 tests/test-validator.py -v
python3 -m py_compile jenkins-driver.py capture-surface.py validate-stage-run.py
echo 'FG-177 JUNIT STAGE TOOLING TESTS PASSED'
