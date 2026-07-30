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

echo -n "fogell-file        (secret file)     -> HTTP "
post_cred <<XML
<org.jenkinsci.plugins.plaincredentials.impl.FileCredentialsImpl>
  <scope>GLOBAL</scope><id>fogell-file</id><description>differential secret file</description>
  <fileName>cert.pem</fileName><secretBytes>$(printf 'cert-file-body' | base64 -w0)</secretBytes>
</org.jenkinsci.plugins.plaincredentials.impl.FileCredentialsImpl>
XML
echo

echo
# The value is BASE64 and fields are TAB-separated, one credential per line. Two rounds of
# review found a delimiter bug in earlier formats — the type inferred from a colon, then
# entries split on a semicolon — and a credential value is arbitrary bytes, so any
# delimiter is wrong. base64 has none. Written to a file rather than an env var so a real
# secret never appears in a process listing.
OUT="${FOGELL_CREDENTIALS_FILE:-$PWD/.fogell-credentials.tsv}"
{
  printf 'fogell-token\ttext\t%s\n'     "$(printf 's3cr3t-value' | base64 -w0)"
  printf 'fogell-userpass\tuserpass\t%s\n' "$(printf 'deploy-bot\np4ssw0rd-value' | base64 -w0)"
  printf 'fogell-file\tfile\t%s\n'      "$(printf 'cert-file-body' | base64 -w0)"
} > "$OUT"
chmod 600 "$OUT"

echo "wrote $OUT"
echo "export FOGELL_CREDENTIALS_FILE='$OUT'"
