#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsecCS.dll"
#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsec.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Domain.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Ir.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Admission.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.Parser.dll"

open Fogell.Groovy.Parser

// Isolated construct probes for the five FG-180 script blocks and the
// large scripted classes. Named from source positions, tested one at a time
// so the parser's stop point is measured, not matched by eye.
let probe name (src: string) =
    printfn "=== %s ===" name
    match Parser.parse src with
    | Ok script -> printfn "OK: %A" script
    | Error e -> printfn "ERR: %s @%A" e.Message e.Position
    printfn ""

// Class A: trailing closure after a call (13+ scripted files stop at `{`)
probe "bare call + closure" "node {\n  echo 'hi'\n}"
probe "paren call + closure" "node('docker') {\n  echo 'hi'\n}"
probe "dotted call + closure" "axes.values().combinations {\n  echo 'hi'\n}"

// Class B: top-level function declarations (9 files stop at `(`)
probe "def function decl" "def mvn(args) {\n  sh args\n}"
probe "typed function decl" "void report(Maven mvn) {\n  echo 'x'\n}"
probe "default param decl" "String pdfName(boolean includeDate = true) {\n  return 'x'\n}"

// Romeh inner 7:21 — reconstruct the block's first lines
probe "romeh shape"
    "def mvnHome\nnode {\n  mvnHome = tool 'M3'\n  stage('x') {\n    echo 'y'\n  }\n  if (isUnix()) {\n    sh 'ls'\n  }\n}"

// varunpalekar inner 6:21: `no line break before '['`
probe "assignment in env-style block" "DB_USERNAME = \"laravel_test\""

// jenkinsci_jenkinsfile-runner: C-style for
probe "c-style for" "for (int i = 0; i < 3; ++i) {\n  echo 'x'\n}"
