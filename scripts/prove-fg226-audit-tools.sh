#!/usr/bin/env bash
# FG-226. The native audit-tool boundary must survive an old fflat version in
# the global store, and the Jenkins input probe must fail closed when setup is
# unreachable, malformed, or rejected. Each arm below reproduces a review
# finding; a proof that only exercises successful infrastructure cannot see any
# of them.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
LAB=$(mktemp -d /tmp/fogell-fg226-audit-proof.XXXXXX)
trap 'jobs -pr | xargs -r kill 2>/dev/null || true; rm -rf "$LAB"' EXIT

[ -x scripts/bin/probe-input ] || {
  echo "FAIL: scripts/bin/probe-input is missing — run scripts/build-audits.sh" >&2
  exit 1
}

echo "=== fflat active-version selection ==="
REAL_FFLAT=$(command -v fflat)
mapfile -t ENTRIES < <(
  LC_ALL=C grep -aoE '\.store/fflat/[0-9A-Za-z._+-]+/fflat/[0-9A-Za-z._+-]+/tools/[^/]+/any/fflat[.]dll' "$REAL_FFLAT" \
    | sort -u
)
if [ "${#ENTRIES[@]}" -ne 1 ]; then
  echo "FAIL: expected one embedded fflat DLL path in $REAL_FFLAT; found ${#ENTRIES[@]}" >&2
  exit 1
fi
VERSION=$(printf '%s\n' "${ENTRIES[0]}" | cut -d/ -f3)
TFM=$(printf '%s\n' "${ENTRIES[0]}" | cut -d/ -f7)

FAKE_HOME=$LAB/home
FAKE_TOOLS=$FAKE_HOME/.dotnet/tools
REAL_TOOL_ROOT=$(dirname "$(readlink -f "$REAL_FFLAT")")
ACTIVE_SOURCE=$REAL_TOOL_ROOT/.store/fflat/$VERSION
ACTIVE_COPY=$FAKE_TOOLS/.store/fflat/$VERSION
ACTIVE_LIB=$ACTIVE_COPY/fflat/$VERSION/tools/$TFM/any/lib/linux/x64/glibc
STALE_LOW=$FAKE_TOOLS/.store/fflat/0.0.0/fflat/0.0.0/tools/net10.0/any/lib/linux/x64/glibc
STALE_HIGH=$FAKE_TOOLS/.store/fflat/999.0.0/fflat/999.0.0/tools/net10.0/any/lib/linux/x64/glibc
mkdir -p "$FAKE_TOOLS/.store/fflat"
# Copy the apphost so its embedded relative DLL path resolves against the fake
# tool root. Prefer hardlinks for speed, but /tmp may be a different filesystem
# from the global tool store; in that case discard the partial tree and make a
# normal metadata-preserving copy. Its three local brotli links are then removed,
# so the proof cannot borrow the already-fixed real store. Stale versions on
# BOTH lexical sides are real additional glob matches. The pre-review
# `*/fflat/*` lookup joins all three paths and fails;
# first/last-match shortcuts mutate a stale directory and are caught below.
cp "$REAL_FFLAT" "$FAKE_TOOLS/fflat"
if ! cp -al "$ACTIVE_SOURCE" "$ACTIVE_COPY" 2>/dev/null; then
  rm -rf "$ACTIVE_COPY"
  cp -a "$ACTIVE_SOURCE" "$ACTIVE_COPY"
fi
rm -f "$ACTIVE_LIB"/libbrotli{enc,dec,common}.so
mkdir -p "$STALE_LOW" "$STALE_HIGH"
HOME=$FAKE_HOME PATH="$FAKE_TOOLS:$PATH" scripts/build-audits.sh --preflight >"$LAB/fflat.log" 2>&1 \
  || { cat "$LAB/fflat.log" >&2; echo "FAIL: planted second fflat store version broke the active tool" >&2; exit 1; }
rg -F 'link preflight OK' "$LAB/fflat.log" >/dev/null \
  || { cat "$LAB/fflat.log" >&2; echo "FAIL: fflat preflight emitted no success verdict" >&2; exit 1; }
for name in enc dec common; do
  [ -L "$ACTIVE_LIB/libbrotli$name.so" ] \
    || { echo "FAIL: active fflat store was not repaired: libbrotli$name.so" >&2; exit 1; }
  [ ! -e "$STALE_LOW/libbrotli$name.so" ] && [ ! -e "$STALE_HIGH/libbrotli$name.so" ] \
    || { echo "FAIL: stale fflat store was mutated: libbrotli$name.so" >&2; exit 1; }
done
echo "  passed  active shim version selected with a planted stale version beside it"

cat >"$LAB/server.py" <<'PY'
import http.server
import json
import sys
import time

scenario, port_file = sys.argv[1], sys.argv[2]

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    def reply(self, status, body=b"", cookie=False):
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("X-Jenkins-Session", "constant-session")
        # Force a fresh connection per request. Python's minimal HTTP/1.0
        # fixture otherwise raced HttpClient's pooled reuse of a socket the
        # server had already closed, intermittently turning the planted 403
        # into a transport error before the next request reached the handler.
        self.send_header("Connection", "close")
        self.close_connection = True
        if cookie:
            self.send_header("Set-Cookie", "JSESSIONID=fg226; Path=/")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/ready":
            self.reply(200, b"{}")
        elif self.path == "/crumbIssuer/api/json":
            if scenario == "missing-crumb":
                self.reply(200, b"{}", True)
            elif scenario == "invalid-crumb-field":
                body = json.dumps({"crumbRequestField": "bad field", "crumb": "token"}, separators=(",", ":")).encode()
                self.reply(200, body, True)
            else:
                body = json.dumps({"crumbRequestField": "Jenkins-Crumb", "crumb": "token"}, separators=(",", ":")).encode()
                self.reply(200, body, True)
        elif self.path.endswith("/wfapi/nextPendingInputAction"):
            self.reply(200, b'{"id":"pending-1"}')
        elif self.path == "/api/json":
            if scenario == "slow-restart-poll":
                count_file = port_file + ".polls"
                try:
                    with open(count_file, "r", encoding="ascii") as f:
                        count = int(f.read())
                except (FileNotFoundError, ValueError):
                    count = 0
                count += 1
                with open(count_file, "w", encoding="ascii") as f:
                    f.write(str(count))
                # Request one is the pre-restart identity read. Every later
                # request is a tolerant restart poll and deliberately never
                # answers inside either the five- or thirty-second bound.
                if count > 1:
                    time.sleep(30)
                    return
            self.reply(200, b"{}")
        else:
            self.reply(404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length:
            self.rfile.read(length)
        if self.path.endswith("/doDelete"):
            self.reply(404)  # The initial absent-job delete is the sole allowed 404.
        elif self.path.startswith("/createItem"):
            self.reply(403 if scenario == "create-403" else 200)
        elif self.path.endswith("/build"):
            self.reply(500 if scenario == "build-500" else 201)
        else:
            self.reply(200)

    def log_message(self, *_):
        pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
with open(port_file, "w", encoding="ascii") as f:
    f.write(str(server.server_port))
server.serve_forever()
PY

run_http_case() {
  local scenario=$1 expected=$2 probe_mode=${3:-approve} pid port rc
  local port_file=$LAB/$scenario.port log=$LAB/$scenario.log
  python3 "$LAB/server.py" "$scenario" "$port_file" &
  pid=$!
  for _ in {1..100}; do [ -s "$port_file" ] && break; sleep 0.05; done
  [ -s "$port_file" ] || { echo "FAIL: $scenario server did not start" >&2; return 1; }
  port=$(<"$port_file")
  for _ in {1..100}; do
    curl -fsS "http://127.0.0.1:$port/ready" >/dev/null 2>&1 && break
    sleep 0.05
  done
  curl -fsS "http://127.0.0.1:$port/ready" >/dev/null 2>&1 \
    || { echo "FAIL: $scenario server never answered its readiness request" >&2; return 1; }
  kill -0 "$pid" 2>/dev/null || { echo "FAIL: $scenario server exited during startup" >&2; return 1; }
  set +e
  JENKINS_URL="http://127.0.0.1:$port" RESTART_CMD=true timeout 15 scripts/bin/probe-input "$probe_mode" >"$log" 2>&1
  rc=$?
  set -e
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  if [ "$rc" -eq 0 ] || [ "$rc" -eq 124 ]; then
    cat "$log" >&2
    echo "FAIL: $scenario did not fail promptly" >&2
    return 1
  fi
  rg -F "$expected" "$log" >/dev/null \
    || { cat "$log" >&2; echo "FAIL: $scenario missed its exact refusal" >&2; return 1; }
  echo "  refused $scenario — $expected"
}

echo "=== probe-input setup refusals ==="
set +e
JENKINS_URL=http://127.0.0.1:1 timeout 10 scripts/bin/probe-input approve >"$LAB/unreachable.log" 2>&1
unreachable_rc=$?
set -e
if [ "$unreachable_rc" -eq 0 ] || [ "$unreachable_rc" -eq 124 ] \
   || ! rg -F 'FAIL: crumb request:' "$LAB/unreachable.log" >/dev/null; then
  cat "$LAB/unreachable.log" >&2
  echo "FAIL: unreachable setup did not refuse promptly" >&2
  exit 1
fi
echo "  refused unreachable crumb endpoint"
run_http_case missing-crumb 'response is missing crumbRequestField or crumb'
run_http_case invalid-crumb-field 'response is missing crumbRequestField or crumb'
run_http_case create-403 'FAIL: create probe job: HTTP 403'
run_http_case build-500 'FAIL: start probe build: HTTP 500'
run_http_case restart-noop 'FAIL: controller identity did not change; RESTART_CMD did not prove a restart' restart

echo "=== restart polling retains the five-second request bound ==="
scenario=slow-restart-poll
port_file=$LAB/$scenario.port
log=$LAB/$scenario.log
python3 "$LAB/server.py" "$scenario" "$port_file" &
pid=$!
for _ in {1..100}; do [ -s "$port_file" ] && break; sleep 0.05; done
[ -s "$port_file" ] || { echo "FAIL: $scenario server did not start" >&2; exit 1; }
port=$(<"$port_file")
set +e
JENKINS_URL="http://127.0.0.1:$port" RESTART_CMD=true timeout 15 scripts/bin/probe-input restart >"$log" 2>&1
rc=$?
set -e
[ -s "$port_file.polls" ] || { cat "$log" >&2; echo "FAIL: slow restart fixture recorded no controller requests" >&2; exit 1; }
polls=$(<"$port_file.polls")
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
if [ "$rc" -ne 124 ] || [ "$polls" -lt 3 ]; then
  cat "$log" >&2
  echo "FAIL: restart poll made $polls request(s); a five-second bound must allow a retry inside 15 seconds" >&2
  exit 1
fi
echo "  passed  slow restart poll retried inside 15 s ($polls requests including identity setup)"

echo "FG-226 AUDIT-TOOL PROOF: active fflat selection, six fail-closed Jenkins setup/restart arms, and the five-second restart-poll bound pass"
