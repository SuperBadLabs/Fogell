#!/usr/bin/env bash
# FG-044. Mirror the differential's test credentials into the pinned Jenkins.
#
# The receipts for `withCredentials` are only reproducible if BOTH engines hold the
# same credential values, so this provisions Jenkins and prints the matching
# FOGELL_CREDENTIALS value for the Fogell side. The values are obvious fakes and are
# committed on purpose: a receipt nobody else can reproduce is not evidence.
set -uo pipefail
: "${FOGELL_JENKINS_URL:=http://127.0.0.1:18099}"

JAR="$(mktemp)"
trap 'rm -f "$JAR"' EXIT
CRUMB="$(curl -s -c "$JAR" -b "$JAR" "$FOGELL_JENKINS_URL/crumbIssuer/api/json" | sed 's/.*"crumb":"\([^"]*\)".*/\1/')"

post_cred() {
  curl -s -o /dev/null -w "%{http_code}" -c "$JAR" -b "$JAR" \
    -H "Jenkins-Crumb: $CRUMB" -H "Content-Type: application/xml" \
    --data-binary @- -X POST \
    "$FOGELL_JENKINS_URL/credentials/store/system/domain/_/createCredentials"
}

echo -n "fogell-token       (secret text)     -> HTTP "
post_cred <<'XML'
<org.jenkinsci.plugins.plaincredentials.impl.StringCredentialsImpl>
  <scope>GLOBAL</scope><id>fogell-token</id><description>differential secret text</description>
  <secret>s3cr3t-value</secret>
</org.jenkinsci.plugins.plaincredentials.impl.StringCredentialsImpl>
XML
echo

echo -n "fogell-userpass    (username/pass)   -> HTTP "
post_cred <<'XML'
<com.cloudbees.plugins.credentials.impl.UsernamePasswordCredentialsImpl>
  <scope>GLOBAL</scope><id>fogell-userpass</id><description>differential user/pass</description>
  <username>deploy-bot</username><password>p4ssw0rd-value</password>
</com.cloudbees.plugins.credentials.impl.UsernamePasswordCredentialsImpl>
XML
echo

echo
echo "export FOGELL_CREDENTIALS='fogell-token=s3cr3t-value,fogell-userpass=deploy-bot:p4ssw0rd-value'"
