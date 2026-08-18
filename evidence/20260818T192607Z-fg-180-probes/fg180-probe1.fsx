#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsecCS.dll"
#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsec.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Domain.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Ir.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Admission.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.Parser.dll"

open Fogell.Groovy.Parser

let probe name src =
    printfn "=== %s ===" name
    printfn "SRC: %s" src
    match Parser.parse src with
    | Ok script -> printfn "OK: %A" script
    | Error e -> printfn "ERR: %s @%A (%A)" e.Message e.Position e.Code
    printfn ""

// The board's OPEN QUESTION: positional command-form initialiser —
// a call, or a two-statement misparse (`def m = tool` then bare string)?
probe "positional command-form initialiser" "def m = tool 'Maven 3.3.9'"

// The measured blocker: NAMED command-form initialiser
probe "named command-form initialiser" "def pom = readMavenPom file: 'pom.xml'"

// Control: statement-position command form (the row says commandArgs handles this)
probe "named command-form statement" "readMavenPom file: 'pom.xml'"
probe "positional command-form statement" "tool 'Maven 3.3.9'"

// Slashy string (FG-141) in plain Groovy expression position
probe "slashy string initialiser" "def p = /a}b/"
probe "division initialiser" "def x = 10 / 2"
probe "division then use" "def x = 10 / 2\necho x"
