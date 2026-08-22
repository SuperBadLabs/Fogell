#!/usr/bin/env bash
set -euo pipefail

base=http://127.0.0.1:18099
case_file=$1
job=$2
[[ "$job" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "invalid job name: $job" >&2; exit 2; }
[ -f "$case_file" ] || { echo "missing case: $case_file" >&2; exit 2; }
jar=$(mktemp /tmp/fg175-jenkins-cookie.XXXXXX)
trap 'rm -f "$jar"' EXIT

crumb_json=$(curl -fsS -c "$jar" -b "$jar" "$base/crumbIssuer/api/json")
crumb_field=$(jq -r .crumbRequestField <<<"$crumb_json")
crumb=$(jq -r .crumb <<<"$crumb_json")
script=$(sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' "$case_file")
xml="<flow-definition plugin=\"workflow-job\"><actions/><description/><keepDependencies>false</keepDependencies><properties/><definition class=\"org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition\" plugin=\"workflow-cps\"><script>${script}</script><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>"

curl -fsS -X POST -b "$jar" -H "$crumb_field: $crumb" "$base/job/$job/doDelete" >/dev/null 2>&1 || true
curl -fsS -X POST -b "$jar" -H "$crumb_field: $crumb" -H 'Content-Type: application/xml' --data-binary "$xml" "$base/createItem?name=$job" >/dev/null
curl -fsS -X POST -b "$jar" -H "$crumb_field: $crumb" "$base/job/$job/build" >/dev/null

for _ in $(seq 1 60); do
    state=$(curl -fsS "$base/job/$job/1/api/json?tree=building,result" 2>/dev/null || true)
    if [[ "$state" == *'"building":false'* ]]; then
        break
    fi
    sleep 1
done

[[ "$state" == *'"building":false'* ]] || { echo "build did not reach a terminal state: $state" >&2; exit 1; }

echo "CASE: $(basename "$case_file")"
echo "CASE-SHA256: $(sha256sum "$case_file" | awk '{print $1}')"
echo "JOB: $job"
echo "BUILD: $base/job/$job/1/"
echo "JENKINS: $(curl -fsSI "$base/login" | tr -d '\r' | sed -n 's/^X-Jenkins: //p' | head -1)"
echo "STATE: $state"
echo 'CONSOLE:'
curl -fsS "$base/job/$job/1/consoleText"
echo 'WORKSPACE:'
ssh luigi "podman exec jenkins-lab sh -c 'if [ -d /var/jenkins_home/workspace/$job ]; then echo STATUS: present; cd /var/jenkins_home/workspace/$job; find . -type f -print | sort; else echo STATUS: absent; fi'"

curl -fsS -X POST -b "$jar" -H "$crumb_field: $crumb" "$base/job/$job/doDelete" >/dev/null
