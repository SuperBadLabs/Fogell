#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsecCS.dll"
#r "/home/srikanth/.nuget/packages/fparsec/1.1.1/lib/netstandard2.0/FParsec.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Domain.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Ir.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Admission.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.dll"
#r "/home/srikanth/projects/fogell/src/Fogell.Groovy.Parser/bin/Release/net10.0/Fogell.Groovy.Parser.dll"

open Fogell.Groovy.Parser

// Isolated, balanced constructs — one per candidate class from the
// first-bad-line sweep. OK/ERR only; the goal is the verdict list.
let probe name (src: string) =
    match Parser.parse src with
    | Ok _ -> printfn "OK   %s" name
    | Error e ->
        let p = e.Position
        printfn "ERR  %-46s %s @%d:%d" name (e.Message.Replace("\n", " ")) p.Line p.Column

probe "cmd-form inside GString placeholder" "def p = \"${tool 'M3'}/bin\""
probe "named cmd-form initialiser (again)" "def n = tool name: 'x', type: 'y'"
probe "$class map key" "def m = [$class: 'C', size: 10]"
probe "subscript assign closure RHS" "builds['a'] = { echo 'x' }"
probe "gstring subscript assign" "builds[\"${p}-jdk\"] = { echo 'x' }"
probe ".new call" "def mvn = lib.Wrapper.new(this, 'img')"
probe "multiline list literal" "def l = [\n  'a',\n  'b'\n]"
probe "multiline call parens" "f(\n  'a',\n  'b'\n)"
probe "try-catch same line" "try { f() } catch (e) { g() }"
probe "try-catch newline before catch" "try {\n  f()\n}\ncatch (e) {\n  g()\n}"
probe "try-catch catch on brace line" "try {\n  f()\n} catch (e) {\n  g()\n}"
probe "string-named args (parallel)" "parallel 'a': { echo 'x' }, 'b': { echo 'y' }"
probe "in operator" "def t = b in ['x', 'y']"
probe "ternary" "def t = a ? 'x' : 'dev'"
probe "paren-in-ternary" "def t = (b in ['x', 'y']) ? b : 'dev'"
probe "trailing + continuation" "sh \"a\" +\n  \"b\""
probe "gstring property name" "m.\"$name\" = ''"
probe "multiline block comment" "/* line one\n line two */\necho 'x'"
probe "elvis" "def v = a ?: 'd'"
probe "safe navigation" "def v = a?.b"
probe "spread/star import" "import groovy.transform.Field"
probe "annotation @Field" "@Field def x = 1"
probe "c-style for (again)" "for (int i = 0; i < 3; ++i) { echo 'x' }"
probe "typed fn decl (again)" "void f(Maven mvn) { echo 'x' }"
probe "default param fn decl" "def f(x = true) { echo 'x' }"
probe "typed param fn decl" "def f(String s) { echo 'x' }"
