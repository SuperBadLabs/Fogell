#!/usr/bin/env python3
"""Acceptance-level parity over the full 228-file corpus.

The corpus is NEVER EXECUTED here -- they are untrusted third-party Jenkinsfiles
and the repo's own scorer refuses to run them (only 16 rows are allowlisted for
execution behind the FG-244 fence). So this compares the one boundary that CAN
be measured against real Jenkins without executing anything: ACCEPTANCE.

Jenkins' /pipeline-model-converter/validate is the Declarative linter. It parses
and validates; it does not run a build. That makes it a safe oracle -- but only
for Declarative pipelines. A scripted Jenkinsfile is legitimately "not
Declarative", so the linter cannot adjudicate it, and obtaining Jenkins' verdict
on scripted files would require CPS-compiling them, which means starting a build
of untrusted code. Those are reported separately, not scored.
"""
import json, pathlib, subprocess, sys, urllib.parse, urllib.request, http.cookiejar

JURL = "http://127.0.0.1:18086"
CORPUS = pathlib.Path.home() / "jenkins-oracle-228" / "corpus" / "jenkinsfiles"
VERDICTS = pathlib.Path.home() / "parity" / "fogell-corpus-verdicts.tsv"

jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
with opener.open(f"{JURL}/crumbIssuer/api/json", timeout=20) as r:
    CRUMB = json.load(r)["crumb"]

def lint(text):
    data = urllib.parse.urlencode({"jenkinsfile": text}).encode()
    req = urllib.request.Request(f"{JURL}/pipeline-model-converter/validate", data=data, method="POST")
    req.add_header("Jenkins-Crumb", CRUMB)
    try:
        with opener.open(req, timeout=60) as r:
            body = r.read().decode("utf-8", "replace")
    except Exception as exc:
        return "ERROR", str(exc)[:120]
    if "Jenkinsfile successfully validated" in body:
        return "valid", ""
    return "invalid", " ".join(body.split())[:160]

fog = {}
for line in VERDICTS.read_text().splitlines()[1:]:
    p = line.split("\t")
    if len(p) >= 3:
        fog[p[0]] = (p[1], p[2])

rows = []
for f in sorted(CORPUS.glob("*.Jenkinsfile")):
    jv, detail = lint(f.read_text(errors="replace"))
    fv, fcode = fog.get(f.name, ("?", "?"))
    rows.append((f.name, fv, fcode, jv, detail))

decl = [r for r in rows if r[1] in ("ok", "err")]
scripted = [r for r in rows if r[1] in ("scripted-ok", "scripted-err")]

print(f"corpus files: {len(rows)}   Fogell-declarative: {len(decl)}   Fogell-scripted: {len(scripted)}\n")
print("=== DECLARATIVE SUBSET: Fogell verdict vs Jenkins declarative linter ===")
cells = {}
for _, fv, _, jv, _ in decl:
    cells[(fv, jv)] = cells.get((fv, jv), 0) + 1
print(f"{'':<12}{'jenkins=valid':>16}{'jenkins=invalid':>18}{'jenkins=ERROR':>16}")
for fv in ("ok", "err"):
    print(f"{'fogell='+fv:<12}"
          f"{cells.get((fv,'valid'),0):>16}{cells.get((fv,'invalid'),0):>18}{cells.get((fv,'ERROR'),0):>16}")
agree = cells.get(("ok","valid"),0) + cells.get(("err","invalid"),0)
print(f"\nagreement on the declarative subset: {agree} / {len(decl)}"
      f"  ({100*agree/len(decl):.1f}%)" if decl else "")

print("\n=== DISAGREEMENTS (the interesting rows) ===")
for name, fv, fcode, jv, detail in decl:
    if (fv, jv) not in (("ok", "valid"), ("err", "invalid")):
        print(f"  {name}")
        print(f"      fogell={fv} ({fcode})   jenkins={jv}")
        if detail: print(f"      jenkins says: {detail[:150]}")

sv = {}
for _, fv, _, jv, _ in scripted:
    sv[(fv, jv)] = sv.get((fv, jv), 0) + 1
print(f"\n=== SCRIPTED SUBSET ({len(scripted)}) — NOT SCORED ===")
print("The Declarative linter cannot adjudicate a scripted Jenkinsfile; getting")
print("Jenkins' verdict would mean CPS-compiling untrusted code by starting a build.")
for k, v in sorted(sv.items()):
    print(f"  fogell={k[0]:<13} linter={k[1]:<8} {v}")
