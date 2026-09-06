#!/usr/bin/env bash
# Regenerate McLoving's mTLS material IN PLACE and restart controller+agent.
# Deliberately does NOT touch the database (mc-up.sh would podman rm the
# postgres container). Certs get 30 days, not the original 1 -- the rig had been
# running on a cert expired since Sep 3, surviving only on an already
# established session, and any restart would have failed.
set -uo pipefail
R=$HOME/faceoff2/mcrun
source "$R/env"
ORG=$MCLOVING_ORGANIZATION_ID
cd "$R/tls"
cp -a identity-bindings.txt identity-bindings.txt.bak 2>/dev/null || true
openssl req -new -newkey rsa:2048 -nodes -x509 -days 30 -subj "/CN=mcloving-faceoff-ca" -keyout ca-key.pem -out ca.pem 2>/dev/null
printf 'subjectAltName=DNS:controller.internal,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' > server.ext
openssl req -new -newkey rsa:2048 -nodes -subj "/CN=controller.internal" -keyout server-key.pem -out server.csr 2>/dev/null
openssl x509 -req -days 30 -in server.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -extfile server.ext -out server.pem 2>/dev/null
printf 'extendedKeyUsage=clientAuth\n' > agent.ext
openssl req -new -newkey rsa:2048 -nodes -subj "/CN=faceoff-agent" -keyout agent-key.pem -out agent.csr 2>/dev/null
openssl x509 -req -days 30 -in agent.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -extfile agent.ext -out agent.pem 2>/dev/null
openssl x509 -in agent.pem -outform DER -out agent.der 2>/dev/null
DIGEST=$(sha256sum agent.der | cut -d' ' -f1)
echo "$DIGEST faceoff-agent trusted-linux $ORG" > identity-bindings.txt
echo "new agent binding ${DIGEST:0:16}...  valid to $(openssl x509 -in agent.pem -noout -enddate | cut -d= -f2)"

pkill -9 -f "$HOME/faceoff2/bin/mcloving" 2>/dev/null; sleep 2
cd "$R" && source ./env
setsid "$HOME/faceoff2/bin/mcloving-controller" >> controller.log 2>&1 < /dev/null &
sleep 8
bash "$HOME/faceoff2/mc-agent-up.sh"
sleep 3
curl -s -o /dev/null -w "controller api=%{http_code}\n" \
  "$MCLOVING_URL/api/v1/organizations/$ORG/projects/$MCLOVING_PROJECT_ID/builds" \
  -H "Authorization: Bearer $MCLOVING_API_TOKEN"
tail -2 "$R/agent.log"
