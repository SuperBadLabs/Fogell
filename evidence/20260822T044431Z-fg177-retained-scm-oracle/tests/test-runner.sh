#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
git init -q --bare "$tmp/fixture.git"
export FG177_ORACLE_DRIVER="$here/tests/fake-driver.py"
export FG177_HERMETIC=1
export FG177_FAKE_STATE="$tmp/state"
export FG177_FIXTURE_PUSH_URL="file://$tmp/fixture.git"
export FG177_FIXTURE_CLONE_URL="file://$tmp/fixture.git"
export FG177_RUN_ID=hermetic-proof
out="$tmp/run"
"$here/run-retained-scm-oracle.sh" "$out"
python3 "$here/validate-retained-scm-run.py" --hermetic "$out"

# A hermetic double is structurally useful but never production evidence.
if python3 "$here/validate-retained-scm-run.py" "$out" >"$tmp/fake-production.log" 2>&1; then
  echo 'ERROR: production validator accepted a hermetic bundle' >&2; exit 1
fi

# OpenSSH sends a single remote-shell command.  Preserve argv boundaries for
# Podman format strings and nested shell scripts instead of exposing `|`, `$1`,
# or semicolons to the login shell.
python3 - "$here/capture-controller-surface.py" <<'PY'
import importlib.util
import os
import pathlib
import shlex
import sys
from unittest import mock

sys.dont_write_bytecode = True
path = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("surface_capture", path)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)
os.environ["FG177_ORACLE_SSH_HOST"] = "fixture-host"
argv = ("podman", "inspect", "fixture-controller", "--format", "{{.Id}}|{{.ImageName}}|{{.Image}}")
completed = mock.Mock(stdout="captured\n")
with mock.patch.object(module.subprocess, "run", return_value=completed) as run:
    assert module.ssh(*argv) == "captured\n"
called = run.call_args.args[0]
assert called[:2] == ["ssh", "fixture-host"]
assert len(called) == 3
assert shlex.split(called[2]) == list(argv)
PY

# A never-created unique job can return 403 from doDelete on this controller.
# Reset must establish existence with a read before attempting deletion.
python3 - "$here/jenkins-driver.py" <<'PY'
import importlib.util
import pathlib
import sys
from unittest import mock

sys.dont_write_bytecode = True
path = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("jenkins_driver", path)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)
missing = SystemExit("ERROR: GET /job/new/api/json returned HTTP 404")
with mock.patch.object(module, "request", side_effect=missing) as request:
    module.reset("new")
request.assert_called_once_with("/job/new/api/json")
PY

# Jenkins binds its CSRF crumb to the cookie issued by crumbIssuer.  Prove the
# helper uses one cookie-aware opener for both requests and attaches the crumb.
python3 - "$here/jenkins-driver.py" <<'PY'
import importlib.util
import http.cookiejar
import json
import pathlib
import sys
from unittest import mock

sys.dont_write_bytecode = True
class Response:
    def __init__(self, body):
        self.status = 200
        self.body = body
        self.headers = {}
    def __enter__(self): return self
    def __exit__(self, *args): return False
    def read(self): return self.body

path = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("jenkins_driver_http", path)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)
module.BASE = "http://fixture.invalid"
cookie_handlers = [handler for handler in module.OPENER.handlers if hasattr(handler, "cookiejar")]
assert len(cookie_handlers) == 1
assert isinstance(cookie_handlers[0].cookiejar, http.cookiejar.CookieJar)
crumb = json.dumps({"crumbRequestField": "Jenkins-Crumb", "crumb": "bound"}).encode()
with mock.patch.object(module.OPENER, "open", side_effect=[Response(crumb), Response(b"")]) as opened:
    assert module.request("/createItem", method="POST", body=b"") == b""
assert opened.call_count == 2
crumb_request = opened.call_args_list[0].args[0]
post_request = opened.call_args_list[1].args[0]
assert crumb_request.full_url.endswith("/crumbIssuer/api/json")
assert post_request.get_header("Jenkins-crumb") == "bound"
PY

# Existing evidence is immutable even if a caller accidentally reuses a path.
before=$(sha256sum "$out/MANIFEST.sha256")
if "$here/run-retained-scm-oracle.sh" "$out" >"$tmp/reuse.log" 2>&1; then
  echo 'ERROR: runner overwrote an existing bundle' >&2; exit 1
fi
[[ $(sha256sum "$out/MANIFEST.sha256") == "$before" ]]

rehash() {
  (cd "$1" && find . -type f ! -name MANIFEST.sha256 -print0 | LC_ALL=C sort -z | xargs -0 sha256sum > MANIFEST.sha256)
}
cp -R "$out" "$tmp/bad-history"
sed -i.bak '/FG177 MAP ENTRY=GIT_PREVIOUS_COMMIT/d' "$tmp/bad-history/runs/git/build-3/console.txt"
rm "$tmp/bad-history/runs/git/build-3/console.txt.bak"; rehash "$tmp/bad-history"
if python3 "$here/validate-retained-scm-run.py" --hermetic "$tmp/bad-history" >"$tmp/bad-history.log" 2>&1; then
  echo 'ERROR: validator accepted missing history evidence' >&2; exit 1
fi

cp -R "$out" "$tmp/bad-key-overshoot"
sed -i.bak '/^FG177 MAP KEYS=/ s/$/,UNMEASURED_KEY/' "$tmp/bad-key-overshoot/runs/git/build-1/console.txt"
rm "$tmp/bad-key-overshoot/runs/git/build-1/console.txt.bak"
printf '%s\n' 'FG177 MAP ENTRY=UNMEASURED_KEY|java.lang.String|unmeasured' >> "$tmp/bad-key-overshoot/runs/git/build-1/console.txt"
rehash "$tmp/bad-key-overshoot"
if python3 "$here/validate-retained-scm-run.py" --hermetic "$tmp/bad-key-overshoot" >"$tmp/bad-key-overshoot.log" 2>&1; then
  echo 'ERROR: validator accepted an overshot key/entry surface' >&2; exit 1
fi

cp -R "$out" "$tmp/bad-surface"
printf 'drift\n' >> "$tmp/bad-surface/surface-after/git-plugin.tsv"; rehash "$tmp/bad-surface"
if python3 "$here/validate-retained-scm-run.py" --hermetic "$tmp/bad-surface" >"$tmp/bad-surface.log" 2>&1; then
  echo 'ERROR: validator accepted oracle drift' >&2; exit 1
fi

cp -R "$out" "$tmp/bad-ref"
printf '%040d\trefs/heads/wrong\n' 0 > "$tmp/bad-ref/runs/checkout-scm/build-6/ref-after.tsv"; rehash "$tmp/bad-ref"
if python3 "$here/validate-retained-scm-run.py" --hermetic "$tmp/bad-ref" >"$tmp/bad-ref.log" 2>&1; then
  echo 'ERROR: validator accepted mutable-ref drift' >&2; exit 1
fi
echo 'FG177 RETAINED SCM HERMETIC TEST: runner, 12-build validator, drift plants, and preservation pass'
