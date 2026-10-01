#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
ocamlopt -O2 unix.cmxa str.cmxa mini_mvn.ml -o mini_mvn
