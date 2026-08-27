#!/usr/bin/env bash
# FG-222. Real-process proof for the controller/build environment boundary.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
RUN_HOST="$PWD/tools/Fogell.Run.Host/bin/Release/net10.0/Fogell.Run.Host"
[ -x "$RUN_HOST" ] || { echo "prove-control-env-isolation: Release host is missing"; exit 1; }

LAB=$(mktemp -d /tmp/fogell-control-env.XXXXXX)
trap 'rm -rf "$LAB"' EXIT

CREDENTIAL_SECRET=fg222-bound-secret
INLINE_CONTROL=fg222-inline-controller-control
DB_CONTROL=postgres://controller/fg222
API_CONTROL=fg222-api-controller-control
SCM_CONTROL=fg222-controller-scm-authority
TMPDIR_CONTROL="$LAB/controller-tmp"
LIVE="$LAB/live"
mkdir -p "$LIVE/fakebin" "$LIVE/source" "$TMPDIR_CONTROL"

# A local remote makes the build-side Git assertion dynamic and network-free.
/usr/bin/git init -q --bare "$LIVE/remote.git"
/usr/bin/git -C "$LIVE/source" init -q -b main
/usr/bin/git -C "$LIVE/source" config user.name fg222
/usr/bin/git -C "$LIVE/source" config user.email fg222@example.invalid
printf 'fixture\n' > "$LIVE/source/file.txt"
/usr/bin/git -C "$LIVE/source" add file.txt
/usr/bin/git -C "$LIVE/source" commit -q -m fixture
/usr/bin/git -C "$LIVE/source" remote add origin "file://$LIVE/remote.git"
/usr/bin/git -C "$LIVE/source" push -q origin main

cat > "$LIVE/fakebin/git" <<'SH'
#!/bin/sh
/usr/bin/env >> "$GIT_CAPTURE"
printf '%s\n' __CALL__ >> "$GIT_CAPTURE"
exec /usr/bin/git "$@"
SH
chmod +x "$LIVE/fakebin/git"

cat > "$LIVE/Jenkinsfile" <<'JENKINS'
pipeline {
    agent any
    environment {
        DECLARED = 'pipeline-value'
        PATH = "__FAKEBIN__:${PATH}"
        GIT_CAPTURE = '__GITLOG__'
    }
    stages {
        stage('boundary') {
            steps {
                sh "printf '%s' 'simple=${env.FOGELL_CREDENTIALS_FILE}' > simple.txt"
                sh "printf '%s' \"complex=${env.FOGELL_CREDENTIALS_FILE == null ? 'absent' : 'present'}\" > complex.txt"
                withEnv(['WITH_ENV=with-value', 'PATH+FG222=/fg222/withenv']) {
                    withCredentials([string(credentialsId: 'fg222-token', variable: 'TOKEN')]) {
                        sh '''
                            /usr/bin/env | /usr/bin/sort > child.env
                            printf '%s' "$DECLARED" > declared.txt
                            printf '%s' "$WITH_ENV" > withenv.txt
                            printf '%s' "$PATH" > path.txt
                            printf '%s' "$HOME" > home.txt
                            printf '%s' "$TOKEN" > credential.txt
                            echo "credential=$TOKEN"
                        '''
                    }
                }
                git url: 'file://__REMOTE__', branch: 'main'
            }
        }
    }
}
JENKINS

sed -i "s|__FAKEBIN__|$LIVE/fakebin|g; s|__GITLOG__|$LIVE/build-git.env|g; s|__REMOTE__|$LIVE/remote.git|g" "$LIVE/Jenkinsfile"
printf 'fg222-token\ttext\t%s\n' "$(printf '%s' "$CREDENTIAL_SECRET" | base64 -w0)" > "$LIVE/credentials.tsv"

set +e
FOGELL_CREDENTIALS="$INLINE_CONTROL" \
FOGELL_CREDENTIALS_FILE="$LIVE/credentials.tsv" \
DATABASE_URL="$DB_CONTROL" \
CONTROLLER_API_TOKEN="$API_CONTROL" \
SSH_AUTH_SOCK="$SCM_CONTROL" \
GIT_ASKPASS="$SCM_CONTROL" \
TMPDIR="$TMPDIR_CONTROL" \
timeout --kill-after=5 90 \
  "$RUN_HOST" "$LIVE/Jenkinsfile" "$LIVE/ws" job "$LIVE/build.journal" \
  > "$LIVE/run.log" 2>&1
run_status=$?
set -e
printf '%s\n' "$run_status" > "$LIVE/run.status"

judge_status() {
  local status=$1 label=$2
  case "$status" in
    ''|*[!0-9]*) echo "  FAIL $label: invalid host status [$status]"; return 1 ;;
    0) return 0 ;;
    124) echo "  FAIL $label: host timed out"; return 1 ;;
    *) echo "  FAIL $label: host nonzero status $status"; return 1 ;;
  esac
}

judge() {
  local state=$1 ws="$1/ws/job" failures=0
  judge_status "$(cat "$state/run.status" 2>/dev/null || true)" artifact || failures=$((failures + 1))
  [ "$(awk -F '\t' '$1 == "build-finished" { count++ } END { print count + 0 }' "$state/build.journal")" -eq 1 ] \
    && grep -qx $'build-finished\tsuccess' "$state/build.journal" \
    || { echo "  FAIL: terminal state is not one unique success"; failures=$((failures + 1)); }
  [ "$(cat "$ws/simple.txt" 2>/dev/null || true)" = simple=null ] \
    || { echo "  FAIL: simple GString saw controller input"; failures=$((failures + 1)); }
  [ "$(cat "$ws/complex.txt" 2>/dev/null || true)" = complex=absent ] \
    || { echo "  FAIL: complex GString saw controller input"; failures=$((failures + 1)); }
  [ "$(cat "$ws/declared.txt" 2>/dev/null || true)" = pipeline-value ] \
    || { echo "  FAIL: declaration was lost"; failures=$((failures + 1)); }
  [ "$(cat "$ws/withenv.txt" 2>/dev/null || true)" = with-value ] \
    || { echo "  FAIL: withEnv was lost"; failures=$((failures + 1)); }
  case "$(cat "$ws/path.txt" 2>/dev/null || true)" in
    /fg222/withenv:"$LIVE"/fakebin:*) ;;
    *) echo "  FAIL: PATH overlay order is wrong"; failures=$((failures + 1)) ;;
  esac
  local build_home
  build_home=$(cat "$ws/home.txt" 2>/dev/null || true)
  case "$build_home" in
    "$state"/ws/_agent_home/*) ;;
    *) echo "  FAIL: HOME is not beneath the build identity root"; failures=$((failures + 1)) ;;
  esac
  [ "$build_home" != "$state/ws/_agent_home" ] \
    || { echo "  FAIL: HOME is not the neutral build path"; failures=$((failures + 1)); }
  [ -d "$build_home" ] \
    || { echo "  FAIL: build-scoped neutral HOME was not materialized"; failures=$((failures + 1)); }
  [ "$(cat "$ws/credential.txt" 2>/dev/null || true)" = "$CREDENTIAL_SECRET" ] \
    || { echo "  FAIL: explicit credential binding was lost"; failures=$((failures + 1)); }
  [ -s "$ws/child.env" ] || { echo "  FAIL: shell env capture missing"; failures=$((failures + 1)); }
  [ -s "$state/build-git.env" ] && grep -qx __CALL__ "$state/build-git.env" \
    || { echo "  FAIL: build Git did not traverse the recording launcher"; failures=$((failures + 1)); }

  local shell_allowed=' BUILD_DISPLAY_NAME BUILD_ID BUILD_NUMBER DECLARED EXECUTOR_NUMBER GIT_CAPTURE HOME JOB_BASE_NAME JOB_NAME NODE_NAME PATH PWD TOKEN TOKEN_FILE WITH_ENV WORKSPACE '
  local git_allowed=' BUILD_DISPLAY_NAME BUILD_ID BUILD_NUMBER DECLARED EXECUTOR_NUMBER GIT_CAPTURE HOME JOB_BASE_NAME JOB_NAME NODE_NAME PATH PWD WORKSPACE __CALL__ '
  local name
  while IFS='=' read -r name _; do
    case "$shell_allowed" in
      *" $name "*) ;;
      *) echo "  FAIL: shell environment has unapproved key $name"; failures=$((failures + 1)) ;;
    esac
  done < "$ws/child.env"
  while IFS='=' read -r name _; do
    case "$git_allowed" in
      *" $name "*) ;;
      *) echo "  FAIL: build Git environment has unapproved key $name"; failures=$((failures + 1)) ;;
    esac
  done < "$state/build-git.env"

  for capture in "$ws/child.env" "$state/build-git.env"; do
    for name in FOGELL_CREDENTIALS FOGELL_CREDENTIALS_FILE DATABASE_URL CONTROLLER_API_TOKEN SSH_AUTH_SOCK GIT_ASKPASS TMPDIR; do
      ! grep -q "^${name}=" "$capture" 2>/dev/null \
        || { echo "  FAIL: $(basename "$capture") inherited $name"; failures=$((failures + 1)); }
    done
    for value in "$INLINE_CONTROL" "$state/credentials.tsv" "$DB_CONTROL" "$API_CONTROL" "$SCM_CONTROL" "$TMPDIR_CONTROL"; do
      ! grep -Fq "$value" "$capture" 2>/dev/null \
        || { echo "  FAIL: $(basename "$capture") inherited controller value"; failures=$((failures + 1)); }
    done
  done

  grep -Fq 'credential=****' "$state/run.log" \
    || { echo "  FAIL: credential output was not masked"; failures=$((failures + 1)); }
  ! grep -Fq "$CREDENTIAL_SECRET" "$state/run.log" \
    || { echo "  FAIL: raw credential reached host output"; failures=$((failures + 1)); }
  [ "$failures" -eq 0 ]
}

expect_planted_failure() {
  local label=$1 state=$2
  if judge "$state" >/dev/null 2>&1; then
    echo "  FAIL: checker accepted planted $label state"
    exit 1
  fi
  echo "  checker rejected planted $label state"
}

judge_status "$run_status" live
judge "$LIVE"
echo "  live shell/GString/build-Git boundary: PASS"

if rg -n 'Environment\.GetEnvironmentVariable(s)?' src/Fogell.Differential/GString.fs src/Fogell.Differential/WalkerOrchestration.fs; then
  echo "  FAIL: a build interpolation/overlay path contains an ambient environment read"
  exit 1
fi

for spec in ordinary-status:7 timeout-status:124 signal-status:143; do
  label=${spec%%:*}; value=${spec##*:}; state="$LAB/planted-$label"; cp -a "$LIVE" "$state"
  printf '%s\n' "$value" > "$state/run.status"; expect_planted_failure "$label" "$state"
done

state="$LAB/planted-simple"; cp -a "$LIVE" "$state"; printf 'simple=present' > "$state/ws/job/simple.txt"; expect_planted_failure simple-gstring "$state"
state="$LAB/planted-complex"; cp -a "$LIVE" "$state"; printf 'complex=present' > "$state/ws/job/complex.txt"; expect_planted_failure complex-gstring "$state"
state="$LAB/planted-shell"; cp -a "$LIVE" "$state"; printf 'CONTROLLER_API_TOKEN=%s\n' "$API_CONTROL" >> "$state/ws/job/child.env"; expect_planted_failure shell-env "$state"
state="$LAB/planted-git"; cp -a "$LIVE" "$state"; printf 'SSH_AUTH_SOCK=%s\n' "$SCM_CONTROL" >> "$state/build-git.env"; expect_planted_failure build-git-env "$state"
state="$LAB/planted-home"; cp -a "$LIVE" "$state"; printf '/home/controller' > "$state/ws/job/home.txt"; expect_planted_failure controller-home "$state"
state="$LAB/planted-declared"; cp -a "$LIVE" "$state"; printf lost > "$state/ws/job/declared.txt"; expect_planted_failure declared-env "$state"
state="$LAB/planted-credential"; cp -a "$LIVE" "$state"; printf lost > "$state/ws/job/credential.txt"; expect_planted_failure credential-binding "$state"
state="$LAB/planted-mask"; cp -a "$LIVE" "$state"; sed -i "s/credential=\*\*\*\*/credential=$CREDENTIAL_SECRET/g" "$state/run.log"; expect_planted_failure credential-mask "$state"

echo "FG-222 controller environment proof: PASS"
