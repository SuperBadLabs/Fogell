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
                            printf '%s' "$TMPDIR" > tmpdir.txt
                            /usr/bin/mktemp > default-temp.txt
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
  local actual_path
  actual_path=$(cat "$ws/path.txt" 2>/dev/null || true)
  case "$actual_path" in
    /fg222/withenv:"$state"/fakebin:*) ;;
    *) echo "  FAIL: PATH overlay order is wrong [$actual_path]"; failures=$((failures + 1)) ;;
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
  local build_tmp
  build_tmp=$(cat "$ws/tmpdir.txt" 2>/dev/null || true)
  [ "$build_tmp" = "$build_home/tmp" ] \
    || { echo "  FAIL: TMPDIR is not the build-local temporary directory [$build_tmp]"; failures=$((failures + 1)); }
  [ -d "$build_tmp" ] && [ "$(stat -c %a "$build_tmp" 2>/dev/null || true)" = 700 ] \
    || { echo "  FAIL: build-local TMPDIR was not materialized privately"; failures=$((failures + 1)); }
  local default_temp
  default_temp=$(cat "$ws/default-temp.txt" 2>/dev/null || true)
  case "$default_temp" in
    "$build_tmp"/*) [ -f "$default_temp" ] || { echo "  FAIL: mktemp result was not materialized"; failures=$((failures + 1)); } ;;
    *) echo "  FAIL: mktemp escaped build-local TMPDIR [$default_temp]"; failures=$((failures + 1)) ;;
  esac
  [ "$(cat "$ws/credential.txt" 2>/dev/null || true)" = "$CREDENTIAL_SECRET" ] \
    || { echo "  FAIL: explicit credential binding was lost"; failures=$((failures + 1)); }
  [ -s "$ws/child.env" ] || { echo "  FAIL: shell env capture missing"; failures=$((failures + 1)); }
  [ -s "$state/build-git.env" ] && grep -qx __CALL__ "$state/build-git.env" \
    || { echo "  FAIL: build Git did not traverse the recording launcher"; failures=$((failures + 1)); }

  local shell_allowed=' BUILD_DISPLAY_NAME BUILD_ID BUILD_NUMBER DECLARED EXECUTOR_NUMBER GIT_CAPTURE HOME JOB_BASE_NAME JOB_NAME NODE_NAME PATH PWD TMPDIR TOKEN TOKEN_FILE WITH_ENV WORKSPACE '
  local git_allowed=' BUILD_DISPLAY_NAME BUILD_ID BUILD_NUMBER DECLARED EXECUTOR_NUMBER GIT_CAPTURE HOME JOB_BASE_NAME JOB_NAME NODE_NAME PATH PWD TMPDIR WORKSPACE __CALL__ '
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

  local tmp_status
  if awk -v expected="$build_tmp" '
      index($0, "TMPDIR=") == 1 { count++; if ($0 != "TMPDIR=" expected) wrong = 1 }
      END { if (wrong) exit 2; if (count != 1) exit 3 }
    ' "$ws/child.env"; then
    :
  else
    tmp_status=$?
    case "$tmp_status" in
      2) echo "  FAIL: child.env has a non-build-local TMPDIR" ;;
      *) echo "  FAIL: child.env does not contain exactly one build-local TMPDIR" ;;
    esac
    failures=$((failures + 1))
  fi
  if awk -v expected="$build_tmp" '
      $0 == "__CALL__" {
        calls++
        if (!opened || count != 1) bad_count = 1
        opened = 0; count = 0
        next
      }
      { opened = 1 }
      index($0, "TMPDIR=") == 1 { count++; if ($0 != "TMPDIR=" expected) wrong = 1 }
      END {
        if (wrong) exit 2
        if (calls == 0 || opened || bad_count) exit 3
      }
    ' "$state/build-git.env"; then
    :
  else
    tmp_status=$?
    case "$tmp_status" in
      2) echo "  FAIL: build-git.env has a non-build-local TMPDIR" ;;
      *) echo "  FAIL: build-git.env does not contain exactly one build-local TMPDIR per call" ;;
    esac
    failures=$((failures + 1))
  fi

  for capture in "$ws/child.env" "$state/build-git.env"; do
    for name in FOGELL_CREDENTIALS FOGELL_CREDENTIALS_FILE DATABASE_URL CONTROLLER_API_TOKEN SSH_AUTH_SOCK GIT_ASKPASS; do
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
  local label=$1 state=$2 expected=${3:-} output
  if output=$(judge "$state" 2>&1); then
    echo "  FAIL: checker accepted planted $label state"
    exit 1
  fi
  if [ -n "$expected" ] && ! grep -Fq "$expected" <<<"$output"; then
    echo "  FAIL: checker rejected planted $label without naming [$expected]"
    exit 1
  fi
  echo "  checker rejected planted $label state"
}

copy_fixture() {
  local state=$1 file
  cp -a "$LIVE" "$state"
  # Captures contain absolute LIVE paths.  Normalize only owned text fixtures
  # so a copied clean control remains accepted before its planted mutation.
  for file in \
    "$state/ws/job/home.txt" "$state/ws/job/tmpdir.txt" "$state/ws/job/default-temp.txt" \
    "$state/ws/job/path.txt" "$state/ws/job/child.env" "$state/build-git.env"; do
    sed -i "s|$LIVE|$state|g" "$file"
  done
  if ! judge "$state" >/dev/null 2>&1; then
    echo "  FAIL: copied clean control was rejected before a planted mutation"
    exit 1
  fi
}

judge_status "$run_status" live
judge "$LIVE"
echo "  live shell/GString/build-Git boundary: PASS"

if rg -n 'Environment\.GetEnvironmentVariable(s)?' src/Fogell.Differential/GString.fs src/Fogell.Differential/WalkerOrchestration.fs; then
  echo "  FAIL: a build interpolation/overlay path contains an ambient environment read"
  exit 1
fi

for spec in ordinary-status:7 timeout-status:124 signal-status:143; do
  label=${spec%%:*}; value=${spec##*:}; state="$LAB/planted-$label"; copy_fixture "$state"
  printf '%s\n' "$value" > "$state/run.status"; expect_planted_failure "$label" "$state"
done

state="$LAB/planted-simple"; copy_fixture "$state"; printf 'simple=present' > "$state/ws/job/simple.txt"; expect_planted_failure simple-gstring "$state"
state="$LAB/planted-complex"; copy_fixture "$state"; printf 'complex=present' > "$state/ws/job/complex.txt"; expect_planted_failure complex-gstring "$state"
state="$LAB/planted-shell"; copy_fixture "$state"; printf 'CONTROLLER_API_TOKEN=%s\n' "$API_CONTROL" >> "$state/ws/job/child.env"; expect_planted_failure shell-env "$state"
state="$LAB/planted-git"; copy_fixture "$state"; printf 'SSH_AUTH_SOCK=%s\n' "$SCM_CONTROL" >> "$state/build-git.env"; expect_planted_failure build-git-env "$state"
state="$LAB/planted-shell-tmpdir"; copy_fixture "$state"; printf 'TMPDIR=%s\n' "$TMPDIR_CONTROL" >> "$state/ws/job/child.env"; expect_planted_failure shell-controller-tmpdir "$state" 'child.env has a non-build-local TMPDIR'
state="$LAB/planted-git-tmpdir"; copy_fixture "$state"; printf 'TMPDIR=%s\n' "$TMPDIR_CONTROL" >> "$state/build-git.env"; expect_planted_failure build-git-controller-tmpdir "$state" 'build-git.env has a non-build-local TMPDIR'
state="$LAB/planted-shell-wrong-tmpdir"; copy_fixture "$state"; printf 'TMPDIR=%s\n' "$LAB/foreign-tmp" >> "$state/ws/job/child.env"; expect_planted_failure shell-wrong-tmpdir "$state" 'child.env has a non-build-local TMPDIR'
state="$LAB/planted-git-wrong-tmpdir"; copy_fixture "$state"; printf 'TMPDIR=%s\n' "$LAB/foreign-tmp" >> "$state/build-git.env"; expect_planted_failure build-git-wrong-tmpdir "$state" 'build-git.env has a non-build-local TMPDIR'
state="$LAB/planted-git-missing-tmpdir"; copy_fixture "$state"; awk 'BEGIN { removed = 0 } /^TMPDIR=/ && !removed { removed = 1; next } { print }' "$state/build-git.env" > "$state/build-git.env.tmp"; mv "$state/build-git.env.tmp" "$state/build-git.env"; expect_planted_failure build-git-missing-tmpdir "$state" 'build-git.env does not contain exactly one build-local TMPDIR per call'
state="$LAB/planted-shell-missing-tmpdir"; copy_fixture "$state"; sed -i '/^TMPDIR=/d' "$state/ws/job/child.env"; expect_planted_failure shell-missing-tmpdir "$state" 'child.env does not contain exactly one build-local TMPDIR'
state="$LAB/planted-home"; copy_fixture "$state"; printf '/home/controller' > "$state/ws/job/home.txt"; expect_planted_failure controller-home "$state"
state="$LAB/planted-declared"; copy_fixture "$state"; printf lost > "$state/ws/job/declared.txt"; expect_planted_failure declared-env "$state"
state="$LAB/planted-credential"; copy_fixture "$state"; printf lost > "$state/ws/job/credential.txt"; expect_planted_failure credential-binding "$state"
state="$LAB/planted-mask"; copy_fixture "$state"; sed -i "s/credential=\*\*\*\*/credential=$CREDENTIAL_SECRET/g" "$state/run.log"; expect_planted_failure credential-mask "$state"

echo "FG-222 controller environment proof: PASS"
