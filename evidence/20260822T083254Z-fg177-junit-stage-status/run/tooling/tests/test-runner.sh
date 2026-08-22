#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
cache=$(mktemp -d /tmp/fg177-stage-pycache.XXXXXX)
trap 'rm -rf "$cache"' EXIT
PYTHONPYCACHEPREFIX="$cache" python3 tests/test-validator.py -v
PYTHONPYCACHEPREFIX="$cache" python3 -m py_compile \
  jenkins-driver.py capture-surface.py validate-stage-run.py
echo 'FG-177 JUNIT STAGE TOOLING TESTS PASSED'
