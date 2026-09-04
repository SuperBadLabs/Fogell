module Fogell.Groovy.Tests

open Expecto
open Fogell.Groovy
open Fogell.Groovy.Interpreter
open Fogell.Admission

let private parseOk src =
    match Fogell.Groovy.Parser.Parser.parse src with
    | Ok s -> s
    | Error e -> failtestf "parse failed: %s" (string e)

let private parses src =
    match Fogell.Groovy.Parser.Parser.parse src with
    | Ok _ -> true
    | Error _ -> false

let private steps = set [ "sh"; "echo"; "node"; "stage"; "library"; "import"; "archiveArtifacts" ]

let private run src =
    Interpreter.runDefault steps (parseOk src)

let private stepNames (o: Outcome) =
    o.Effects |> List.map (function StepCall(n, _, _) -> n)

let private stepArgs (o: Outcome) =
    o.Effects
    |> List.map (function
        | StepCall(n, pos, _) -> n, pos |> List.map Value.toDisplay)

module CarrierFixtures =

    type NestedHostCarrier<'host> =
        { HostObject: 'host }

    // Keep the reviewed VScmMap -> record -> Entries shape intact, then plant
    // an arbitrary host-object reference below another record/container edge. A direct-field-only audit
    // accepts both fixtures and therefore cannot satisfy FG-072.
    type PreservedScmMap<'host> =
        { Entries: Map<string, string>
          Metadata: Map<string, NestedHostCarrier<'host>> }

    type PreservedCarrierShape<'host> =
        | VScmMap of PreservedScmMap<'host>

module private CarrierSchema =

    type Finding =
        { Path: string
          TypeName: string
          Reason: string }

    type Audit =
        { Manifest: string list
          Findings: Finding list }

    let private optionType = typedefof<option<_>>
    let private listType = typedefof<list<_>>
    let private mapType = typedefof<Map<_, _>>
    let private refType = typeof<int ref>.GetGenericTypeDefinition()

    let private fullName (candidate: System.Type) =
        if isNull candidate.FullName then candidate.Name else candidate.FullName

    let private genericName (candidate: System.Type) =
        let definitionName = fullName (candidate.GetGenericTypeDefinition())
        let marker = definitionName.IndexOf('`')
        if marker < 0 then definitionName else definitionName.Substring(0, marker)

    let private isGeneric (definition: System.Type) (candidate: System.Type) =
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() = definition

    let rec private renderType (candidate: System.Type) =
        if Microsoft.FSharp.Reflection.FSharpType.IsTuple candidate then
            Microsoft.FSharp.Reflection.FSharpType.GetTupleElements candidate
            |> Array.map renderType
            |> String.concat " * "
            |> sprintf "tuple<%s>"
        elif isGeneric optionType candidate then
            $"option<{renderType (candidate.GetGenericArguments().[0])}>"
        elif isGeneric listType candidate then
            $"list<{renderType (candidate.GetGenericArguments().[0])}>"
        elif isGeneric mapType candidate then
            let arguments = candidate.GetGenericArguments()
            $"map<{renderType arguments.[0]}, {renderType arguments.[1]}>"
        elif isGeneric refType candidate then
            $"ref<{renderType (candidate.GetGenericArguments().[0])}>"
        elif candidate.IsGenericType then
            candidate.GetGenericArguments()
            |> Array.map renderType
            |> String.concat ", "
            |> fun arguments -> $"{genericName candidate}<{arguments}>"
        else
            fullName candidate

    let private formatUnion (candidate: System.Type) =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases candidate
        |> Array.map (fun unionCase ->
            let fields =
                unionCase.GetFields()
                |> Array.map (fun field -> $"{field.Name}:{renderType field.PropertyType}")
                |> String.concat ", "

            if fields = "" then unionCase.Name else $"{unionCase.Name}({fields})")
        |> String.concat " | "
        |> sprintf "union %s"

    let private formatRecord (candidate: System.Type) =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields candidate
        |> Array.map (fun field -> $"{field.Name}:{renderType field.PropertyType}")
        |> String.concat "; "
        |> sprintf "record { %s }"

    let audit (root: System.Type) =
        // The dictionary is both the cycle fence (Value -> Env -> Value and the
        // recursive AST) and the first full discovery path for each definition.
        // Reviewed containers are transparent; every F# union/record schema is
        // manifested; every other transitive type fails closed as a finding.
        let definitions = System.Collections.Generic.Dictionary<System.Type, string>()
        let findings = ResizeArray<Finding>()

        let addFinding path candidate reason =
            findings.Add(
                { Path = path
                  TypeName = renderType candidate
                  Reason = reason }
            )

        let rec visit path (candidate: System.Type) =
            if
                candidate = typeof<bool>
                || candidate = typeof<int64>
                || candidate = typeof<single>
                || candidate = typeof<string>
            then
                ()
            elif Microsoft.FSharp.Reflection.FSharpType.IsTuple candidate then
                Microsoft.FSharp.Reflection.FSharpType.GetTupleElements candidate
                |> Array.iteri (fun index item -> visit $"{path}.tuple[{index}]" item)
            elif isGeneric optionType candidate then
                visit $"{path}.option" (candidate.GetGenericArguments().[0])
            elif isGeneric listType candidate then
                visit $"{path}.list" (candidate.GetGenericArguments().[0])
            elif isGeneric mapType candidate then
                let arguments = candidate.GetGenericArguments()
                visit $"{path}.map-key" arguments.[0]
                visit $"{path}.map-value" arguments.[1]
            elif isGeneric refType candidate then
                visit $"{path}.ref" (candidate.GetGenericArguments().[0])
            elif Microsoft.FSharp.Reflection.FSharpType.IsUnion candidate then
                if definitions.TryAdd(candidate, path) then
                    for unionCase in Microsoft.FSharp.Reflection.FSharpType.GetUnionCases candidate do
                        for field in unionCase.GetFields() do
                            visit $"{path}.{unionCase.Name}.{field.Name}" field.PropertyType
            elif Microsoft.FSharp.Reflection.FSharpType.IsRecord candidate then
                if definitions.TryAdd(candidate, path) then
                    for field in Microsoft.FSharp.Reflection.FSharpType.GetRecordFields candidate do
                        visit $"{path}.{field.Name}" field.PropertyType
            else
                addFinding path candidate "unreviewed transitive carrier type"

        visit (renderType root) root

        let manifest =
            definitions
            |> Seq.map (fun entry ->
                let schema =
                    if Microsoft.FSharp.Reflection.FSharpType.IsUnion entry.Key then
                        formatUnion entry.Key
                    else
                        formatRecord entry.Key

                $"{entry.Value} :: {renderType entry.Key} = {schema}")
            |> Seq.sort
            |> Seq.toList

        { Manifest = manifest
          Findings = findings |> Seq.sortBy (fun finding -> finding.Path) |> Seq.toList }

/// FG-011: every construct below was confirmed necessary by measuring the real
/// corpus. Grammar coverage is demand-driven, not speculative.
let grammar =
    testList
        "FG-011 scripted grammar"
        [ test "shebang, annotation and import are tolerated" {
              Expect.isTrue (parses "#!/usr/bin/env groovy\n@Library('x') _\nimport a.b.C\nsh 'make'\n") "preamble"
          }
          test "@Library is preserved as a library() call, not discarded" {
              let o = run "@Library('mylib') _\nsh 'make'\n"
              Expect.contains (stepNames o) "library" "dependency survives into the AST"
          }
          test "trailing commas in lists and maps" {
              Expect.isTrue (parses "def a = [1, 2,]\ndef b = [k: 1,]\n") "sepEndBy"
          }
          test "slashy strings and regex operators" {
              Expect.isTrue (parses "def m = ('main' =~ /ma.n/)\ndef n = ('x' ==~ /x/)\n") "=~ and ==~"
          }
          test "typed closure parameters" {
              Expect.isTrue (parses "def f = { String s -> s }\n") "typed params"
          }
          test "final and postfix increment" {
              Expect.isTrue (parses "final X = 1\ndef i = 0\ni++\n") "final, ++"
          }
          test "C-style for loop" {
              Expect.isTrue (parses "for (int i = 0; i < 3; i++) { echo 'x' }\n") "C-style for"
          }
          test "ranges, switch, instanceof, multi-assign" {
              Expect.isTrue (parses "def r = (1..3)\n") "range"
              Expect.isTrue (parses "switch (a) { case 1: break\n default: break }\n") "switch"
              Expect.isTrue (parses "if (a instanceof String) { echo 'x' }\n") "instanceof"
              Expect.isTrue (parses "def (a, b) = [1, 2]\n") "multi-assign"
          }
          test "spread-dot, ordinary property and safe navigation keep distinct AST shapes" {
              let source =
                  "def spread = rows*.child\n"
                  + "def nested = rows*.child*.name\n"
                  + "def safeAfterSpread = rows*.child?.name\n"
                  + "def ordinary = rows.child\n"
                  + "def safe = rows?.child\n"

              match parseOk source with
              | [ SDef("spread", Some(ESpreadProp(EVar "rows", "child")))
                  SDef("nested", Some(ESpreadProp(ESpreadProp(EVar "rows", "child"), "name")))
                  SDef("safeAfterSpread", Some(ESafeProp(ESpreadProp(EVar "rows", "child"), "name")))
                  SDef("ordinary", Some(EProp(EVar "rows", "child")))
                  SDef("safe", Some(ESafeProp(EVar "rows", "child"))) ] -> ()
              | other -> failtestf "property operators collapsed in the AST: %A" other
          }
          test "every assignment-like direct spread target stays visible to the refusal scanner" {
              let source =
                  "rows*.name = 'x'\n"
                  + "rows*.name += 'x'\n"
                  + "rows*.name++\n"
                  + "rows*.name--\n"
                  + "rows*.child.name = 'x'\n"
                  + "rows*.child?.name = 'x'\n"

              match parseOk source with
              | [ SAssign(ESpreadProp(EVar "rows", "name"), _)
                  SAssign(ESpreadProp(EVar "rows", "name"), _)
                  SAssign(ESpreadProp(EVar "rows", "name"), _)
                  SAssign(ESpreadProp(EVar "rows", "name"), _)
                  SAssign(EProp(ESpreadProp(EVar "rows", "child"), "name"), _)
                  SAssign(ESafeProp(ESpreadProp(EVar "rows", "child"), "name"), _) ] as script ->
                  Expect.isTrue (Ast.containsSpreadAssignment script) "all target wrappers are detected"
              | other -> failtestf "assignment target lost its spread node: %A" other

              Expect.isFalse
                  (Ast.containsSpreadAssignment (parseOk "rows.name = 'x'\n"))
                  "ordinary assignment remains outside the refusal"
          }
          test "index updates retain syntax while index results remain outer receiver boundaries" {
              let outer =
                  parseOk (
                      "rows*.child[0].name = 'x'\n"
                      + "rows*.child[0]?.name = 'x'\n"
                      + "rows*.children[0][1].name = 'x'\n"
                      + "rows*.children[0].first().name = 'x'\n"
                  )

              match outer with
              | [ SAssign(EProp(EIndex(ESpreadProp(EVar "rows", "child"), EInt 0L), "name"), _)
                  SAssign(ESafeProp(EIndex(ESpreadProp(EVar "rows", "child"), EInt 0L), "name"), _)
                  SAssign(
                      EProp(
                          EIndex(EIndex(ESpreadProp(EVar "rows", "children"), EInt 0L), EInt 1L),
                          "name"
                      ),
                      _
                  )
                  SAssign(
                      EProp(
                          ECall(
                              MethodCall(EIndex(ESpreadProp(EVar "rows", "children"), EInt 0L), "first"),
                              [],
                              None
                          ),
                          "name"
                      ),
                      _
                  ) ] -> ()
              | other -> failtestf "index-result write boundaries lost their AST shapes: %A" other

              Expect.isFalse (Ast.containsSpreadAssignment outer) "outer writes are not spread l-values"
              Expect.isFalse
                  (Ast.containsSpreadDerivedIndexAssignment outer)
                  "an outer property or method write does not become a direct index write"

              let direct =
                  parseOk (
                      "rows*.child[0] = 'x'\n"
                      + "rows*.child[0] += 'x'\n"
                      + "rows*.child[0]++\n"
                      + "rows*.child[0]--\n"
                      + "rows*.children[0][1] = 'x'\n"
                  )

              match direct with
              | [ SAssign(EIndex(ESpreadProp(EVar "rows", "child"), EInt 0L), _)
                  SIndexCompoundAssign(EIndex(ESpreadProp(EVar "rows", "child"), EInt 0L), "+", _)
                  SIndexPostfixAssign(EIndex(ESpreadProp(EVar "rows", "child"), EInt 0L), "+")
                  SIndexPostfixAssign(EIndex(ESpreadProp(EVar "rows", "child"), EInt 0L), "-")
                  SAssign(EIndex(EIndex(ESpreadProp(EVar "rows", "children"), EInt 0L), EInt 1L), _) ] -> ()
              | other -> failtestf "direct projected-index targets lost their AST shapes: %A" other

              Expect.isFalse (Ast.containsSpreadAssignment direct) "index results stop the spread-write traversal"
              Expect.isTrue
                  (Ast.containsSpreadDerivedIndexAssignment direct)
                  "the legacy audit scanner still identifies every direct spread-derived index form"
          }
          test "spread reads used to compute a write target are not themselves write paths" {
              let source =
                  "foo(rows*.name).bar = 1\n"
                  + "foo(values: rows*.name).bar = 1\n"
                  + "holder.foo { rows*.name }.bar = 1\n"
                  + "xs[rows*.name[0]] = 'x'\n"

              match parseOk source with
              | [ SAssign(
                      EProp(ECall(FreeCall "foo", [ APos(ESpreadProp(EVar "rows", "name")) ], None), "bar"),
                      _
                  )
                  SAssign(
                      EProp(ECall(FreeCall "foo", [ ANamed("values", ESpreadProp(EVar "rows", "name")) ], None), "bar"),
                      _
                  )
                  SAssign(EProp(ECall(MethodCall(EVar "holder", "foo"), [], Some trailing), "bar"), _)
                  SAssign(EIndex(EVar "xs", EIndex(ESpreadProp(EVar "rows", "name"), EInt 0L)), _) ] as script ->
                  Expect.equal
                      trailing.Body
                      [ SExpr(ESpreadProp(EVar "rows", "name")) ]
                      "trailing closure keeps the spread read visible"

                  Expect.isFalse
                      (Ast.containsSpreadAssignment script)
                      "call arguments, trailing closure reads and index keys are not write receivers"
              | other -> failtestf "spread-read target probes lost their AST shapes: %A" other

              Expect.isFalse
                  (Ast.containsSpreadAssignment (parseOk "rows*.child.first().name = 'x'\n"))
                  "a method result starts a fresh ordinary write receiver"
          }

          test "method-call result boundaries preserve exact property, safe and index AST shapes" {
              let source =
                  "rows*.child.first().name = 'x'\n"
                  + "rows*.child.first()?.name = 'x'\n"
                  + "rows*.child.first()[0] = 'x'\n"
                  + "rows*.child.find(index: 0).name = 'x'\n"
                  + "rows*.child.find { true }.name = 'x'\n"

              match parseOk source with
              | [ SAssign(
                      EProp(ECall(MethodCall(ESpreadProp(EVar "rows", "child"), "first"), [], None), "name"),
                      _
                  )
                  SAssign(
                      ESafeProp(ECall(MethodCall(ESpreadProp(EVar "rows", "child"), "first"), [], None), "name"),
                      _
                  )
                  SAssign(
                      EIndex(ECall(MethodCall(ESpreadProp(EVar "rows", "child"), "first"), [], None), EInt 0L),
                      _
                  )
                  SAssign(
                      EProp(
                          ECall(
                              MethodCall(ESpreadProp(EVar "rows", "child"), "find"),
                              [ ANamed("index", EInt 0L) ],
                              None
                          ),
                          "name"
                      ),
                      _
                  )
                  SAssign(EProp(ECall(MethodCall(ESpreadProp(EVar "rows", "child"), "find"), [], Some trailing), "name"), _) ] as script ->
                  Expect.equal trailing.Body [ SExpr(EBool true) ] "trailing closure stays attached to the boundary call"
                  Expect.isFalse (Ast.containsSpreadAssignment script) "no call-result wrapper is a spread write"
                  Expect.isTrue
                      (Ast.containsSpreadDerivedIndexAssignment script)
                      "the direct method-result index retains spread-read provenance for preflight"
              | other -> failtestf "method-result write boundaries lost their AST shapes: %A" other

              Expect.isTrue
                  (Ast.containsSpreadDerivedIndexAssignment
                      (parseOk "rows*.holder.first()['slot'] = 'x'\n"))
                  "a statically ambiguous map/list receiver is conservatively classified"

              Expect.isTrue
                  (Ast.containsSpreadDerivedIndexAssignment
                      (parseOk "rows*.children?.first()[0] = 'x'\n"))
                  "safe method calls preserve the same direct-index provenance"

              Expect.isTrue
                  (Ast.containsSpreadDerivedIndexAssignment
                      (parseOk "rows*.children.first().first()[0] = 'x'\n"))
                  "nested method-call chains preserve provenance through every receiver"

              Expect.isTrue
                  (Ast.containsSpreadAssignment (parseOk "rows*.child.first()*.name = 'x'\n"))
                  "a new spread operator after the call remains an actual spread target"

              Expect.isTrue
                  (Ast.containsSpreadAssignment (parseOk "holder.foo { rows*.name = 'x' }.bar = 1\n"))
                  "the separate statement traversal still sees a write inside a call closure"
          }

          test "a closure suffix on a completed call becomes a call-result invocation" {
              match parseOk "x() { true } { false }" with
              | [ SExpr(
                    ECall(
                        MethodCall(ECall(FreeCall "x", [], Some first), "call"),
                        [],
                        Some second
                    )
                ) ] ->
                  Expect.equal first.Body [ SExpr(EBool true) ] "the first closure remains attached to x"
                  Expect.equal second.Body [ SExpr(EBool false) ] "the suffix closure is retained on the result call"
              | other -> failtestf "the second closure was consumed without an AST receiver frame: %A" other
          }

          // FG-174. Both parsers hold this rule, because both produce calls and a rule
          // held by only one of them is the shape of half the findings on this branch.
          test "a duplicate named argument is refused, parenthesised" {
              Expect.isFalse (parses "sh(script: 'exit 7', returnStatus: true, returnStatus: 'false')\n") "map literal duplicate"
          }

          test "a duplicate named argument is refused, command form" {
              // The form without parentheses goes through a DIFFERENT parser path, and
              // covering only the parenthesised one would leave the commonest spelling of
              // a step call unchecked.
              Expect.isFalse (parses "sh script: 'exit 7', returnStatus: true, returnStatus: false\n") "command form too"
          }

          test "DISTINCT named arguments still parse in both forms" {
              Expect.isTrue (parses "sh(script: 'make', returnStatus: true)\n") "parenthesised"
              Expect.isTrue (parses "sh script: 'make', returnStatus: true\n") "command form"
          }

          test "user-defined functions are recognised, not treated as unknown steps" {
              let script = parseOk "def helper(a) { return a }\nhelper(1)\n"
              Expect.contains (Ast.definedFunctions script) "helper" "declared function found"
          } ]

/// FG-190/192. Trivia is lexed forward and its break fact is scoped to one
/// exact stream index. Expression-bearing delimiters may continue an index
/// across a break; statement bodies and the command/typed/return seams may not.
let fg190192TriviaState =
    testList
        "FG-190/192 forward trivia state"
        [ test "a same-line block comment keeps a postfix index attached" {
              match parseOk "def xs = [1]\ndef value = xs /* same line */ [0]\n" with
              | [ SDef("xs", _); SDef("value", Some(EIndex(EVar "xs", EInt 0L))) ] -> ()
              | other -> failtestf "same-line index split unexpectedly: %A" other
          }

          test "comment text containing an opener stays non-nesting and preserves a same-line index" {
              match parseOk "def xs = [1]\ndef value = xs /* text /* is ordinary */ [0]\n" with
              | [ SDef("xs", _); SDef("value", Some(EIndex(EVar "xs", EInt 0L))) ] -> ()
              | other -> failtestf "nested-looking comment changed the index: %A" other
          }

          test "a break before nested-looking comment text ends the top-level expression" {
              let source = "def xs = [1]\nxs /*\ntext /* is ordinary */ [0]\n"

              match parseOk source with
              | [ SDef("xs", _); SExpr(EVar "xs"); SExpr(EList [ EInt 0L ]) ] -> ()
              | other -> failtestf "forward comment boundary was lost: %A" other
          }

          test "the FG-187 top-level split remains strict" {
              match parseOk "xs\n[0]\n" with
              | [ SExpr(EVar "xs"); SExpr(EList [ EInt 0L ]) ] -> ()
              | other -> failtestf "top-level lines merged: %A" other
          }

          test "line comments record LF, CR and CRLF terminators" {
              for label, ending in [ "LF", "\n"; "CR", "\r"; "CRLF", "\r\n" ] do
                  match parseOk ("xs // comment" + ending + "[0]") with
                  | [ SExpr(EVar "xs"); SExpr(EList [ EInt 0L ]) ] -> ()
                  | other -> failtestf "%s line-comment terminator was lost: %A" label other
          }

          test "line and block trivia preserve exact LF, CR and CRLF error positions" {
              let expectPosition label expected source =
                  match Fogell.Groovy.Parser.Parser.parse source with
                  | Error e -> Expect.equal e.Position expected $"{label}: physical error position"
                  | Ok parsed -> failtestf "%s unexpectedly parsed as %A" label parsed

              for label, ending in [ "LF", "\n"; "CR", "\r"; "CRLF", "\r\n" ] do
                  expectPosition
                      $"{label} line comment"
                      { Line = 2L; Column = 1L }
                      ("def x = 1 // comment" + ending + "@")

                  expectPosition
                      $"{label} block comment"
                      { Line = 2L; Column = 14L }
                      ("def x = 1 /* comment" + ending + "continued */ @")
          }

          test "a parenthesised expression may continue an index across a break" {
              match parseOk "(xs\n[0])\n" with
              | [ SExpr(EIndex(EVar "xs", EInt 0L)) ] -> ()
              | other -> failtestf "grouped index did not continue: %A" other
          }

          test "a call argument is an expression-bearing group" {
              match parseOk "pick(xs\n[0])\n" with
              | [ SExpr(ECall(FreeCall "pick", [ APos(EIndex(EVar "xs", EInt 0L)) ], None)) ] -> ()
              | other -> failtestf "call argument lost its grouped index: %A" other
          }

          test "a GString placeholder is an expression-bearing group" {
              match parseOk "def value = \"${xs\n[0]}\"\n" with
              | [ SDef("value", Some(EGString [ GExpr(EIndex(EVar "xs", EInt 0L)) ])) ] -> ()
              | other -> failtestf "GString placeholder lost its grouped index: %A" other
          }

          test "group exit keeps an immediate outer index attached" {
              match parseOk "(xs\n[0])[1]\n" with
              | [ SExpr(EIndex(EIndex(EVar "xs", EInt 0L), EInt 1L)) ] -> ()
              | other -> failtestf "outer index chain changed: %A" other
          }

          test "group exit keeps ordinary binary and index chains intact" {
              match parseOk "(xs\n[0]) + ys[1]\n" with
              | [ SExpr(EBinary("+", EIndex(EVar "xs", EInt 0L), EIndex(EVar "ys", EInt 1L))) ] -> ()
              | other -> failtestf "binary/index chain changed: %A" other
          }

          test "nested expression groups retain continuation depth" {
              Expect.isTrue
                  (parses "pick(([xs\n[0]])[0])\n")
                  "nested call, paren, list and index groups all stay expression-bearing"
          }

          test "a closure body resets inherited expression depth" {
              match parseOk "pick({ xs\n[0] })\n" with
              | [ SExpr(ECall(FreeCall "pick", [ APos(EClosure c) ], None)) ] ->
                  Expect.equal
                      c.Body
                      [ SExpr(EVar "xs"); SExpr(EList [ EInt 0L ]) ]
                      "closure statements remain split inside an outer call group"
              | other -> failtestf "closure argument shape changed: %A" other
          }

          test "unterminated block trivia survives speculative backtracking as a refusal" {
              for source in
                  [ "def xs = [1]\nxs /* never closed"
                    "def value = tool /* never closed"
                    "def value = (xs /* never closed" ] do
                  match Fogell.Groovy.Parser.Parser.parse source with
                  | Error e ->
                      Expect.stringContains
                          (string e)
                          "terminated block comment"
                          "the trivia refusal remains visible after attempted alternatives"
                  | Ok parsed -> failtestf "unterminated block comment parsed as %A" parsed
          }

          test "a failed typed-declaration attempt restores trivia state" {
              match parseOk "echo msg\n[0]\n" with
              | [ SExpr(ECall(FreeCall "echo", [ APos(EVar "msg") ], None)); SExpr(EList [ EInt 0L ]) ] -> ()
              | other -> failtestf "attempted declaration leaked state into fallback: %A" other
          }

          test "command expression, typed seam and return remain strict across comment breaks" {
              match parseOk "def value = tool /*\ntext /* ordinary */ 'M3'\n" with
              | [ SDef("value", Some(EVar "tool")); SExpr(EStr "M3") ] -> ()
              | other -> failtestf "command expression crossed a break: %A" other

              match parseOk "echo msg /*\ntext /* ordinary */ (x) { echo 'y' }\n" with
              | SExpr(ECall(FreeCall "echo", _, _)) :: _ -> ()
              | other -> failtestf "typed declaration seam crossed a break: %A" other

              match parseOk "return /*\ntext /* ordinary */ value\n" with
              | [ SReturn None; SExpr(EVar "value") ] -> ()
              | other -> failtestf "return swallowed the next line: %A" other
          }

          test "statement command calls cannot swallow newline or comment-break successors" {
              let expected =
                  [ SExpr(EVar "pwd")
                    SExpr(ECall(FreeCall "sh", [ APos(EStr "danger") ], None)) ]

              Expect.equal (parseOk "pwd\nsh 'danger'\n") expected "plain newline keeps both statements"

              Expect.equal
                  (parseOk "pwd /*\ntext /* ordinary */ sh 'danger'\n")
                  expected
                  "forward non-nesting comment break keeps both statements"
          }

          test "switch headers are expression groups and switch bodies reset depth" {
              let source = "switch (xs\n[0]) { case 1: item\n[0] }\n"

              match parseOk source with
              | [ SSwitch(
                    EIndex(EVar "xs", EInt 0L),
                    [ Some(EInt 1L), [ SExpr(EVar "item"); SExpr(EList [ EInt 0L ]) ] ]
                  ) ] -> ()
              | other -> failtestf "switch group/body state changed: %A" other
          }

          test "for headers retain expression grouping without leaking into their bodies" {
              Expect.isTrue
                  (parses "for (item in xs\n[0]) { echo item }\n")
                  "for-in source continues inside its header"

              Expect.isTrue
                  (parses "for (int i = xs\n[0]; i < 1; i++) { echo i }\n")
                  "C-style initializer continues inside its header"

              match parseOk "for (item in xs) { item\n[0] }\n" with
              | [ SForIn(_, _, [ SExpr(EVar "item"); SExpr(EList [ EInt 0L ]) ]) ] -> ()
              | other -> failtestf "for body inherited header group depth: %A" other
          } ]

/// FG-013: the sandbox is the reason ADR 0002 was affordable. These tests are
/// the acceptance criterion.
let sandbox =
    testList
        "FG-013 sandbox denies escape"
        [ test "file access is denied by name" {
              let o = run "def f = new File('/etc/passwd')\n"

              match o.Fault with
              | Some(Denied d) -> Expect.stringContains d.Attempted "File" "names what was attempted"
              | other -> failtestf "expected a denial, got %A" other
          }

          test "process execution is denied" {
              let o = run "'ls'.execute()\n"
              Expect.isSome o.Fault "denied"
          }

          test "reflection is denied" {
              let o = run "def c = getClass()\n"
              Expect.isSome o.Fault "denied"
          }

          test "an unregistered step is denied and names itself" {
              let o = run "kubernetesDeploy(configs: 'x')\n"

              match o.Fault with
              | Some(Denied d) ->
                  Expect.equal d.Attempted "kubernetesDeploy" "names the step"
                  Expect.stringContains d.Reason "registered step" "explains why"
              | other -> failtestf "expected a denial, got %A" other
          }

          test "a registered step becomes a host effect, never a direct action" {
              let o = run "sh 'make'\necho 'done'\n"
              Expect.equal (stepNames o) [ "sh"; "echo" ] "effects recorded in order"
              Expect.isNone o.Fault "no fault"
          }

          test "a trailing closure ARGUMENT is a body, not a positional (batch mode)" {
              // The normalisation above the host split existed for the LIVE path only:
              // batch recorded the raw positional list, so this logged the closure as an
              // argument AND ran it as a body — the same block counted twice. Raised in
              // review on PR #53. Nothing in the walker read `Effects`, so no receipt
              // could have caught it; this is the only thing that can.
              let o = run "node 'label', { sh 'make' }\n"

              Expect.equal
                  (stepArgs o)
                  [ "node", [ "label" ]; "sh", [ "make" ] ]
                  "the closure is the body, and its contents are their own effects"
          }

          test "denial happens before the step is emitted" {
              let o = run "sh 'ok'\nnew File('/etc/passwd')\nsh 'never'\n"
              Expect.equal (stepNames o) [ "sh" ] "the second sh is never reached"
              Expect.isSome o.Fault "faulted"
          }

          test "test-owned escape inventory matches production and both direct gates deny every name" {
              let expectedEscapeNames =
                  set
                      [ "File"
                        "FileInputStream"
                        "FileOutputStream"
                        "RandomAccessFile"
                        "ProcessBuilder"
                        "Runtime"
                        "System"
                        "Class"
                        "ClassLoader"
                        "GroovyShell"
                        "GroovyClassLoader"
                        "Eval"
                        "evaluate"
                        "URL"
                        "URLConnection"
                        "Socket"
                        "ServerSocket"
                        "HttpURLConnection"
                        "Thread"
                        "Unsafe"
                        "MethodHandles"
                        "getClass"
                        "forName"
                        "newInstance"
                        "getDeclaredMethod"
                        "getDeclaredField"
                        "setAccessible"
                        "invoke"
                        "execute"
                        "exec" ]

              Expect.equal Sandbox.knownEscapes expectedEscapeNames "production escape inventory changed without review"

              for name in expectedEscapeNames do
                  match Sandbox.admitCall Set.empty Set.empty name with
                  | Error d -> Expect.equal d.Attempted name $"free call {name}: exact denied name"
                  | Ok admitted -> failtestf "free call %s unexpectedly admitted as %A" name admitted

                  match Sandbox.admitMethod name with
                  | Error d -> Expect.equal d.Attempted name $"method {name}: exact denied name"
                  | Ok admitted -> failtestf "method %s unexpectedly admitted as %A" name admitted
          }

          test "independent escape matrix is denied before any successor effect" {
              // Test-owned inputs are deliberate. Deriving this matrix from
              // Sandbox.knownEscapes made deletion from the production set delete the
              // corresponding test vector too, and exercised only the free-call gate.
              let escapeCases =
                  [ "constructor", "new File('/etc/passwd')\n", "new File"
                    "free call", "evaluate('1 + 1')\n", "evaluate"
                    "method", "'ls'.execute()\n", "execute"
                    "safe null", "def target = null\ntarget?.execute()\n", "execute"
                    "safe value", "'ls'?.execute()\n", "execute" ]

              for family, source, attempted in escapeCases do
                  let o = run (source + "sh 'successor'\n")

                  match o.Fault with
                  | Some(Denied d) -> Expect.equal d.Attempted attempted $"{family}: exact denied call"
                  | other -> failtestf "%s: expected typed Denied, got %A" family other

                  Expect.isEmpty o.Effects $"{family}: no successor effect after denial"
          }

          test "admitted null-safe builtin keeps arguments lazy and execution continues" {
              let o = run "def target = null\ntarget?.trim(sh('argument'))\nsh 'successor'\n"
              Expect.isNone o.Fault "the admitted builtin short-circuits normally"
              Expect.equal (stepArgs o) [ "sh", [ "successor" ] ] "argument was not evaluated; successor ran"
          }

          test "registered steps, pure builtins and script helpers remain admitted" {
              let registered = run "sh 'registered'\n"
              Expect.isNone registered.Fault "registered step"
              Expect.equal (stepArgs registered) [ "sh", [ "registered" ] ] "registered effect emitted"

              let builtin = run "def value = '  clean  '.trim()\nsh value\n"
              Expect.isNone builtin.Fault "pure builtin"
              Expect.equal (stepArgs builtin) [ "sh", [ "clean" ] ] "builtin result reached the step"

              let helper = run "def helper() { return 'local' }\nhelper()\nsh 'successor'\n"
              Expect.isNone helper.Fault "script helper"
              Expect.equal (stepArgs helper) [ "sh", [ "successor" ] ] "helper returned without blocking later work"
          }

          test "Value carrier schema is exact and transitively free of unreviewed CLR carriers" {
              let audit = CarrierSchema.audit typeof<Value>

              let expectedManifest =
                  [ "Fogell.Groovy.Interpreter.Value :: Fogell.Groovy.Interpreter.Value = union VNull | VBool(Item:System.Boolean) | VInt(Item:System.Int64) | VInteger(Item:System.Int64) | VArithmeticInteger(Item:System.Int64) | VFloat(Item:System.Single) | VStr(Item:System.String) | VList(Item:ref<list<Fogell.Groovy.Interpreter.Value>>) | VRange(Item:list<System.Int64>) | VMap(Item:ref<map<System.String, Fogell.Groovy.Interpreter.Value>>) | VScmMap(Item:Fogell.Groovy.Interpreter.ScmMap) | VScmKeySet(Item:list<System.String>) | VJUnitSummary(Item:ref<Fogell.Groovy.Interpreter.JUnitSummary>) | VClosure(Item1:Fogell.Groovy.Closure, Item2:Fogell.Groovy.Interpreter.Env) | VFunc(name:System.String, parameters:list<System.String>, body:list<Fogell.Groovy.Stmt>)"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item1 :: Fogell.Groovy.Closure = record { Params:list<System.String>; Body:list<Fogell.Groovy.Stmt> }"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item1.Body.list :: Fogell.Groovy.Stmt = union SExpr(Item:Fogell.Groovy.Expr) | SDef(name:System.String, value:option<Fogell.Groovy.Expr>) | SAssign(target:Fogell.Groovy.Expr, value:Fogell.Groovy.Expr) | SIndexCompoundAssign(target:Fogell.Groovy.Expr, op:System.String, value:Fogell.Groovy.Expr) | SIndexPostfixAssign(target:Fogell.Groovy.Expr, op:System.String) | SIf(cond:Fogell.Groovy.Expr, thenBranch:list<Fogell.Groovy.Stmt>, elseBranch:list<Fogell.Groovy.Stmt>) | SForIn(var:System.String, source:Fogell.Groovy.Expr, body:list<Fogell.Groovy.Stmt>) | SWhile(cond:Fogell.Groovy.Expr, body:list<Fogell.Groovy.Stmt>) | SSwitch(subject:Fogell.Groovy.Expr, arms:list<tuple<option<Fogell.Groovy.Expr> * list<Fogell.Groovy.Stmt>>>) | SReturn(Item:option<Fogell.Groovy.Expr>) | SBreak | SContinue | SThrow(Item:Fogell.Groovy.Expr) | STry(body:list<Fogell.Groovy.Stmt>, catch:option<tuple<option<System.String> * option<System.String> * list<Fogell.Groovy.Stmt>>>, finallyBlock:list<Fogell.Groovy.Stmt>) | SFunc(name:System.String, parameters:list<System.String>, body:list<Fogell.Groovy.Stmt>)"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item1.Body.list.SExpr.Item :: Fogell.Groovy.Expr = union ENull | EBool(Item:System.Boolean) | EInt(Item:System.Int64) | EStr(Item:System.String) | EGString(Item:list<Fogell.Groovy.GStringPart>) | EList(Item:list<Fogell.Groovy.Expr>) | EMap(Item:list<tuple<System.String * Fogell.Groovy.Expr>>) | EVar(Item:System.String) | EProp(target:Fogell.Groovy.Expr, name:System.String) | ESpreadProp(target:Fogell.Groovy.Expr, name:System.String) | ESafeProp(target:Fogell.Groovy.Expr, name:System.String) | EIndex(target:Fogell.Groovy.Expr, index:Fogell.Groovy.Expr) | EUnary(op:System.String, operand:Fogell.Groovy.Expr) | EBinary(op:System.String, left:Fogell.Groovy.Expr, right:Fogell.Groovy.Expr) | ETernary(cond:Fogell.Groovy.Expr, ifTrue:Fogell.Groovy.Expr, ifFalse:Fogell.Groovy.Expr) | EElvis(Item1:Fogell.Groovy.Expr, Item2:Fogell.Groovy.Expr) | ECall(target:Fogell.Groovy.CallTarget, args:list<Fogell.Groovy.Arg>, trailing:option<Fogell.Groovy.Closure>) | EClosure(Item:Fogell.Groovy.Closure)"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item1.Body.list.SExpr.Item.ECall.args.list :: Fogell.Groovy.Arg = union APos(Item:Fogell.Groovy.Expr) | ANamed(name:System.String, value:Fogell.Groovy.Expr)"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item1.Body.list.SExpr.Item.ECall.target :: Fogell.Groovy.CallTarget = union FreeCall(name:System.String) | MethodCall(target:Fogell.Groovy.Expr, name:System.String) | SafeMethodCall(target:Fogell.Groovy.Expr, name:System.String)"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item1.Body.list.SExpr.Item.EGString.Item.list :: Fogell.Groovy.GStringPart = union GLit(Item:System.String) | GExpr(Item:Fogell.Groovy.Expr)"
                    "Fogell.Groovy.Interpreter.Value.VClosure.Item2 :: Fogell.Groovy.Interpreter.Env = record { Vars:map<System.String, ref<Fogell.Groovy.Interpreter.Value>>; Funcs:map<System.String, list<tuple<list<System.String> * list<Fogell.Groovy.Stmt>>>> }"
                    "Fogell.Groovy.Interpreter.Value.VJUnitSummary.Item.ref :: Fogell.Groovy.Interpreter.JUnitSummary = record { TotalCount:System.Int64; FailCount:System.Int64; SkipCount:System.Int64; PassCount:System.Int64; Duration:option<System.Single> }"
                    "Fogell.Groovy.Interpreter.Value.VScmMap.Item :: Fogell.Groovy.Interpreter.ScmMap = record { Entries:map<System.String, System.String> }" ]

              Expect.isEmpty audit.Findings "the reviewed Value graph has no unreviewed carrier type"
              Expect.equal audit.Manifest expectedManifest "the complete transitive Value schema is reviewed exactly"
          }

          test "carrier audit rejects nested host objects without changing the outer shape" {
              let objectAudit = CarrierSchema.audit typeof<CarrierFixtures.PreservedCarrierShape<obj>>

              let fileInfoAudit =
                  CarrierSchema.audit typeof<CarrierFixtures.PreservedCarrierShape<System.IO.FileInfo>>

              Expect.equal objectAudit.Findings.Length 1 "nested System.Object is rejected"
              Expect.equal objectAudit.Findings.Head.TypeName "System.Object" "object leaf is named"

              Expect.equal
                  objectAudit.Findings.Head.Path
                  "Fogell.Groovy.Tests+CarrierFixtures+PreservedCarrierShape<System.Object>.VScmMap.Item.Metadata.map-value.HostObject"
                  "object path is complete"

              Expect.equal fileInfoAudit.Findings.Length 1 "nested FileInfo is rejected"
              Expect.equal fileInfoAudit.Findings.Head.TypeName "System.IO.FileInfo" "host leaf is named"

              Expect.equal
                  fileInfoAudit.Findings.Head.Path
                  "Fogell.Groovy.Tests+CarrierFixtures+PreservedCarrierShape<System.IO.FileInfo>.VScmMap.Item.Metadata.map-value.HostObject"
                  "host path is complete"
          } ]

/// Budgets: the interpreter runs on the admission path, so a runaway script is
/// a denial of service on the controller.
let budgets =
    testList
        "FG-013 evaluation budgets"
        [ test "an infinite while loop is stopped" {
              let o = run "def i = 0\nwhile (true) { i = i + 1 }\n"

              match o.Fault with
              | Some(BudgetExhausted _) -> ()
              | other -> failtestf "expected BudgetExhausted, got %A" other
          }

          test "an enormous range is refused rather than materialised" {
              let o = run "def r = (1..100000000)\n"

              match o.Fault with
              | Some(BudgetExhausted _) -> ()
              | other -> failtestf "expected BudgetExhausted, got %A" other
          }

          test "unbounded recursion is stopped by call depth" {
              let o = run "def f(n) { return f(n + 1) }\nf(0)\n"

              match o.Fault with
              | Some(BudgetExhausted _) -> ()
              | other -> failtestf "expected BudgetExhausted, got %A" other
          }

          test "a catastrophic regex does not hang" {
              let sw = System.Diagnostics.Stopwatch.StartNew()
              run "def s = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!'\ndef m = (s =~ /(a+)+$/)\n" |> ignore
              sw.Stop()
              Expect.isLessThan sw.ElapsedMilliseconds 5000L "regex has a timeout"
          } ]

let semantics =
    testList
        "interpreter semantics"
        [ test "GString interpolation resolves variables" {
              let o = run "def name = 'world'\nsh \"echo ${name}\"\n"
              Expect.equal (stepArgs o) [ "sh", [ "echo world" ] ] "interpolated"
          }
          test "a user function is called and its return used" {
              let o = run "def target() { return 'prod' }\nsh \"deploy ${target()}\"\n"
              Expect.equal (stepArgs o) [ "sh", [ "deploy prod" ] ] "function result interpolated"
          }
          test "if/else selects the right branch" {
              let o = run "if (1 > 2) { sh 'no' } else { sh 'yes' }\n"
              Expect.equal (stepArgs o) [ "sh", [ "yes" ] ] "else taken"
          }
          test "for-in iterates a list" {
              let o = run "for (x in ['a','b']) { echo x }\n"
              Expect.equal (stepArgs o) [ "echo", [ "a" ]; "echo", [ "b" ] ] "both iterations"
          }
          test "each with a closure iterates" {
              let o = run "['a','b'].each { echo it }\n"
              Expect.equal (stepArgs o) [ "echo", [ "a" ]; "echo", [ "b" ] ] "closure applied"
          }
          test "try/catch recovers and continues" {
              let o = run "try { throw 'boom' } catch (e) { echo 'caught' }\nsh 'after'\n"
              Expect.equal (stepNames o) [ "echo"; "sh" ] "handler ran, execution continued"
              Expect.isNone o.Fault "recovered"
          }
          test "short-circuit avoids evaluating the right side" {
              let o = run "if (false && (1/0 == 0)) { sh 'no' } else { sh 'yes' }\n"
              Expect.isNone o.Fault "division by zero never evaluated"
              Expect.equal (stepArgs o) [ "sh", [ "yes" ] ] "else branch"
          }
          test "division by zero faults rather than crashing the host" {
              match (run "def x = 1 / 0\n").Fault with
              | Some(Thrown _) -> ()
              | other -> failtestf "expected Thrown, got %A" other
          }
          test "named step arguments are preserved" {
              let o = run "archiveArtifacts artifacts: '*.jar'\n"

              match o.Effects with
              | [ StepCall("archiveArtifacts", _, named) ] ->
                  Expect.equal (named |> List.map fst) [ "artifacts" ] "named arg kept"
              | other -> failtestf "unexpected effects: %A" other
          } ]


let predicateValues =
    testList
        "FG-048 a `when` predicate's VALUE"
        [ test "a bare trailing expression is the value" {
              Expect.equal (run "1 == 1").Returned (Some(VBool true)) "single expression"
          }

          test "a multi-statement closure returns its LAST expression" {
              // REVIEW FIX (Codex, PR #13): only a script that was exactly one
              // expression produced a value, so `def deploy = true; deploy` gave
              // None — which a `when` reads as unevaluable and FAILS THE BUILD on,
              // where Jenkins simply runs the stage. Groovy returns the last
              // expression of a closure regardless of what precedes it.
              Expect.equal (run "def deploy = true\ndeploy").Returned (Some(VBool true)) "last expression wins"
          }

          test "an explicit return still wins" {
              Expect.equal (run "return false").Returned (Some(VBool false)) "explicit return"
          }

          test "an assignment IS a value, as in Groovy" {
              // This test previously asserted `def x = 1` produced NO value. That was
              // wrong about Groovy — a declaration with an initialiser evaluates to the
              // assigned value — and a Codex review on PR #13 caught the code matching
              // the wrong assertion. `expression { def deploy = true; deploy = false }`
              // must read as false, not as unevaluable.
              Expect.equal (run "def x = 1").Returned (Some(VInt 1L)) "declaration yields its value"
              Expect.equal (run "def d = true\nd = false").Returned (Some(VBool false)) "reassignment yields its value"
          }

          test "a predicate that truly produces nothing stays None" {
              // None must remain distinguishable from false: nothing ran, so there is
              // no value — which is unevaluable, not negative.
              Expect.equal (run "if (false) { 1 }").Returned None "untaken branch yields nothing"
          }

          test "an untaken trailing conditional yields nothing, not an earlier value" {
              // REVIEW FIX (Codex, PR #14 round 3): `true` set the trailing value and
              // the untaken `if` left it there, so this read as TRUE. The trailing
              // value belongs to the FINAL statement, not to whatever ran last.
              Expect.equal (run "true\nif (false) { false }").Returned None "untaken if clears it"
              Expect.equal (run "true\nif (true) { false }").Returned (Some(VBool false)) "taken branch supplies it"
              Expect.equal (run "true\nwhile (false) { 1 }").Returned None "loop that never runs clears it"
          }

          test "an uninitialised trailing declaration yields null, not the previous value" {
              // Round-6 finding: value tracking covered only INITIALISED declarations,
              // so `true; def x` left `true` in place and `[1].any { … }` was true.
              Expect.equal (run "true\ndef x").Returned (Some VNull) "uninitialised def is null"
          }

          test "an assignment through a property or index target yields its RHS" {
              // Round-7 finding: only the bare-variable form recorded a value, so
              // a supported map-property target as the FINAL statement left
              // LastValue absent or stale — reported unevaluable, or reusing an earlier
              // truthy value. Groovy assignments yield their RHS whatever the target is.
              Expect.equal
                  (run "def values = [:]\ntrue\nvalues.DEPLOY = false").Returned
                  (Some(VBool false))
                  "a write that actually occurred yields its RHS"
          }

          test "a closure returns its trailing expression without `return`" {
              // `[1].any { it == 1 }` was FALSE because applyClosure discarded the
              // block's value unless an explicit `return` appeared.
              Expect.equal (run "[1].any { it == 1 }").Returned (Some(VBool true)) "implicit closure return"
          } ]

let stepValueUse =
    // FG-160/FG-174. A step call in a VALUE position is refused unless the step can
    // actually supply a value.
    //
    // The original reason was the batch model: it collected `StepCall` effects and every
    // call evaluated to `VNull`, so a body using a return value decided branches on null.
    // FG-172 replaced that with a live host and FG-174 taught `sh` to answer
    // `returnStdout`/`returnStatus`; FG-177 then separated a measured genuine null and
    // wrapper body result from unsupported object/map results. The exemplars use `node()` for the latter,
    // while plain sh/echo/archive calls are admitted by the stub contract below.
    let uses src =
        // A STUB of the real contract, deliberately. This assembly must not depend on
        // `Fogell.Differential` — keeping the step vocabulary out of the interpreter layer
        // is why `find` takes a predicate at all. These tests assert that `find` CONSULTS
        // the rule and walks every value position; the rule ITSELF (which steps, and
        // `returnStatus` winning when both are set) is `WalkerRules.returnContract` and is
        // tested against that function in Fogell.Differential.Tests.
        Fogell.Groovy.Interpreter.StepValueUse.find
            steps.Contains
            (fun n so st ->
                if n = "sh" || n = "bat" then
                    so <> Fogell.Groovy.Interpreter.StepValueUse.Undecidable
                    && st <> Fogell.Groovy.Interpreter.StepValueUse.Undecidable
                else
                    Set.contains n (set [ "echo"; "archiveArtifacts" ]))
            (parseOk src)

    let usedSteps src = uses src |> List.map (fun u -> u.Step)

    testList
        "FG-160 step calls whose value is used"
        [ test "a bare step statement is NOT a value use" {
              // The whole point: the common shape must still run. If this fires, the
              // refusal rejects the corpus it was meant to admit.
              Expect.isEmpty (uses "sh 'make'\necho 'done'") "a discarded call is safe"
          }

          test "a step in a variable initialiser IS a value use" {
              Expect.equal (usedSteps "def out = node()") [ "node" ] "def RHS"
          }

          test "a step in an if condition IS a value use" {
              Expect.equal (usedSteps "if (node() == null) { echo 'ok' }") [ "node" ] "condition"
          }

          test "a step in an ASSIGNMENT is a value use" {
              Expect.equal (usedSteps "x = node()") [ "node" ] "assignment RHS"
          }

          test "a step nested in a string interpolation is a value use" {
              // The sneakiest shape, and the one a naive statement-level check misses.
              Expect.equal (usedSteps "echo \"got ${node()}\"") [ "node" ] "interpolation"
          }

          test "a step as an ARGUMENT to a bare statement call is a value use" {
              // The outer call is a discarded statement; the inner one is not. A check
              // that only asked "is this statement a call" would pass this.
              Expect.equal (usedSteps "echo node()") [ "node" ] "argument"
          }

          test "a step inside a TRAILING BLOCK is not a VALUE use" {
              // `timeout(5) { sh 'x' }` discards the inner value exactly as a statement
              // does — so this finder, which answers only the VALUE question, says
              // nothing about it.
              //
              // IT IS NOT THEREFORE RUNNABLE. This comment claimed a trailing block was a
              // "perfectly runnable shape", and the pre-push verifier showed it is not:
              // the interpreter runs the closure immediately and flattens it, so a
              // replayed wrapper arrives with no body and `dir('x') { sh 'pwd' }` runs in
              // the wrong directory. `findWrapperCalls` refuses it. One sentence cannot
              // answer both questions, and the old one tried.
              Expect.isEmpty (uses "node { sh 'make' }") "a trailing block holds statements, not a value"
          }

          test "a WRAPPER call is refused, because replay cannot carry its body" {
              let wrappers src =
                  Fogell.Groovy.Interpreter.StepValueUse.findWrapperCalls steps.Contains (parseOk src)
                  |> List.map (fun u -> u.Step)

              Expect.equal (wrappers "node { sh 'make' }") [ "node" ] "a step with a trailing block"
          }

          test "a wrapper NESTED in control flow is still found" {
              let wrappers src =
                  Fogell.Groovy.Interpreter.StepValueUse.findWrapperCalls steps.Contains (parseOk src)
                  |> List.map (fun u -> u.Step)

              Expect.equal (wrappers "if (true) { node { sh 'make' } }") [ "node" ] "the walk reaches into branches"
          }

          test "a bare step with NO trailing block is not a wrapper" {
              // The refusal must not swallow the shape FG-160 exists to run.
              let wrappers src =
                  Fogell.Groovy.Interpreter.StepValueUse.findWrapperCalls steps.Contains (parseOk src)

              Expect.isEmpty (wrappers "sh 'make'\necho 'done'") "plain steps stay runnable"
          }

          test "a step in a RETURN is a value use" {
              Expect.equal (usedSteps "return node()") [ "node" ] "return value"
          }

          test "a step in a for-in SOURCE is a value use" {
              Expect.equal
                  (usedSteps "for (f in node()) { echo f }")
                  [ "node" ]
                  "loop source"
          }

          test "a NON-step call is never flagged" {
              // `isStep` decides, not call shape. A user function or builtin returning a
              // value is ordinary Groovy and must not be refused.
              Expect.isEmpty (uses "def x = someHelper()") "only registered steps batch their effects"
          }

          test "the position is NAMED, not just the step" {
              // A refusal that says only "a step value is used" sends someone hunting.
              let u = uses "def out = node()" |> List.exactlyOne
              Expect.equal u.Where "a variable initialiser" "the position is reported"
          }

          test "several uses are all reported, in source order" {
              let src = "def a = node()\nif (node() == null) { echo a }"
              Expect.equal (usedSteps src) [ "node"; "node" ] "every use, not the first"
          }

          // FG-174/FG-177. Typed shell answers and genuine null are both modelled.
          test "returnStdout: true is ADMITTED in a value position" {
              Expect.isEmpty (uses "def out = sh(script: 'x', returnStdout: true)") "the step supplies a value"
          }

          test "returnStatus: true is ADMITTED in a value position" {
              Expect.isEmpty (uses "if (sh(script: 'x', returnStatus: true) == 0) { echo 'ok' }") "the step supplies a value"
          }

          // THE FLAG MUST BE A LITERAL `true`, and these three are why the check reads the
          // argument LIST rather than looking for a key.
          test "plain and literal-false sh calls return genuine null" {
              Expect.isEmpty (uses "def plain = sh(script: 'x')") "plain sh publishes null"
              Expect.isEmpty
                  (uses "def off = sh(script: 'x', returnStdout: false, returnStatus: false)")
                  "literal false selects null"
          }

          test "a NON-LITERAL flag is still refused" {
              // A static reader cannot know what `flag` holds, so assuming it is true
              // would be guessing in the direction that fails open.
              Expect.equal (usedSteps "def out = sh(script: 'x', returnStdout: flag)") [ "sh" ] "unknown at read time"
          }

          test "the flag must be NAMED, not positional" {
              // `sh('x', true)` says nothing about WHICH option is being set.
              // Its return shape is still the plain-null contract; the shared runtime
              // validator refuses the malformed two-positional call before execution.
              Expect.isEmpty (uses "def out = sh('x', true)") "a bare true selects no typed flag"
          }

          test "the flags belong to the SHELL STEPS, not to every step" {
              // The verifier ran this one: Fogell handed the script "hello\n" where
              // Jenkins' `echo` returns null and only warns about the unknown parameter,
              // so `got == null` took the other branch and skipped work Jenkins runs —
              // while the build reported success. Echo's descriptor now answers genuine
              // null regardless of this unknown key; the runtime validator still throws
              // the measured constructor-map binding fault before publishing that value.
              Expect.isEmpty
                  (uses "def got = echo(message: 'hello', returnStdout: true)")
                  "the flag does not turn echo into a stdout producer"
          }

          test "unsupported object and map results stay refused" {
              Expect.equal (usedSteps "def got = node()") [ "node" ] "an unmodelled producer remains blocked"
          }
        ]

let hostedSteps =
    // FG-160 slice 2. The callback boundary, holding the four things the BATCH model
    // could not express — each one a refusal in slice 1, each found by review:
    //   a step's RETURN VALUE, a wrapper's BODY, `env` MUTATION, and per-step ordering
    //   the host can hang durability on.
    // These test the seam, not the walker; the walker side is separate work.
    let hostThat perform setEnv =
        { Perform = fun _ name positional named runBody -> perform name positional named runBody
          CanContinue = fun () -> true
          SetEnv = setEnv
          // FG-178. These tests exercise the SEAM, not a walker environment; an empty
          // list keeps the body's bindings exactly as `runHosted` set them.
          CurrentEnv = fun () -> []
          // FG-184. The seam's own answer, spelled out here rather than imported from
          // `WalkerRules`: these tests drive `dir`/`retry`/`withEnv` bodies directly, and
          // borrowing the production set would make them pass because of what the walker
          // happens to register rather than because the normalisation asked at all.
          TakesBlock = fun name -> Set.contains name (set [ "dir"; "timeout"; "retry"; "withEnv" ]) }

    /// Records what the host was asked to do, in order.
    let recording () =
        let log = ResizeArray<string>()

        let host =
            hostThat
                (fun name positional _named runBody ->
                    let args = positional |> List.map Value.toDisplay |> String.concat ","
                    log.Add $"{name}({args})"

                    match runBody with
                    | Some run ->
                        log.Add $"{name}:body-start"
                        run ()
                        log.Add $"{name}:body-end"
                    | None -> ()

                    VNull)
                (fun k v -> log.Add $"env:{k}={v}")

        host, log

    let runIt host src =
        let hostedVocabulary = Set.union steps (set [ "dir"; "timeout"; "retry"; "withEnv" ])
        Interpreter.runHosted host Budget.defaults hostedVocabulary (Env.empty) (parseOk src)

    testList
        "FG-160 slice 2 the host performs steps live"
        [ test "steps are performed IN ORDER, as they are reached" {
              let host, log = recording ()
              let outcome = runIt host "sh 'a'\nsh 'b'"

              Expect.isNone outcome.Fault "the script ran"
              Expect.equal (List.ofSeq log) [ "sh(a)"; "sh(b)" ] "source order, live"
          }

          test "Effects is EMPTY in hosted mode, because the steps already happened" {
              // A caller reading Effects here has asked for the wrong model; an empty list
              // makes that visible instead of handing back a replayable-looking log.
              let host, _ = recording ()
              let outcome = runIt host "sh 'a'"
              Expect.isEmpty outcome.Effects "hosted mode performs, it does not collect"
          }

          test "a step's RETURN VALUE reaches the script" {
              // The defect that started the refusals: batch mode evaluated every call to
              // null, so `def out = sh(...)` bound null and every branch on it was wrong.
              let host =
                  hostThat (fun _ _ _ _ -> VStr "deadbeef") (fun _ _ -> ())

              let outcome =
                  Interpreter.runHosted host Budget.defaults steps Env.empty
                      (parseOk "def out = sh(script: 'git rev-parse HEAD', returnStdout: true)\nreturn out")

              Expect.isNone outcome.Fault "no fault"
              Expect.equal outcome.Returned (Some(VStr "deadbeef")) "the host's value is the call's value"
          }

          test "a WRAPPER's body runs INSIDE the wrapper, not flattened before it" {
              // Batch mode evaluated a trailing closure immediately and flattened its
              // effects, so `dir('x') { sh 'pwd' }` ran `sh` outside `x`. The body arrives
              // as a thunk the host invokes where it chooses.
              let host, log = recording ()
              let outcome = runIt host "node('linux') { sh 'pwd' }"

              Expect.isNone outcome.Fault "no fault"

              Expect.equal
                  (List.ofSeq log)
                  [ "node(linux)"; "node:body-start"; "sh(pwd)"; "node:body-end" ]
                  "the inner step runs between the wrapper's setup and teardown"
          }

          test "a wrapper returns its body's typed implicit or explicit value" {
              let host, _ = recording ()

              let explicit = runIt host "return dir('sub') { return 7 }"
              Expect.isNone explicit.Fault "explicit body return ran"
              Expect.equal explicit.Returned (Some(VInt 7L)) "the Integer stayed typed"

              let implicit = runIt host "return withEnv(['A=1']) { ['value', 2] }"
              Expect.isNone implicit.Fault "implicit body return ran"

              match implicit.Returned with
              | Some(VList values) ->
                  Expect.equal values.Value [ VStr "value"; VInt 2L ] "the list stayed typed"
              | other -> failtestf "expected a typed list body result, got %A" other
          }

          test "the body-result cell keeps the final host invocation" {
              let host =
                  hostThat
                      (fun name _ _ runBody ->
                          match name, runBody with
                          | "retry", Some run ->
                              run ()
                              run ()
                          | _, Some run -> run ()
                          | _ -> ()

                          VNull)
                      (fun _ _ -> ())

              let outcome = runIt host "def n = 0\nreturn retry(2) { n = n + 1\n n }"
              Expect.isNone outcome.Fault "the seam permitted both host-selected invocations"
              Expect.equal outcome.Returned (Some(VInt 2L)) "the final host invocation supplies the result"
          }

          test "an absorbed later fault cannot recycle an earlier body result" {
              let host =
                  hostThat
                      (fun name _ _ runBody ->
                          match name, runBody with
                          | "retry", Some run ->
                              run ()

                              try
                                  run ()
                              with _ ->
                                  ()
                          | _, Some run -> run ()
                          | _ -> ()

                          VNull)
                      (fun _ _ -> ())

              let outcome = runIt host "def n = 0\nreturn retry(2) { n = n + 1\n if (n == 2) { 1 / 0 }\n n }"

              match outcome.Fault with
              | Some(HostedCallRefused why) ->
                  Expect.stringContains why "no return value exists" "the stale first result was cleared"
              | other -> failtestf "expected a hosted-call refusal, got %A" other
          }

          test "a wrapper that does not invoke its body cannot invent null" {
              let host = hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ())
              let outcome = runIt host "return dir('sub') { 7 }"

              match outcome.Fault with
              | Some(HostedCallRefused why) ->
                  Expect.stringContains why "without executing its body" "the refusal names the missing result"
              | other -> failtestf "expected a hosted-call refusal, got %A" other
          }

          test "a wrapper body is NOT run when the host declines to run it" {
              // `retry`, `timeout` and a skipped `when` all need this: the host decides
              // whether and how many times the body runs. Batch mode had already run it.
              let host = hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ())
              let ran = ref false

              let counting =
                  { host with
                      Perform =
                        fun _ name _ _ _ ->
                            if name = "sh" then ran.Value <- true
                            VNull }

              let outcome =
                  Interpreter.runHosted counting Budget.defaults steps Env.empty (parseOk "node('linux') { sh 'pwd' }")

              Expect.isNone outcome.Fault "no fault"
              Expect.isFalse ran.Value "an unrun body performs no steps"
          }

          test "a host FAULT stops the script where it happened" {
              // A failing step must not let the rest of the block run — batch mode could
              // not fail mid-script at all, because nothing had run yet.
              let host =
                  hostThat
                      (fun name _ _ _ ->
                          if name = "sh" then failwith "step failed"
                          VNull)
                      (fun _ _ -> ())

              Expect.throws
                  (fun () -> Interpreter.runHosted host Budget.defaults steps Env.empty (parseOk "sh 'a'\nsh 'b'") |> ignore)
                  "a host exception propagates rather than being swallowed"
          }

          test "a hosted halt unwinds before later arguments or Perform" {
              let performed = ref false

              let host =
                  { hostThat (fun _ _ _ _ -> performed.Value <- true; VNull) (fun _ _ -> ()) with
                      CanContinue = fun () -> false }

              let missing =
                  runIt host "sh(script: MISSING)\nreturn 'SURVIVED'"

              Expect.isNone missing.Fault "the unreachable missing property was never forced"
              Expect.isNone missing.Returned "the halted script unwinds without a replacement return"
              Expect.isFalse performed.Value "Perform is not entered after reachability says false"

              let sideEffect =
                  runIt host "def arg() { sh 'nested'; return 'outer' }\nsh(script: arg())\nreturn 'DONE'"

              Expect.isNone sideEffect.Fault "the unreachable helper argument was never invoked"
              Expect.isNone sideEffect.Returned "the halted script does not resume after the call"
              Expect.isFalse performed.Value "neither the nested nor outer step was performed"
          }

          test "finally cleanup calls run in the narrow hosted-halt unwind phase" {
              let reachable = ref true
              let performed = ResizeArray<HostedCallPhase * string * string list>()

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun phase name positional _ _ ->
                            performed.Add(phase, name, positional |> List.map Value.toDisplay)

                            if name = "stage" then
                                reachable.Value <- false

                            VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  runIt host (
                      "def increment(value) { return value + 1 }\n"
                      + "def n = 0\n"
                      + "try { stage 'halt' } finally { n = increment(n); echo \"cleanup:${n}\" }\n"
                      + "echo 'must-not-run'\n"
                  )

              Expect.isNone outcome.Fault "the original hosted halt remains control flow, not a replacement fault"
              Expect.isNone outcome.Returned "normal finally completion preserves the original halt"

              Expect.equal
                  (List.ofSeq performed)
                  [ OrdinaryCall, "stage", [ "halt" ]
                    FinallyUnwind, "echo", [ "cleanup:1" ] ]
                  "the helper and hosted cleanup execute, but ordinary continuation stays suppressed"
          }

          test "nested finally blocks unwind inner then outer under a hosted halt" {
              let reachable = ref true
              let performed = ResizeArray<HostedCallPhase * string>()

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun phase name _ _ _ ->
                            performed.Add(phase, name)

                            if name = "stage" then
                                reachable.Value <- false

                            VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  runIt host (
                      "try { try { stage 'halt' } finally { echo 'inner' } } "
                      + "finally { echo 'outer' }\n"
                  )

              Expect.isNone outcome.Fault "the original halt escapes after both cleanup blocks"

              Expect.equal
                  (List.ofSeq performed)
                  [ OrdinaryCall, "stage"
                    FinallyUnwind, "echo"
                    FinallyUnwind, "echo" ]
                  "nested cleanup order is inner then outer and both calls are explicitly phased"
          }

          test "a finally helper still shadows the hosted step with the same name" {
              let reachable = ref true
              let performed = ResizeArray<HostedCallPhase * string * string list>()

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun phase name positional _ _ ->
                            performed.Add(phase, name, positional |> List.map Value.toDisplay)

                            if name = "stage" then
                                reachable.Value <- false

                            VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  runIt host (
                      "def echo(value) { library \"helper:${value}\"; return value }\n"
                      + "try { stage 'halt' } finally { echo('cleanup') }\n"
                  )

              Expect.isNone outcome.Fault "the original hosted halt survives helper cleanup"

              Expect.equal
                  (List.ofSeq performed)
                  [ OrdinaryCall, "stage", [ "halt" ]
                    FinallyUnwind, "library", [ "helper:cleanup" ] ]
                  "helper resolution wins over the registered echo step during unwind"
          }

          test "a finally return replaces a hosted halt" {
              let reachable = ref true

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun _ name _ _ _ ->
                            if name = "stage" then
                                reachable.Value <- false

                            VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome = runIt host "try { stage 'halt' } finally { return 7 }"

              Expect.isNone outcome.Fault "the finally return suppresses the hosted halt"
              Expect.equal outcome.Returned (Some(VInt 7L)) "the replacing return value survives"
          }

          test "a new cleanup refusal stops successors but still runs its nested finally" {
              let reachable = ref true
              let performed = ResizeArray<HostedCallPhase * string>()

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun phase name positional _ _ ->
                            performed.Add(phase, name)

                            match name, positional with
                            | "stage", _ ->
                                reachable.Value <- false
                                VNull
                            | "sh", [ VStr "refuse" ] -> Interpreter.raiseHostedCallRefused "cleanup refused"
                            | _ -> VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  runIt host (
                      "try { stage 'original-halt' } finally { "
                      + "try { sh 'refuse'; echo 'inner-successor' } "
                      + "finally { echo 'nested-cleanup' }; "
                      + "echo 'outer-successor' }"
                  )

              Expect.equal outcome.Fault (Some(HostedCallRefused "cleanup refused")) "the newer refusal escapes"

              Expect.equal
                  (List.ofSeq performed)
                  [ OrdinaryCall, "stage"
                    FinallyUnwind, "sh"
                    FinallyUnwind, "echo" ]
                  "the nested finally runs under inherited permission; both successors stay suppressed"
          }

          test "a nested finally return replaces both a cleanup refusal and the inherited halt" {
              let reachable = ref true

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun _ name positional _ _ ->
                            match name, positional with
                            | "stage", _ ->
                                reachable.Value <- false
                                VNull
                            | "sh", [ VStr "refuse" ] -> Interpreter.raiseHostedCallRefused "cleanup refused"
                            | _ -> VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  runIt host (
                      "try { stage 'original-halt' } finally { "
                      + "try { sh 'refuse'; return 7 } finally { return 9 } }"
                  )

              Expect.isNone outcome.Fault "the nested finally return replaces both pending halts"
              Expect.equal outcome.Returned (Some(VInt 9L)) "the innermost replacing return wins"
          }

          test "a fresh status-only cleanup halt stops successors and still runs nested finally" {
              let reachable = ref true
              let performed = ResizeArray<HostedCallPhase * string>()

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun phase name _ _ _ ->
                            performed.Add(phase, name)

                            match name with
                            | "stage" ->
                                reachable.Value <- false
                                VNull
                            | "stash" ->
                                Interpreter.raiseHostedStepHalted
                                    "stash"
                                    HostedFailure
                                    "hosted step 'stash' failed during finally cleanup"
                            | _ -> VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  Interpreter.runHosted host Budget.defaults (Set.add "stash" steps) Env.empty (parseOk (
                      "try { stage 'original-halt' } finally { "
                      + "try { stash name: 'cleanup'; echo 'inner-successor' } "
                      + "finally { echo 'nested-cleanup' }; "
                      + "echo 'outer-successor' }"
                  ))

              Expect.equal
                  outcome.Fault
                  (Some(HostedStepHalted(
                      "stash",
                      HostedFailure,
                      "hosted step 'stash' failed during finally cleanup"
                  )))
                  "the fresh cleanup status owns the escaping halt"

              Expect.equal
                  (List.ofSeq performed)
                  [ OrdinaryCall, "stage"
                    FinallyUnwind, "stash"
                    FinallyUnwind, "echo" ]
                  "nested cleanup runs but both successors stay suppressed"
          }

          test "a nested finally return may replace a fresh status-only cleanup halt" {
              let reachable = ref true

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun _ name _ _ _ ->
                            match name with
                            | "stage" ->
                                reachable.Value <- false
                                VNull
                            | "stash" ->
                                Interpreter.raiseHostedStepHalted "stash" HostedFailure "cleanup failed"
                            | _ -> VNull
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  Interpreter.runHosted host Budget.defaults (Set.add "stash" steps) Env.empty (parseOk (
                      "try { stage 'original-halt' } finally { "
                      + "try { stash name: 'cleanup'; return 7 } finally { return 9 } }"
                  ))

              Expect.isNone outcome.Fault "the nested return replaces both pending halts"
              Expect.equal outcome.Returned (Some(VInt 9L)) "the innermost replacing return wins"
          }

          test "a hosted call refusal stays opaque to Groovy catch and explicit at wrapper ownership" {
              let performed = ResizeArray<string>()

              let host =
                  { hostThat (fun _ _ _ _ -> VNull) (fun _ _ -> ()) with
                      Perform =
                        fun _ name positional _ _ ->
                            performed.Add name

                            if name = "sh" && positional = [ VStr "refuse" ] then
                                Interpreter.raiseHostedCallRefused "call refused"

                            VNull }

              let outcome = runIt host "try { sh 'refuse' } catch (Exception e) { echo 'caught' }; echo 'after'"
              Expect.equal outcome.Fault (Some(HostedCallRefused "call refused")) "Groovy catch cannot absorb a model refusal"
              Expect.equal (List.ofSeq performed) [ "sh" ] "catch and successor effects do not run"

              let caught =
                  Interpreter.catchHostedHalt (fun () -> Interpreter.raiseHostedCallRefused "wrapper reason")

              Expect.equal
                  caught
                  (Some(Interpreter.CallRefusalHalt "wrapper reason"))
                  "the wrapper receives the precise deferred reason"

              let statusCaught =
                  Interpreter.catchHostedHalt (fun () ->
                      Interpreter.raiseHostedStepHalted "stash" HostedAborted "cleanup aborted")

              Expect.equal
                  statusCaught
                  (Some(Interpreter.StepStatusHalt("stash", HostedAborted, "cleanup aborted")))
                  "wrapper ownership preserves status kind and diagnostic"
          }

          test "a nested argument that halts unwinds before later arguments and the outer step" {
              let reachable = ref true
              let performed = ResizeArray<string>()

              let host =
                  { hostThat
                        (fun name positional _ _ ->
                            performed.Add name

                            if name = "sh" && positional = [ VStr "halt" ] then
                                reachable.Value <- false

                            VNull)
                        (fun _ _ -> ()) with
                      CanContinue = fun () -> reachable.Value }

              let outcome =
                  runIt host "echo(sh('halt'), MISSING)\nreturn 'SURVIVED'"

              Expect.isNone outcome.Fault "MISSING after the halting nested call was not forced"
              Expect.isNone outcome.Returned "the interpreter does not resume after the halting nested call"
              Expect.equal (List.ofSeq performed) [ "sh" ] "the outer echo never enters Perform"
          }

          test "script helpers shadow hosted step names while the branch is live" {
              for helper in [ "sh"; "echo"; "node"; "archiveArtifacts" ] do
                  let performed = ResizeArray<string>()

                  let host =
                      hostThat
                          (fun name _ _ _ -> performed.Add name; VNull)
                          (fun _ _ -> ())

                  let outcome =
                      runIt host $"def {helper}(value) {{ return value }}\nreturn {helper}('HELPER')"

                  Expect.isNone outcome.Fault $"{helper}: the helper call succeeds"
                  Expect.equal outcome.Returned (Some(VStr "HELPER")) $"{helper}: helper result wins"
                  Expect.isEmpty performed $"{helper}: hosted Perform is never entered"
          }

          test "a hosted halt never invokes a shadowing helper with missing or side-effect arguments" {
              for helper in [ "sh"; "echo"; "node"; "archiveArtifacts" ] do
                  let reachable = ref true
                  let performed = ResizeArray<string>()

                  let host =
                      { hostThat
                            (fun name _ _ _ ->
                                performed.Add name

                                if name = "stage" then
                                    reachable.Value <- false

                                VNull)
                            (fun _ _ -> ()) with
                          CanContinue = fun () -> reachable.Value }

                  let outcome =
                      runIt host $"def {helper}(value) {{ library 'helper-body'; return value }}\nstage 'halt'\n{helper}(MISSING)\n{helper}(echo('side-effect'))\nreturn 'REPLACED'"

                  Expect.isNone outcome.Fault $"{helper}: the original hosted halt is not replaced"
                  Expect.isNone outcome.Returned $"{helper}: execution stops at the hosted halt"
                  Expect.equal (List.ofSeq performed) [ "stage" ] $"{helper}: helper body and argument effects never run"
          }

          test "a NESTED wrapper still refreshes the Jenkins env binding" {
              // FG-201. The refresh's "ours" check was a per-invocation cell, so a
              // wrapper nested inside another found the OUTER wrapper's cell, failed
              // the identity check, and silently skipped its refresh — `${env.A}`
              // interpolated null under a green build. Receipt
              // `script-nested-wrappers-env` diverged on exactly this shape for four
              // days before a suite run caught it; CI cannot run the differential
              // suite, so this pins the refresh where CI can see it. The host below
              // maintains a real overlay stack for `withEnv`, which is the one thing
              // the recording host's empty CurrentEnv cannot exercise.
              let overlay: (string * string) list ref = ref []
              let log = ResizeArray<string>()

              let host =
                  { Perform =
                      fun _ name positional _named runBody ->
                          (match name, positional with
                           | "withEnv", [ VList entries ] ->
                               let pairs =
                                   entries.Value
                                   |> List.choose (function
                                       | VStr s ->
                                           match s.Split([| '=' |], 2) with
                                           | [| k; v |] -> Some(k, v)
                                           | _ -> None
                                       | _ -> None)

                               let saved = overlay.Value
                               overlay.Value <- pairs @ saved

                               // restore in a FINALLY, as the walker does — a body
                               // fault must leave CurrentEnv reporting the enclosing
                               // overlay, or the FG-202 abnormal-exit read below
                               // would be asserted against a harness bug
                               try
                                   runBody |> Option.iter (fun run -> run ())
                               finally
                                   overlay.Value <- saved
                           | "sh", [ VStr rendered ] -> log.Add rendered
                           | _ -> runBody |> Option.iter (fun run -> run ()))

                          VNull
                    CanContinue = fun () -> true
                    SetEnv = fun _ _ -> ()
                    CurrentEnv = fun () -> overlay.Value
                    TakesBlock = fun name -> Set.contains name (set [ "dir"; "withEnv" ]) }

              let outcome =
                  Interpreter.runHosted host Budget.defaults (set [ "sh"; "dir"; "withEnv" ]) Env.empty
                      (parseOk
                          "dir('sub') {\n  withEnv(['A=one']) { sh \"a:${env.A}\" }\n  sh \"after:${env.A}\"\n  try { withEnv(['B=two']) { def x = 1 / 0 } } catch (e) { }\n  sh \"catch:${env.B}\"\n  withEnv(['TARGET=prod']) { sh \"t:${env.TARGET}\" }\n}")

              Expect.isNone outcome.Fault "the script ran"

              // Both SIBLING wrappers under the outer `dir` must see their own
              // binding: under the per-invocation check both interpolate null.
              // The `catch:` read is the ABNORMAL exit — the faulting body leaves
              // `withEnv` by exception, a script-level catch swallows it, and the
              // read after it must still see the restored environment: the
              // verifier's construction against the first (return-only) spelling
              // of the FG-202 exit refresh.
              // And the read BETWEEN them must see the RESTORED outer environment,
              // not the first wrapper's retained snapshot — FG-202, the shape the
              // verifier constructed against the FG-201 fix (probe
              // `post-exit-env-read`: jenkins after:null, fogell read after:one
              // until the exit re-refresh landed).
              Expect.equal
                  (List.ofSeq log)
                  [ "a:one"; "after:null"; "catch:null"; "t:prod" ]
                  "entry refreshes inside each wrapper; exit restores on return AND on fault"
          }
        ]

let scmMapValues =
    let scmValue =
        VScmMap
            { Entries =
                Map.ofList
                    [ "GIT_URL", "file:///fixture.git"
                      "GIT_COMMIT", "0123456789abcdef"
                      "GIT_BRANCH", "origin/main" ] }

    let runScmScript source =
        let performed = ResizeArray<string>()

        let host =
            { Perform =
                fun _ name _ _ _ ->
                    performed.Add name
                    if name = "source" then scmValue else VNull
              CanContinue = fun () -> true
              SetEnv = fun _ _ -> ()
              CurrentEnv = fun () -> []
              TakesBlock = fun _ -> false }

        let outcome =
            Interpreter.runHosted
                host
                Budget.defaults
                (set [ "source"; "sink" ])
                Env.empty
                (parseOk source)

        outcome, List.ofSeq performed

    let expectOpaqueRefusal label operation =
        let source =
            "def value = source()\n"
            + $"try {{ {operation}; sink('escaped') }} catch (Exception problem) {{ sink('caught') }}\n"

        let outcome, performed = runScmScript source

        match outcome.Fault with
        | Some(Denied _)
        | Some(Unsupported _)
        | Some(HostedCallRefused _) -> ()
        | other -> failtestf "%s: expected a catch-opaque modelling refusal, got %A" label other

        Expect.equal performed [ "source" ] $"{label}: neither successor nor catch handler reached the host"

    testList
        "FG-177 nominal immutable SCM return map"
        [ test "only the measured string lookup and key-set surface is available" {
              let source =
                  "def value = source()\n"
                  + "def dynamicKey = 'GIT_COMMIT'\n"
                  + "return [value.GIT_COMMIT, value['GIT_URL'], value[dynamicKey], "
                  + "value.get('GIT_BRANCH'), value.get('MISSING'), "
                  + "value.containsKey('GIT_COMMIT'), value.containsKey('MISSING'), "
                  + "value.MISSING, value['MISSING'], value.keySet().join(',')]\n"

              let outcome, performed =
                  runScmScript source

              Expect.isNone outcome.Fault "the measured accessors run"
              Expect.equal performed [ "source" ] "lookup is interpreter-local after the producer returns"

              match outcome.Returned with
              | Some(VList values) ->
                  Expect.equal
                      values.Value
                      [ VStr "0123456789abcdef"
                        VStr "file:///fixture.git"
                        VStr "0123456789abcdef"
                        VStr "origin/main"
                        VNull
                        VBool true
                        VBool false
                        VNull
                        VNull
                        VStr "GIT_BRANCH,GIT_COMMIT,GIT_URL" ]
                      "property, string index, get, containsKey, missing lookup and sorted keySet stay exact"
              | other -> failtestf "expected a list of measured SCM-map answers, got %A" other
          }

          test "keySet returns a sorted nominal view" {
              let outcome, performed = runScmScript "return source().keySet()"

              Expect.isNone outcome.Fault "the zero-argument key-set projection runs"
              Expect.equal performed [ "source" ] "the projection stays interpreter-local"
              Expect.equal
                  outcome.Returned
                  (Some(VScmKeySet [ "GIT_BRANCH"; "GIT_COMMIT"; "GIT_URL" ]))
                  "the key set is sorted and never represented as a mutable VList"
          }

          test "unmeasured object operations remain catch-opaque" {
              let operations =
                  [ "rendering", "def ignored = \"${value}\""
                    "nested rendering", "def ignored = \"${[value]}\""
                    "truthiness", "def ignored = value ? 1 : 0"
                    "equality", "def ignored = value == value"
                    "nested equality", "def ignored = [value] == [value]"
                    "ordering", "def ignored = value < value"
                    "property mutation", "value.GIT_COMMIT = 'changed'"
                    "index mutation", "value['GIT_COMMIT'] = 'changed'"
                    "method mutation", "value.put('GIT_COMMIT', 'changed')"
                    "compound mutation", "value['GIT_COMMIT'] += 'changed'"
                    "postfix mutation", "value['GIT_COMMIT']++"
                    "spread", "def ignored = value*.GIT_COMMIT"
                    "nested spread", "def ignored = [value]*.GIT_COMMIT"
                    "wrong index", "def ignored = value[0]"
                    "null index", "def ignored = value[null]"
                    "reflection", "def ignored = value.getClass()"
                    "unsupported method", "def ignored = value.size()"
                    "get non-string", "def ignored = value.get(0)"
                    "get named extra", "def ignored = value.get('GIT_COMMIT', extra: 1)"
                    "get trailing closure", "def ignored = value.get('GIT_COMMIT') { sink('escaped') }"
                    "containsKey non-string", "def ignored = value.containsKey(0)"
                    "containsKey named extra", "def ignored = value.containsKey('GIT_COMMIT', extra: 1)"
                    "containsKey trailing closure", "def ignored = value.containsKey('GIT_COMMIT') { sink('escaped') }"
                    "keySet argument", "def ignored = value.keySet('extra')"
                    "keySet named extra", "def ignored = value.keySet(extra: 1)"
                    "keySet trailing closure", "def ignored = value.keySet() { sink('escaped') }"
                    "keySet append", "def keys = value.keySet(); keys << 'FAKE'"
                    "keySet add", "def keys = value.keySet(); keys.add('FAKE')"
                    "keySet iteration", "def keys = value.keySet(); for (key in keys) { sink('escaped') }"
                    "keySet rendering", "def ignored = \"${value.keySet()}\""
                    "keySet hosted argument", "sink(value.keySet())"
                    "keySet join named extra", "def ignored = value.keySet().join(',', extra: 1)"
                    "keySet join trailing closure", "def ignored = value.keySet().join(',') { sink('escaped') }"
                    "throw", "throw value"
                    "nested throw", "throw [value]"
                    "hosted argument", "sink(value)"
                    "nested hosted argument", "sink([value])" ]

              for label, operation in operations do
                  expectOpaqueRefusal label operation
          }

          test "nested SCM-map containment is identity-aware and stack-safe" {
              let mutable ordinary = VInt 0L
              let mutable nested = scmValue

              for _ in 1..20000 do
                  ordinary <- VList(ref [ ordinary ])
                  nested <- VList(ref [ nested ])

              Expect.isFalse (Value.containsScmMap ordinary) "a deep ordinary value is not misclassified"
              Expect.isTrue (Value.containsScmMap nested) "a deeply wrapped SCM map is still found"

              let cycle = ref []
              let cyclic = VList cycle
              cycle.Value <- [ cyclic; scmValue ]
              Expect.isTrue (Value.containsScmMap cyclic) "a cyclic wrapper terminates and retains the nominal child"
          }

          test "checkout scm token never overrides lexical or binding shadowing" {
              let cases =
                  [ "local", "def scm = 'local-shadow'\ncheckout(scm)", "local-shadow"
                    "helper parameter", "def useScm(scm) { checkout(scm) }\nuseScm('helper-shadow')", "helper-shadow"
                    "script binding", "scm = 'binding-shadow'\ncheckout(scm)", "binding-shadow" ]

              for label, source, expected in cases do
                  let seen = ResizeArray<Value list>()

                  let host =
                      { Perform =
                          fun _ name positional _ _ ->
                              if name = "checkout" then seen.Add positional
                              VNull
                        CanContinue = fun () -> true
                        SetEnv = fun _ _ -> ()
                        CurrentEnv = fun () -> []
                        TakesBlock = fun _ -> false }

                  let outcome =
                      Interpreter.runHosted
                          host
                          Budget.defaults
                          (set [ "checkout" ])
                          Env.empty
                          (parseOk source)

                  Expect.isNone outcome.Fault $"{label}: the shadowing expression evaluates normally"
                  Expect.equal
                      (List.ofSeq seen)
                      [ [ VStr expected ] ]
                      $"{label}: the real shadowing value reaches validation, never the injected scm token"
          }

          test "SCM accessor names reject non-SCM receivers in the lax evaluator" {
              let cases =
                  [ "ordinary map containsKey", "def value = [GIT_COMMIT: 'fake']\nreturn value.containsKey('GIT_COMMIT')"
                    "ordinary map get", "def value = [GIT_COMMIT: 'fake']\nreturn value.get('GIT_COMMIT')"
                    "free containsKey", "return containsKey('GIT_COMMIT')" ]

              for label, source in cases do
                  let outcome =
                      Interpreter.run Budget.defaults Set.empty Env.empty (parseOk source)

                  match outcome.Fault with
                  | Some(Unsupported message) ->
                      Expect.stringContains message "only for an SCM return map" $"{label}: stable refusal"
                  | other -> failtestf "%s: expected an explicit lax-path refusal, got %A" label other
          } ]

let junitSummaryValues =
    let runJUnitScript (summary: JUnitSummary) source =
        let performed = ResizeArray<string>()

        let host =
            { Perform =
                fun _ name _ _ _ ->
                    performed.Add name
                    if name = "source" then VJUnitSummary(ref summary) else VNull
              CanContinue = fun () -> true
              SetEnv = fun _ _ -> ()
              CurrentEnv = fun () -> []
              TakesBlock = fun _ -> false }

        let outcome =
            Interpreter.runHosted
                host
                Budget.defaults
                (set [ "source"; "sink" ])
                Env.empty
                (parseOk source)

        outcome, List.ofSeq performed

    let positive =
        { TotalCount = 4L
          FailCount = 2L
          SkipCount = 1L
          PassCount = 1L
          Duration = Some 7.75f }

    let zero =
        { TotalCount = 0L
          FailCount = 0L
          SkipCount = 0L
          PassCount = 0L
          Duration = Some 0.0f }

    let expectOpaqueRefusal label operation =
        let source =
            "def value = source()\n"
            + $"try {{ {operation}; sink('escaped') }} catch (Exception problem) {{ sink('caught') }}\n"

        let outcome, performed = runJUnitScript positive source

        match outcome.Fault with
        | Some(Denied _)
        | Some(Unsupported _)
        | Some(HostedCallRefused _) -> ()
        | other -> failtestf "%s: expected a catch-opaque modelling refusal, got %A" label other

        Expect.equal performed [ "source" ] $"{label}: neither successor nor catch handler reached the host"

    testList
        "FG-213/FG-214 nominal JUnit summary accessors"
        [ test "properties and zero-argument getters return the same Integer values" {
              for label, summary in [ "positive", positive; "zero", zero ] do
                  let source =
                      "def value = source()\n"
                      + "return [value.totalCount, value.failCount, value.skipCount, value.passCount, "
                      + "value.getTotalCount(), value.getFailCount(), value.getSkipCount(), value.getPassCount()]\n"

                  let outcome, performed = runJUnitScript summary source
                  Expect.isNone outcome.Fault $"{label}: every measured accessor runs"
                  Expect.equal performed [ "source" ] $"{label}: access remains interpreter-local"

                  match outcome.Returned with
                  | Some(VList values) ->
                      let expectedAccessors =
                          [ VArithmeticInteger summary.TotalCount
                            VArithmeticInteger summary.FailCount
                            VArithmeticInteger summary.SkipCount
                            VInteger summary.PassCount ]

                      Expect.equal
                          values.Value
                          (expectedAccessors @ expectedAccessors)
                          $"{label}: getter and property spellings preserve exact Integer values"
                  | other -> failtestf "%s: expected eight accessor values, got %A" label other
          }

          test "every property and getter is Integer-not-Long" {
              let source =
                  "def value = source()\n"
                  + "return [value.totalCount, value.failCount, value.skipCount, value.passCount, "
                  + "value.getTotalCount(), value.getFailCount(), value.getSkipCount(), value.getPassCount()].every { "
                  + "it instanceof Integer && !(it instanceof Long) }\n"

              for label, summary in [ "positive", positive; "zero", zero ] do
                  let outcome, performed = runJUnitScript summary source
                  Expect.isNone outcome.Fault $"{label}: provenance checks run"
                  Expect.equal outcome.Returned (Some(VBool true)) $"{label}: all eight accessors are Integer-only"
                  Expect.equal performed [ "source" ] $"{label}: provenance checks have no hosted effects"
          }

          test "total fail and skip retain their established arithmetic result surface" {
              for accessor, expected in
                  [ "totalCount", 4L
                    "failCount", 2L
                    "skipCount", 1L
                    "getTotalCount()", 4L
                    "getFailCount()", 2L
                    "getSkipCount()", 1L ] do
                  let source =
                      "def value = source()\n"
                      + $"def count = value.{accessor}\n"
                      + "return [-count, count + 1, count - 1, count * 2, count / 1, count % 2, count >= 0, count == "
                      + string expected
                      + ", (count ? 1 : 0), count..(count + 1)]\n"

                  let outcome, performed = runJUnitScript positive source
                  Expect.isNone outcome.Fault $"{accessor}: the pre-existing VInt-compatible operations remain admitted"
                  Expect.equal performed [ "source" ] $"{accessor}: arithmetic remains interpreter-local"

                  let expectedValues =
                      [ VInt(-expected)
                        VInt(expected + 1L)
                        VInt(expected - 1L)
                        VInt(expected * 2L)
                        VInt expected
                        VInt(expected % 2L)
                        VBool true
                        VBool true
                        VInt 1L
                        VRange [ expected; expected + 1L ] ]

                  match outcome.Returned with
                  | Some(VList values) ->
                      Expect.equal values.Value expectedValues $"{accessor}: every result type matches the former VInt path"
                  | other -> failtestf "%s: expected compatibility-operation results, got %A" accessor other
          }

          test "passCount property and getter keep arithmetic and range refused" {
              for accessor in [ "passCount"; "getPassCount()" ] do
                  for label, expression in
                      [ "unary", "-count"
                        "plus", "count + 1"
                        "minus", "count - 1"
                        "multiply", "count * 2"
                        "divide", "count / 1"
                        "modulo", "count % 2"
                        "range", "count..2" ] do
                      expectOpaqueRefusal $"{accessor} {label}" $"def count = value.{accessor}; def ignored = {expression}"
          }

          test "globally admitted getter names remain receiver and signature scoped" {
              for getter in Sandbox.junitSummaryCountGetters |> Set.toList do
                  for shape, operation in
                      [ "wrong receiver", $"def ignored = 'text'.{getter}()"
                        "free call", $"def ignored = {getter}()"
                        "positional argument", $"def ignored = value.{getter}(1)"
                        "named argument", $"def ignored = value.{getter}(extra: 1)"
                        "trailing closure", $"def ignored = value.{getter}() {{ sink('escaped') }}" ] do
                      expectOpaqueRefusal $"{getter} {shape}" operation
          }

          test "duration property and getter preserve Float provenance and rendering" {
              for label, summary, rendered in [ "positive", positive, "7.75"; "zero", zero, "0.0" ] do
                  let source =
                      "def value = source()\n"
                      + "return [value.duration, value.getDuration(), value.duration == value.getDuration(), "
                      + "value.duration instanceof Float, value.duration instanceof Number, "
                      + "value.duration instanceof Object, "
                      + "value.duration instanceof Double, value.duration instanceof BigDecimal, "
                      + "\"${value.duration}\"]\n"

                  let outcome, performed = runJUnitScript summary source
                  Expect.isNone outcome.Fault $"{label}: duration accessors run"
                  Expect.equal performed [ "source" ] $"{label}: duration access remains interpreter-local"

                  match outcome.Returned with
                  | Some(VList values) ->
                      Expect.equal
                          values.Value
                          [ VFloat summary.Duration.Value
                            VFloat summary.Duration.Value
                            VBool true
                            VBool true
                            VBool true
                            VBool true
                            VBool false
                            VBool false
                            VStr rendered ]
                          $"{label}: duration retains the measured Float surface"
                  | other -> failtestf "%s: expected duration accessor values, got %A" label other
          }

          test "duration rendering matches the measured Java Float spellings" {
              for value, expected in
                  [ 0.0f, "0.0"
                    1.25f, "1.25"
                    42.0f, "42.0"
                    2_503.1f, "2503.1"
                    31_536_000.0f, "3.1536E7"
                    System.Single.NaN, "NaN" ] do
                  Expect.equal (Value.javaFloatDisplay value) expected $"{value}: Java Float text"
          }

          test "unverified subnormal Float rendering fails closed" {
              let subnormal =
                  { positive with
                      Duration = Some(System.BitConverter.Int32BitsToSingle 1) }
              let outcome, performed = runJUnitScript subnormal "def value = source()\nreturn \"${value.duration}\"\n"

              match outcome.Fault with
              | Some(Unsupported message) -> Expect.stringContains message "binary32 boundary" "the unsafe formatter boundary is named"
              | other -> failtestf "expected subnormal-rendering refusal, got %A" other

              Expect.equal performed [ "source" ] "no hosted effect runs"
          }

          test "duration getter remains receiver and signature scoped" {
              for getter in Sandbox.junitSummaryDurationGetters |> Set.toList do
                  for shape, operation in
                      [ "wrong receiver", $"def ignored = 'text'.{getter}()"
                        "free call", $"def ignored = {getter}()"
                        "positional argument", $"def ignored = value.{getter}(1)"
                        "named argument", $"def ignored = value.{getter}(extra: 1)"
                        "trailing closure", $"def ignored = value.{getter}() {{ sink('escaped') }}" ] do
                      expectOpaqueRefusal $"{getter} {shape}" operation
          }

          test "unknown duration lexical provenance refuses only duration access" {
              let unknown = { positive with Duration = None }
              let outcome, performed = runJUnitScript unknown "def value = source()\nreturn value.totalCount\n"
              Expect.isNone outcome.Fault "count access remains available"
              Expect.equal outcome.Returned (Some(VArithmeticInteger positive.TotalCount)) "counts survive unknown duration text"
              Expect.equal performed [ "source" ] "count access remains interpreter-local"

              let refused, refusedPerformed = runJUnitScript unknown "def value = source()\ndef ignored = value.duration\n"
              match refused.Fault with
              | Some(Unsupported message) -> Expect.stringContains message "TimeToFloat" "the lexical boundary is named"
              | other -> failtestf "expected unknown-duration refusal, got %A" other
              Expect.equal refusedPerformed [ "source" ] "no hosted successor runs"
          }

          test "duration operations outside the measured surface remain refused" {
              for label, expression in
                  [ "unary", "-duration"
                    "plus", "duration + 1"
                    "ordering", "duration < 8"
                    "mixed equality", "duration == 8"
                    "unmeasured instanceof", "duration instanceof Serializable"
                    "range", "duration..8"
                    "truthiness", "duration ? 1 : 0"
                    "indexing", "duration[0]"
                    "direct toString", "duration.toString()" ] do
                  expectOpaqueRefusal label $"def duration = value.duration; def ignored = {expression}"

              for label, expression in
                  [ "string plus left", "'x' + duration"
                    "string plus right", "duration + 'x'"
                    "string left shift", "'x' << duration"
                    "list left shift", "[] << duration"
                    "list plus", "[] + [duration]"
                    "list join", "[duration].join(',')"
                    "list contains argument", "[].contains(duration)"
                    "list indexing receiver", "[duration][0]"
                    "list indexing key", "[1][duration]"
                    "map indexing key", "[:][duration]"
                    "list reverse", "[duration].reverse()"
                    "method iteration", "[duration].each { sink('escaped') }"
                    "method predicate iteration", "[duration].any { true }" ] do
                  expectOpaqueRefusal label $"def duration = value.duration; def ignored = {expression}"

              expectOpaqueRefusal
                  "iteration"
                  "def duration = value.duration; for (item in [duration]) { sink('escaped') }"
          } ]

/// FG-195: resolution is by SIGNATURE, as Groovy's is. The four measured shapes are
/// asserted TOGETHER because three refusals were added one at a time, each looking
/// complete, and the fourth was found inside the guard written for the third — a
/// signature model is only a fix if all four hold at once.
let callableResolution =
    // preamble helpers arrive exactly as the walker supplies them: SFunc folded into
    // the incoming env, so the tests exercise the same seam the script block uses
    let helperEnv src =
        parseOk src
        |> List.fold
            (fun acc s ->
                match s with
                | Fogell.Groovy.SFunc(n, ps, b) -> Env.withFunc n ps b acc
                | _ -> acc)
            Env.empty

    let runWith env src =
        Interpreter.runStrictVars Budget.defaults steps env (parseOk src)

    let faultText (o: Outcome) =
        match o.Fault with
        | Some(Unsupported m) -> m
        | other -> failtestf "expected an Unsupported refusal, got %A" other

    testList
        "FG-195 callable resolution by signature"
        [ test "shape (a): a local closure SHADOWS a preamble helper" {
              let o = runWith (helperEnv "def x() { return 'HELPER' }") "def x = { 'LOCAL' }\nreturn x()"
              Expect.isNone o.Fault "runs"
              Expect.equal o.Returned (Some(VStr "LOCAL")) "Groovy resolves the local"
          }

          test "shape (b): overloads resolve by ARITY, both directions" {
              let env = helperEnv "def pick() { return 'zero' }\ndef pick(v) { return 'one' }"
              Expect.equal ((runWith env "return pick()").Returned) (Some(VStr "zero")) "zero-arg body"
              Expect.equal ((runWith env "return pick(1)").Returned) (Some(VStr "one")) "one-arg body"
          }

          test "shape (c): no matching arity is a refusal NAMING the signature" {
              let o = runWith (helperEnv "def constant(v) { return v }") "return constant()"
              let m = faultText o
              Expect.stringContains m "0 argument(s)" "names what was attempted"
              Expect.stringContains m "candidates take 1" "names what exists"
          }

          test "shape (d): a named-argument group is ONE Map argument" {
              let o = runWith (helperEnv "def zero() { return 'z' }") "return zero(foo: 1)"
              Expect.stringContains (faultText o) "1 argument(s)" "the Map counted as an argument"
          }

          test "a named-argument group binds as a Map, FIRST" {
              let env = helperEnv "def one(m) { return m }"
              match (runWith env "return one(foo: 'bar')").Returned with
              | Some(VMap m) -> Expect.equal (Map.tryFind "foo" m.Value) (Some(VStr "bar")) "the group arrived as a Map"
              | other -> failtestf "expected a Map return, got %A" other
          }

          test "a trailing closure is a FINAL argument, and the parameter is callable" {
              // exercises helper resolution AND closure-value invocation in one shape:
              // the block binds to `c`, and `c()` is a local-closure call (FG-189)
              let env = helperEnv "def wrap(c) { return c() }"
              let o = runWith env "return wrap { 'FROM-BLOCK' }"
              Expect.isNone o.Fault "runs"
              Expect.equal o.Returned (Some(VStr "FROM-BLOCK")) "block passed, then invoked"
          }

          test "a closure with declared parameters binds by arity" {
              let o = runWith Env.empty "def f = { a, b -> a + b }\nreturn f(1, 2)"
              Expect.equal o.Returned (Some(VInt 3L)) "two arguments bound"
          }

          test "a closure arity mismatch is a refusal naming the signature" {
              let o = runWith Env.empty "def f = { a, b -> a + b }\nreturn f(1)"
              Expect.stringContains (faultText o) "2 parameter(s)" "names the closure's arity"
          }

          test "a SAME-ARITY duplicate is refused at the call, not silently last-wins" {
              let o = runWith Env.empty "def d() { return 'one' }\ndef d() { return 'two' }\nreturn d()"
              Expect.stringContains (faultText o) "more than once" "the ambiguity is named"
          }

          test "the .call spelling resolves exactly as the bare call does" {
              let o = runWith Env.empty "def f = { v -> v }\nreturn f.call('VIA-CALL')"
              Expect.equal o.Returned (Some(VStr "VIA-CALL")) "explicit call spelling"
          }

          test "a caught fault inside a closure does not leak call depth" {
              // the verifier's construction: each caught fault skipped the depth
              // decrement, so 70 caught faults exhausted the 64-deep call budget
              // with an UNCATCHABLE refusal where Jenkins prints and succeeds
              let o =
                  runWith
                      Env.empty
                      "def boom = { 1 / 0 }\nfor (i in 1..70) { try { boom() } catch (e) { } }\nreturn 'SURVIVED'"

              Expect.isNone o.Fault "depth restored on the fault path"
              Expect.equal o.Returned (Some(VStr "SURVIVED")) "the loop of caught faults completes"
          }

          test "in-script overloads resolve too, not only preamble ones" {
              let o = runWith Env.empty "def pick() { return 'zero' }\ndef pick(v) { return 'one' }\nreturn pick(9)"
              Expect.equal o.Returned (Some(VStr "one")) "arity picks among hoisted candidates"
          } ]

/// FG-193: a Groovy map is a REFERENCE object. Aliases share the ref; the Jenkins
/// environment is recognised by the MAP's identity, however many rebinds away.
let mapIdentity =
    testList
        "FG-193 map reference identity"
        [ test "a mutation through an alias is visible to every name" {
              // measured: jenkins=alias:x fogell=alias:null before the ref
              let o = Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk "def local = [:]\ndef other = local\nother.FOO = 'x'\nreturn local.FOO")
              Expect.equal o.Returned (Some(VStr "x")) "the write reached the shared map"
          }

          // the statement-level index spelling (`n['b'] = …`) is FG-015b's open
          // parse gap and cannot be covered here until it parses

          test "a non-map receiver's property write faults CATCHABLY in strict mode" {
              // measured: Jenkins throws and a script-level catch intercepts it —
              // the fault class matters as much as the fault (the first spelling
              // raised an uncatchable refusal and diverged where parity had held)
              let uncaught = Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk "def s = 'str'\ns.FOO = 'x'\nreturn 'SURVIVED'")

              match uncaught.Fault with
              | Some(UnknownProperty k) -> Expect.equal k "FOO" "names the property"
              | other -> failtestf "expected UnknownProperty, got %A" other

              let caught =
                  Interpreter.runStrictVars Budget.defaults steps Env.empty
                      (parseOk "def s = 'str'\ntry { s.FOO = 'x' } catch (Exception e) { }\nreturn 'SURVIVED'")

              Expect.isNone caught.Fault "the catch intercepted it"
              Expect.equal caught.Returned (Some(VStr "SURVIVED")) "execution continued, as on Jenkins"
          }

          test "the Jenkins env is recognised by VALUE identity through a rebind" {
              // measured: the aliased write reached Jenkins' shell and silently
              // vanished here — it must route to the host exactly as the direct
              // spelling does, however many rebinds separate it
              let sets = ResizeArray<string * string>()

              let host =
                  { Perform = fun _ _ _ _ runBody -> (runBody |> Option.iter (fun run -> run ())); VNull
                    CanContinue = fun () -> true
                    SetEnv = fun k v -> sets.Add(k, v)
                    CurrentEnv = fun () -> []
                    TakesBlock = fun _ -> false }

              let seeded =
                  { Env.ofValues (Map.ofList [ "x", VNull ]) with Vars = Map.ofList [ "env", ref (VMap(ref Map.empty)) ] }

              let o =
                  Interpreter.runHosted host Budget.defaults steps seeded
                      (parseOk "def saved = env\ndef env = saved\nenv.FOO = 'bar'\nreturn 'DONE'")

              Expect.isNone o.Fault "ran"
              Expect.equal (List.ofSeq sets) [ "FOO", "bar" ] "the aliased write reached the host"
          } ]

/// FG-191: equality and display survive cyclic values. Three shapes killed the
/// PROCESS before this — no fault, no receipt, walker dead — and each is pinned
/// here where CI can see it, beside the identity semantics that replaced the
/// structural walk for closures.
let cyclicValues =
    let runS src =
        Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk src)

    let sharedDag leaf depth =
        let mutable value = VList(ref [ leaf ])

        for _ in 1..depth do
            value <- VList(ref [ value; value ])

        value

    testList
        "FG-191 cyclic values and closure identity"
        [ test "reference-cycle scan visits a shared DAG once and remains stack-safe" {
              let depth = 30
              let dag = sharedDag (VInt 1L) depth
              let scan = Value.scanReferenceCycles dag

              Expect.isFalse scan.HasCycle "sibling aliases are not ancestry cycles"
              Expect.equal scan.ReferencesVisited (depth + 1) "every distinct list ref is entered once"
              Expect.equal scan.EdgesVisited (1 + (2 * depth)) "edges are charged once per completed node"

              let mutable deep = VNull
              let deepLength = 50000

              for _ in 1..deepLength do
                  deep <- VList(ref [ deep ])

              let deepScan = Value.scanReferenceCycles deep
              Expect.isFalse deepScan.HasCycle "a deep acyclic chain is not a cycle"
              Expect.equal deepScan.ReferencesVisited deepLength "iterative traversal reaches every ref without stack recursion"
              Expect.equal deepScan.EdgesVisited deepLength "one edge per chain node"

              let direct = ref [ VNull ]
              direct.Value <- [ VList direct ]
              Expect.isTrue (Value.scanReferenceCycles (VList direct)).HasCycle "a direct list cycle remains visible"

              let mixedList = ref [ VNull ]
              let mixedMap = ref (Map.ofList [ "back", VList mixedList ])
              mixedList.Value <- [ VMap mixedMap ]
              Expect.isTrue (Value.scanReferenceCycles (VList mixedList)).HasCycle "a mixed list/map cycle remains visible"
          }

          test "equality and ordering memoize completed shared pairs" {
              let left = sharedDag (VInt 1L) 26
              let right = sharedDag (VInt 1L) 26
              let unequal = sharedDag (VInt 2L) 26

              Expect.equal (Value.tryEq left right) (Value.Answer true) "equal shared DAGs terminate without re-expansion"
              Expect.equal (Value.tryCompare left right) Value.Unorderable "ordering shared acyclic DAGs refuses by name (FG-205) and terminates without re-expansion"
              Expect.equal (Value.tryEq left unequal) (Value.Answer false) "memoization cannot turn a leaf mismatch into equality"
              Expect.equal (Value.tryCompare left unequal) Value.Unorderable "a leaf mismatch between acyclic lists is still Jenkins' hash fallback, not a structural order"

              let directLeft = ref [ VNull ]
              let directRight = ref [ VNull ]
              directLeft.Value <- [ VList directLeft ]
              directRight.Value <- [ VList directRight ]

              Expect.equal
                  (Value.tryEq (VList directLeft) (VList directRight))
                  Value.CycleDetected
                  "completed pairs never mask a direct equality cycle"

              Expect.equal
                  (Value.tryCompare (VList directLeft) (VList directRight))
                  Value.OrderingCycleDetected
                  "completed pairs never mask a direct ordering cycle"

              let mixedListLeft = ref [ VNull ]
              let mixedListRight = ref [ VNull ]
              let mixedMapLeft = ref (Map.ofList [ "back", VList mixedListLeft ])
              let mixedMapRight = ref (Map.ofList [ "back", VList mixedListRight ])
              mixedListLeft.Value <- [ VMap mixedMapLeft ]
              mixedListRight.Value <- [ VMap mixedMapRight ]

              Expect.equal
                  (Value.tryEq (VList mixedListLeft) (VList mixedListRight))
                  Value.CycleDetected
                  "a list/map equality cycle crosses both memoized pair kinds"

              Expect.equal
                  (Value.tryCompare (VList mixedListLeft) (VList mixedListRight))
                  Value.OrderingCycleDetected
                  "a list/map ordering cycle crosses both memoized pair kinds"
          }

          test "displaying a self-referential map renders (this Map), as Groovy does" {
              let o = runS "def m = [:]\nm.self = m\nreturn \"${m}\""
              Expect.isNone o.Fault "survives"
              Expect.equal o.Returned (Some(VStr "[self:(this Map)]")) "Groovy's own rendering"
          }

          test "list identity is cycle-safe without erasing Groovy's direct-self marker" {
              let direct = runS "def xs = [null]\nxs[0] = xs\nreturn \"${xs}:${xs == xs}\""
              Expect.isNone direct.Fault "direct self display and identity equality survive"
              Expect.equal direct.Returned (Some(VStr "[(this Collection)]:true")) "Groovy's direct self marker"

              let distinct = runS "def a = [null]\na[0] = a\ndef b = [null]\nb[0] = b\nreturn a == b"

              match distinct.Fault with
              | Some(CyclicValue Equality) -> ()
              | other -> failtestf "expected distinct-cycle comparison fault, got %A" other

              let mixed = runS "def xs = [null]\ndef m = [back: xs]\nxs[0] = m\nreturn \"${xs}\""

              match mixed.Fault with
              | Some(CyclicValue Display) -> ()
              | other -> failtestf "expected typed mixed-cycle display fault, got %A" other
          }

          test "cyclic display retains StackOverflowError ancestry for interpolation and toString" {
              let notException =
                  runS (
                      "def xs = [null]\ndef m = [back: xs]\nxs[0] = m\n"
                      + "try { return \"${xs}\" } catch (Exception e) { return 'wrong' }"
                  )

              match notException.Fault with
              | Some(CyclicValue Display) -> ()
              | other -> failtestf "Exception incorrectly intercepted cyclic display: %A" other

              let caughtByError =
                  runS (
                      "def xs = [null]\ndef m = [back: xs]\nxs[0] = m\n"
                      + "try { return xs.toString() } catch (Error e) { return 'caught-error' }"
                  )

              Expect.isNone caughtByError.Fault "Error intercepts StackOverflowError"
              Expect.equal caughtByError.Returned (Some(VStr "caught-error")) "explicit toString uses typed display fault"

              let caughtByThrowable =
                  runS (
                      "def xs = [null]\ndef m = [back: xs]\nxs[0] = m\n"
                      + "try { return 'value=' + xs } catch (Throwable e) { return 'caught-throwable' }"
                  )

              Expect.isNone caughtByThrowable.Fault "Throwable intercepts StackOverflowError"
              Expect.equal caughtByThrowable.Returned (Some(VStr "caught-throwable")) "string concatenation shares the boundary"
          }

          test "list closure methods and for-in observe live index writes and extensions" {
              let o =
                  runS (
                      "def eachXs = [1, 2]\ndef eachSeen = []\n"
                      + "eachXs.each { eachSeen << it; if (it == 1) { eachXs[0] = 8; eachXs[1] = 9; eachXs << 3 }; if (it == 9) { eachXs[0] = 7 } }\n"
                      + "echo \"each:${eachSeen}:${eachXs}\"\n"
                      + "def collectXs = [1, 2]\ndef collected = collectXs.collect { if (it == 1) { collectXs[1] = 9; collectXs << 3 }; it * 10 }\n"
                      + "echo \"collect:${collected}:${collectXs}\"\n"
                      + "def filterXs = [1, 2]\ndef filtered = filterXs.findAll { if (it == 1) { filterXs[1] = 9; filterXs << 3 }; it > 1 }\n"
                      + "echo \"findAll:${filtered}:${filterXs}\"\n"
                      + "def findXs = [1, 2]\ndef found = findXs.find { if (it == 1) { findXs[1] = 9; findXs << 3 }; it > 5 }\n"
                      + "echo \"find:${found}:${findXs}\"\n"
                      + "def anyXs = [1, 2]\ndef anyResult = anyXs.any { if (it == 1) { anyXs[1] = 9; anyXs << 3 }; it == 9 }\n"
                      + "echo \"any:${anyResult}:${anyXs}\"\n"
                      + "def everyXs = [1, 2]\ndef everySeen = []\n"
                      + "def everyResult = everyXs.every { everySeen << it; if (it == 1) { everyXs[1] = 9; everyXs << 3 }; it < 10 }\n"
                      + "echo \"every:${everyResult}:${everySeen}:${everyXs}\"\n"
                      + "def forXs = [1, 2]\ndef forSeen = []\n"
                      + "for (v in forXs) { forSeen << v; if (v == 1) { forXs[1] = 9; forXs[3] = 4 } }\n"
                      + "echo \"for:${forSeen}:${forXs}\""
                  )

              Expect.isNone o.Fault "all measured live traversals execute"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "each:[1, 9, 3]:[7, 9, 3]" ]
                    "echo", [ "collect:[10, 90, 30]:[1, 9, 3]" ]
                    "echo", [ "findAll:[9, 3]:[1, 9, 3]" ]
                    "echo", [ "find:9:[1, 9, 3]" ]
                    "echo", [ "any:true:[1, 9, 3]" ]
                    "echo", [ "every:true:[1, 9, 3]:[1, 9, 3]" ]
                    "echo", [ "for:[1, 9, null, 4]:[1, 9, null, 4]" ] ]
                  "current is captured, unvisited writes are observed, and appended/extended slots are visited"
          }

          test "sorting cyclic lists follows Jenkins' alias and StackOverflow boundaries without host recursion" {
              let alias = runS "def a = [null]\na[0] = a\ndef sorted = [a, a].sort()\nreturn sorted.size()"
              Expect.isNone alias.Fault "a top-level alias compares by identity"
              Expect.equal alias.Returned (Some(VInt 2L)) "both alias entries remain"

              let distinct =
                  runS (
                      "def a = [null]\ndef b = [null]\na[0] = a\nb[0] = b\n"
                      + "try { [a, b].sort() } catch (Throwable e) { return 'caught' }\nreturn 'missed'"
                  )

              Expect.isNone distinct.Fault "StackOverflowError is catchable by Throwable"
              Expect.equal distinct.Returned (Some(VStr "caught")) "the cycle becomes a survivable scripted fault"

              let nestedAlias =
                  runS (
                      "def a = [null]\na[0] = a\ndef left = [a]\ndef right = [a]\n"
                      + "try { [left, right].sort() } catch (Error e) { return 'caught' }\nreturn 'missed'"
                  )

              Expect.isNone nestedAlias.Fault "the nested-alias overflow is catchable by Error"
              Expect.equal nestedAlias.Returned (Some(VStr "caught")) "identity does not over-short-circuit nested comparison"

              let notException =
                  runS (
                      "def a = [null]\ndef b = [null]\na[0] = a\nb[0] = b\n"
                      + "try { [a, b].sort() } catch (Exception e) { return 'wrong' }"
                  )

              match notException.Fault with
              | Some(CyclicValue Ordering) -> ()
              | other -> failtestf "Exception incorrectly intercepted StackOverflowError: %A" other
          }

          test "every collection ordering entry point is host-safe" {
              let sorted = runS "return [3, 1, 2].sort()"
              Expect.equal sorted.Returned (Some(VList(ref [ VInt 1L; VInt 2L; VInt 3L ]))) "acyclic sort is unchanged"

              for methodCall in [ "min()"; "max()"; "unique(false)" ] do
                  let outcome =
                      runS (
                          "def a = [null]\ndef b = [null]\na[0] = a\nb[0] = b\n"
                          + $"return [a, b].{methodCall}"
                      )

                  match outcome.Fault with
                  | Some(Denied _) -> ()
                  | Some(Unsupported _) -> ()
                  | other -> failtestf "%s escaped its explicit safe refusal: %A" methodCall other
          }

          test "same-AST closures from two calls are NOT equal — identity, not structure" {
              // this exact comparison was a process-killing stack overflow
              let o = runS "def make() {\n    def r\n    r = { r }\n    return r\n}\ndef a = make()\ndef b = make()\nreturn a == b"
              Expect.isNone o.Fault "survives"
              Expect.equal o.Returned (Some(VBool false)) "distinct invocations, distinct closures"
          }

          test "an aliased closure IS equal; a distinct literal is not" {
              let o = runS "def a = { 1 }\ndef b = { 1 }\ndef c = a\nreturn [a == b, a == c]"
              Expect.equal o.Returned (Some(VList(ref [ VBool false; VBool true ]))) "reference semantics"
          }

          test "comparing two distinct cyclic maps FAULTS instead of dying" {
              // Groovy's own chase is a JVM StackOverflowError and a failed build;
              // the fault below is this runtime's survivable spelling of the same
              let o = runS "def m = [:]\nm.self = m\ndef n = [:]\nn.self = n\nreturn m == n"

              match o.Fault with
              | Some(CyclicValue Equality) -> ()
              | other -> failtestf "expected the cycle fault, got %A" other
          }

          test "cyclic equality preserves Error ancestry across == and contains" {
              let notException =
                  runS (
                      "def a = [null]\ndef b = [null]\na[0] = a\nb[0] = b\n"
                      + "try { return a == b } catch (Exception e) { return 'wrong' }"
                  )

              match notException.Fault with
              | Some(CyclicValue Equality) -> ()
              | other -> failtestf "Exception incorrectly intercepted cyclic equality: %A" other

              let caught =
                  runS (
                      "def a = [null]\ndef b = [null]\na[0] = a\nb[0] = b\n"
                      + "try { return [a].contains(b) } catch (Error e) { return 'caught' }"
                  )

              Expect.isNone caught.Fault "Error catches cyclic contains equality"
              Expect.equal caught.Returned (Some(VStr "caught")) "the Error arm owns recovery"
          }

          test "hosted collection coercion faults before dispatch while interpolation keeps self markers" {
              let performed = ResizeArray<string * Value list * (string * Value) list>()

              let host =
                  { Perform =
                      fun _ name positional named runBody ->
                          performed.Add(name, positional, named)
                          runBody |> Option.iter (fun run -> run ())
                          VNull
                    CanContinue = fun () -> true
                    SetEnv = fun _ _ -> ()
                    CurrentEnv = fun () -> []
                    TakesBlock = fun _ -> false }

              let displayed =
                  Interpreter.runHosted host Budget.defaults steps Env.empty
                      (parseOk "def xs = [null]\nxs[0] = xs\necho \"${xs}\"\nreturn 'done'")

              Expect.isNone displayed.Fault "interpolation is an ordinary display context"
              Expect.equal displayed.Returned (Some(VStr "done")) "script continued"
              Expect.equal (List.ofSeq performed) [ "echo", [ VStr "[(this Collection)]" ], [] ] "only the rendered string reached the host"

              performed.Clear()

              let notException =
                  Interpreter.runHosted host Budget.defaults steps Env.empty
                      (parseOk (
                          "def xs = [null]\nxs[0] = xs\n"
                          + "try { echo xs } catch (Exception e) { return 'wrong' }"
                      ))

              match notException.Fault with
              | Some(CyclicValue HostedArgumentCoercion) -> ()
              | other -> failtestf "Exception incorrectly intercepted hosted coercion: %A" other

              Expect.isEmpty performed "the host was never entered"

              let caught =
                  Interpreter.runHosted host Budget.defaults steps Env.empty
                      (parseOk (
                          "def xs = [null]\nxs[0] = xs\n"
                          + "try { echo message: xs } catch (Error e) { return 'caught' }"
                      ))

              Expect.isNone caught.Fault "Error catches the pre-dispatch StackOverflowError"
              Expect.equal caught.Returned (Some(VStr "caught")) "the handler ran"
              Expect.isEmpty performed "named coercion also has zero hosted effects"
          }

          test "a shared DAG reaches hosted validation promptly without an effect" {
              let mutable validations = 0
              let effects = ResizeArray<string>()

              let host =
                  { Perform =
                      fun _ _ _ _ _ ->
                          validations <- validations + 1
                          Interpreter.raiseHostedCallRefused "deleteDir takes no arguments"
                    CanContinue = fun () -> true
                    SetEnv = fun _ _ -> ()
                    CurrentEnv = fun () -> []
                    TakesBlock = fun _ -> false }

              let stopwatch = System.Diagnostics.Stopwatch.StartNew()

              let outcome =
                  Interpreter.runHosted host Budget.defaults (set [ "deleteDir" ]) Env.empty
                      (parseOk (
                          "def dag = [1]\ndef i = 0\n"
                          + "while (i < 30) { dag = [dag, dag]; i++ }\n"
                          + "deleteDir dag"
                      ))

              stopwatch.Stop()
              Expect.equal outcome.Fault (Some(HostedCallRefused "deleteDir takes no arguments")) "validation refusal is returned"
              Expect.equal validations 1 "the small shared graph crosses cycle coercion once"
              Expect.isEmpty effects "validation refusal records no hosted effect"
              Expect.isLessThan stopwatch.ElapsedMilliseconds 5000L "completed-reference memoization avoids exponential alias expansion"
          }

          test "cyclic map keys preserve hashing Error ancestry" {
              let notException =
                  runS (
                      "def key = [null]\nkey[0] = key\ndef keyed = [:]\n"
                      + "try { keyed[key] = 'x' } catch (Exception e) { return 'wrong' }"
                  )

              match notException.Fault with
              | Some(CyclicValue HashKey) -> ()
              | other -> failtestf "Exception incorrectly intercepted cyclic hashing: %A" other

              let caught =
                  runS (
                      "def key = [null]\nkey[0] = key\ndef keyed = [:]\n"
                      + "try { keyed[key] = 'x' } catch (Throwable e) { return keyed.size() }"
                  )

              Expect.isNone caught.Fault "Throwable catches cyclic key hashing"
              Expect.equal caught.Returned (Some(VInt 0L)) "the map was not mutated"
          }

          test "closures minted by ONE literal in a loop are DISTINCT" {
              // the identity model's first spelling compared the captured env
              // RECORD, and a while body assigning through cells never changes
              // it — this returned true, a false equality this branch's own
              // probe caught before it shipped; every evaluation mints a fresh
              // record now (receipt script-loop-closure-eq)
              let o =
                  Interpreter.runStrictVars Budget.defaults steps Env.empty
                      (parseOk "def xs = []\ndef i = 0\nwhile (i < 2) {\n    xs = xs + [{ 9 }]\n    i = i + 1\n}\nreturn xs[0] == xs[1]")

              Expect.equal o.Returned (Some(VBool false)) "per evaluation, not per source location"
          }

          test "a map compared WITH ITSELF is simply equal — identity short-circuits" {
              let o = runS "def m = [:]\nm.self = m\nreturn m == m"
              Expect.equal o.Returned (Some(VBool true)) "same ref, no walk"
          } ]

let fg015bSortAndRangeReview =
    let runS src =
        Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk src)

    testList
        "FG-015b sort identity and immutable IntRange"
        [ test "no-argument sort mutates and returns the same list identity" {
              let o =
                  runS (
                      "def xs = [2, 1, 2]\ndef alias = xs\ndef sorted = xs.sort()\n"
                      + "sorted[0] = 9\necho xs\necho alias\necho sorted"
                  )

              Expect.isNone o.Fault "the measured no-copy overload runs"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[9, 2, 2]" ]; "echo", [ "[9, 2, 2]" ]; "echo", [ "[9, 2, 2]" ] ]
                  "receiver, alias, and return share one ref cell"

              let closureSort = runS "def xs = [2, 1]\nreturn xs.sort { -it }"

              match closureSort.Fault with
              | Some(Unsupported why) -> Expect.stringContains why "comparator/key closure" "named overload boundary"
              | other -> failtestf "sort closure was silently accepted: %A" other
          }

          test "a cyclic sort faults before replacing the receiver contents" {
              let o =
                  runS (
                      "def left = [null]\ndef right = [null]\nleft[0] = left\nright[0] = right\n"
                      + "def xs = [left, right, 1]\n"
                      + "try { xs.sort() } catch (Throwable ignored) { echo \"state:${xs[0] == left}:${xs[1] == right}:${xs[2] == 1}\" }"
                  )

              Expect.isNone o.Fault "the typed StackOverflowError is caught"
              Expect.equal (stepArgs o) [ "echo", [ "state:true:true:true" ] ] "no partial receiver replacement"
          }

          test "IntRange stays list-like for reads, equality, iteration, reverse and collect" {
              let o =
                  runS (
                      "def r = 1..3\ndef eachSeen = []\nr.each { eachSeen << it }\n"
                      + "def forSeen = []\nfor (v in 3..1) { forSeen << v }\n"
                      + "def reversed = r.reverse()\nreversed[0] = 9\n"
                      + "def collected = r.collect { it * 10 }\ncollected[0] = 8\n"
                      + "echo \"range:${r}:${r[0]}:${r[-1]}:${r[5]}:${r == [1, 2, 3]}\"\n"
                      + "echo \"iteration:${eachSeen}:${forSeen}\"\n"
                      + "echo \"fresh:${reversed}:${collected}:${r}\""
                  )

              Expect.isNone o.Fault "all read-only/fresh-result range operations execute"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "range:[1, 2, 3]:1:3:null:true" ]
                    "echo", [ "iteration:[1, 2, 3]:[3, 2, 1]" ]
                    "echo", [ "fresh:[9, 2, 1]:[8, 20, 30]:[1, 2, 3]" ] ]
                  "range itself remains immutable while fresh results mutate"
          }

          test "every IntRange replacement form faults at Jenkins' measured write phase" {
              let o =
                  runS (
                      "def r = 1..3\ndef alias = r\ndef events = []\n"
                      + "def rhs = { events << 'plain-rhs'; 9 }\n"
                      + "def compoundRhs = { events << 'compound-rhs'; 2 }\n"
                      + "try { r[0] = rhs() } catch (UnsupportedOperationException ignored) { events << 'plain-caught' }\n"
                      + "try { r[0] += compoundRhs() } catch (UnsupportedOperationException ignored) { events << 'compound-caught' }\n"
                      + "try { r[0]++ } catch (UnsupportedOperationException ignored) { events << 'postfix-caught' }\n"
                      + "try { alias[-1] = 7 } catch (UnsupportedOperationException ignored) { events << 'alias-caught' }\n"
                      + "try { r.sort() } catch (UnsupportedOperationException ignored) { events << 'sort-caught' }\n"
                      + "echo events\necho r\necho alias"
                  )

              Expect.isNone o.Fault "all typed faults are caught"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[plain-rhs, plain-caught, compound-rhs, compound-caught, postfix-caught, alias-caught, sort-caught]" ]
                    "echo", [ "[1, 2, 3]" ]
                    "echo", [ "[1, 2, 3]" ] ]
                  "plain and compound RHS run before the failed write; no form mutates the range"
          } ]

/// FG-205. The `sort` builtin against Jenkins' `NumberAwareComparator`, measured
/// on the pinned lab (the probe table on the ticket; receipt
/// `fg205-cyclic-map-sort`). Groovy swallows `Cannot compare` and orders the
/// pair by `hashCode()`: a cyclic map anywhere inside either element overflows
/// there, and an acyclic map sorts by Java hash order — which this engine does
/// not model, so it refuses by name instead of printing the structural order
/// it printed before this ticket.
let fg205SortFallback =
    let runS src =
        Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk src)

    let refuses script =
        match (runS script).Fault with
        | Some(Unsupported why) ->
            Expect.stringStarts why "unsupported_collection_ordering:" $"{script} refused under another name"
        | other -> failtestf "%s sorted instead of refusing by name: %A" script other

    testList
        "FG-205 sort follows Jenkins' hash fallback boundary"
        [ test "acyclic maps and lists refuse by name instead of sorting structurally" {
              for script in
                  [ "return [[b: 2], [a: 1]].sort()"
                    "return [[a: 1], [a: 0]].sort()"
                    "return [[k: 1], [k: 1]].sort()"
                    "return [[a: 1], 100].sort()"
                    "return [[2], [1]].sort()" ] do
                  refuses script
          }

          test "scalars of different classes refuse; same-class scalars and null still order as measured" {
              for script in
                  [ "return ['ab', 5000].sort()"
                    "return [5000, 'ab'].sort()"
                    "return [true, 1].sort()"
                    "return ['a', 1].sort()" ] do
                  refuses script

              let ints = runS "return [3, 1, 2].sort()"
              Expect.equal ints.Returned (Some(VList(ref [ VInt 1L; VInt 2L; VInt 3L ]))) "integral order is unchanged"
              let strings = runS "return ['b', 'a', null].sort()"

              Expect.equal
                  strings.Returned
                  (Some(VList(ref [ VNull; VStr "a"; VStr "b" ])))
                  "null first, then code-unit order (measured)"

              let bools = runS "return [true, false].sort()"
              Expect.equal bools.Returned (Some(VList(ref [ VBool false; VBool true ]))) "false before true (measured)"
          }

          test "a cyclic map anywhere in a hash-fallback pair faults as Jenkins' StackOverflowError" {
              let cyclic = "def m = [k: 1]\nm.self = m\n"

              for sorted in [ "[[1, m], [2]]"; "[m, 5]"; "[5, m]"; "[[k: 1, self: null], m]"; "[[m], [m]]" ] do
                  let caught =
                      runS (cyclic + $"try {{ {sorted}.sort() }} catch (Throwable e) {{ return 'caught' }}\nreturn 'missed'")

                  Expect.isNone caught.Fault $"{sorted}: the overflow is a survivable scripted fault"
                  Expect.equal caught.Returned (Some(VStr "caught")) $"{sorted}: caught by Throwable"

              let notException =
                  runS (cyclic + "try { [[1, m], [2]].sort() } catch (Exception e) { return 'wrong' }")

              match notException.Fault with
              | Some(CyclicValue Ordering) -> ()
              | other -> failtestf "Exception intercepted the StackOverflowError: %A" other
          }

          test "the same map on both sides returns before the fallback" {
              let alias = runS "def m = [k: 1]\nm.self = m\nreturn [m, m].sort().size()"
              Expect.isNone alias.Fault "identity short-circuits"
              Expect.equal alias.Returned (Some(VInt 2L)) "both entries remain"
          } ]

/// FG-241. `=~`/`==~` with a pattern that does not compile. MEASURED on the
/// pinned lab (receipt `fg241-regex-pattern-fault`): Jenkins raises
/// java.util.regex.PatternSyntaxException, which `catch (Exception)`,
/// `catch (IllegalArgumentException)` and the class itself intercept and
/// `catch (ArithmeticException)` lets escape; uncaught, it fails the build.
/// Before this ticket every construction failure — and the 100 ms matching
/// budget — read as `false`.
let fg241RegexPatternFault =
    let runS src =
        Interpreter.runStrictVars Budget.defaults steps Env.empty (parseOk src)

    testList
        "FG-241 invalid regex patterns fault instead of answering false"
        [ test "an invalid pattern is a typed fault, for both operators" {
              for op in [ "==~"; "=~" ] do
                  let o = runS $"return 'ab' {op} /a)b|ab/"

                  match o.Fault with
                  | Some(RegexPatternInvalid(pattern, detail)) ->
                      Expect.equal pattern "a)b|ab" $"{op}: the pattern is carried"
                      Expect.isNotEmpty detail $"{op}: the host diagnosis is carried"
                  | other -> failtestf "%s answered instead of faulting: %A" op other
          }

          test "the measured catch boundary: Exception, IllegalArgumentException and the class catch it; ArithmeticException does not" {
              for clause in [ "Exception e"; "IllegalArgumentException e"; "PatternSyntaxException e"; "e" ] do
                  let o =
                      runS $"try {{ return 'ab' ==~ /a)b|ab/ }} catch ({clause}) {{ return 'caught' }}"

                  Expect.isNone o.Fault $"catch ({clause}) intercepts"
                  Expect.equal o.Returned (Some(VStr "caught")) $"catch ({clause}) runs its handler"

              let escaped =
                  runS "try { return 'ab' ==~ /a)b|ab/ } catch (ArithmeticException e) { return 'wrong' }"

              match escaped.Fault with
              | Some(RegexPatternInvalid _) -> ()
              | other -> failtestf "ArithmeticException intercepted a PatternSyntaxException: %A" other
          }

          test "the caught value renders class-then-message, and valid patterns are unchanged" {
              let bound = runS "try { return 'ab' ==~ /[z-a]/ } catch (Exception e) { return \"${e}\" }"

              match bound.Returned with
              | Some(VStr text) ->
                  Expect.stringStarts text "java.util.regex.PatternSyntaxException: " "Jenkins' `${e}` shape, class then message"
              | other -> failtestf "no bound exception value: %A" other

              let valid = runS "return ['ab' ==~ /a.|zz/, 'ab' =~ /zz/, 'a}b' ==~ /a}b/]"
              Expect.equal valid.Returned (Some(VList(ref [ VBool true; VBool false; VBool true ]))) "valid patterns still answer"
          }

          test "an exhausted matching budget refuses by name instead of answering false" {
              let o = runS "return 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!' =~ /(a+)+$/"

              match o.Fault with
              | Some(Unsupported why) -> Expect.stringStarts why "unsupported_regex_budget:" "named refusal"
              | other -> failtestf "catastrophic backtracking answered or faulted differently: %A" other
          } ]

/// FG-180. Command-form calls in EXPRESSION position, and the constructs the
/// same corpus sweep recovered. The first test pins the defect that made this
/// P1: the positional form ADMITTED with a wrong AST — two statements, the
/// variable bound to an unresolved name — which no file count can see.
let fg180Grammar =
    let ast src = parseOk src

    testList
        "FG-180 expression-position command form"
        [ test "positional command-form initialiser is a CALL, not two statements" {
              match ast "def m = tool 'M3'" with
              | [ SDef("m", Some(ECall(FreeCall "tool", [ APos(EStr "M3") ], None))) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "the call actually runs — effects, not just shape" {
              let o = run "def out = sh 'make'\n"
              Expect.contains (stepNames o) "sh" "initialiser RHS reached the effect list"
          }

          test "named command-form initialiser" {
              let o = run "def st = sh script: 'make', returnStatus: true\n"
              Expect.contains (stepNames o) "sh" "named args in expression position"
          }

          test "command form inside a GString placeholder" {
              Expect.isTrue (parses "def p = \"${tool 'M3'}/bin\"\n") "placeholder holds a whole expression"
          }

          test "plain assignment RHS command form" {
              match ast "mvnHome = tool 'M3'" with
              | [ SAssign(EVar "mvnHome", ECall(FreeCall "tool", _, _)) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "a line break ends the command form — the next statement is not swallowed" {
              match ast "def x = foo\n'M3'" with
              | [ SDef("x", Some(EVar "foo")); SExpr(EStr "M3") ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "binary operators never read as command arguments" {
              match ast "def x = a - 1" with
              | [ SDef("x", Some(EBinary("-", _, _))) ] -> ()
              | other -> failtestf "subtraction was consumed: %A" other

              match ast "def y = 10 / 2" with
              | [ SDef("y", Some(EBinary("/", _, _))) ] -> ()
              | other -> failtestf "division was consumed: %A" other
          }

          test "a duplicate named argument is refused in expression position too" {
              Expect.isFalse (parses "def n = tool name: 'a', name: 'b'\n") "FG-174 reaches the new position"
          }

          test "string-named arguments in both call forms" {
              Expect.isTrue (parses "parallel 'UI Tests': { echo 'x' }, 'API': { echo 'y' }\n") "command form"
              Expect.isTrue (parses "parallel('UI Tests': { echo 'x' })\n") "parenthesised"
          }

          test "double-quoted constant names keep their decoded AST names in both call forms" {
              match ast "parallel(\"UI\\tTests\": { echo 'x' }, 'API': { echo 'y' })\n" with
              | [ SExpr(
                    ECall(
                        FreeCall "parallel",
                        [ ANamed("UI\tTests", EClosure _); ANamed("API", EClosure _) ],
                        None
                    )
                  ) ] -> ()
              | other -> failtestf "wrong parenthesised AST: %A" other

              match ast "parallel \"UI\\\"Tests\": { echo 'x' }, 'API': { echo 'y' }\n" with
              | [ SExpr(
                    ECall(
                        FreeCall "parallel",
                        [ ANamed("UI\"Tests", EClosure _); ANamed("API", EClosure _) ],
                        None
                    )
                  ) ] -> ()
              | other -> failtestf "wrong command-form AST: %A" other
          }

          test "all four quoted consumers share Java numeric escape decoding" {
              let source =
                  "def single = '\\b\\f\\u0041\\uu0042\\7\\77\\377\\400\\777'\n"
                  + "def tripleSingle = '''\\b\\f\\u0041\\uu0042\\7\\77\\377\\400\\777'''\n"
                  + "def double = \"\\b\\f\\u0041\\uu0042\\7\\77\\377\\400\\777\"\n"
                  + "def tripleDouble = \"\"\"\\b\\f\\u0041\\uu0042\\7\\77\\377\\400\\777\"\"\"\n"

              let expected = "\b\fAB\u0007?\u00ff 0?7"

              match ast source with
              | [ SDef("single", Some(EStr single))
                  SDef("tripleSingle", Some(EStr tripleSingle))
                  SDef("double", Some(EStr double))
                  SDef("tripleDouble", Some(EStr tripleDouble)) ] ->
                  Expect.equal single expected "single quoted"
                  Expect.equal tripleSingle expected "triple single quoted"
                  Expect.equal double expected "double quoted"
                  Expect.equal tripleDouble expected "triple double quoted"
              | other -> failtestf "numeric escapes did not stay plain strings: %A" other
          }

          test "numeric escapes also decode in constant names without creating interpolation" {
              match ast "f(\"\\b\\f\\u0041\\uu0042\\7\\77\\377\\400\\777\": 1)\n" with
              | [ SExpr(ECall(FreeCall "f", [ ANamed(name, EInt 1L) ], None)) ] ->
                  Expect.equal name "\b\fAB\u0007?\u00ff 0?7" "constant-name decoder"
              | other -> failtestf "wrong numeric constant-name AST: %A" other

              match ast "def value = \"\\044MISSING\"\n" with
              | [ SDef("value", Some(EStr "$MISSING")) ] -> ()
              | other -> failtestf "an octal dollar became interpolation: %A" other
          }

          test "slashy strings retain numeric-looking escapes literally" {
              match ast "def pattern = /\\b\\7\\77\\377/\n" with
              | [ SDef("pattern", Some(EStr value)) ] ->
                  Expect.equal value "\\b\\7\\77\\377" "slashy has delimiter-only escaping"
              | other -> failtestf "wrong slashy AST: %A" other
          }

          test "slashy strings refuse every raw physical line-ending spelling" {
              for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                  let source = "def value = /before" + newline + "after/\n"

                  Expect.isFalse
                      (parses source)
                      $"slashy raw {newlineLabel} crosses Fogell's supported single-line boundary"
          }

          test "ordinary strings refuse every raw physical line-ending spelling" {
              for quoteLabel, quote in [ "single", "'"; "double", "\"" ] do
                  for newlineLabel, newline in [ "LF", "\n"; "CRLF", "\r\n"; "bare CR", "\r" ] do
                      let source = "def value = " + quote + "before" + newline + "after" + quote + "\n"

                      Expect.isFalse
                          (parses source)
                          $"{quoteLabel}-quoted raw {newlineLabel} requires a triple-quoted multiline string"
          }

          test "all four quoted consumers remove each physical continuation spelling" {
              for label, ending in [ "LF", "\n"; "CRLF", "\r\n"; "CR", "\r" ] do
                  let sources =
                      [ "single", "def value = 'before\\" + ending + "after'\n"
                        "triple-single", "def value = '''before\\" + ending + "after'''\n"
                        "double", "def value = \"before\\" + ending + "after\"\n"
                        "triple-double", "def value = \"\"\"before\\" + ending + "after\"\"\"\n" ]

                  for consumer, source in sources do
                      match ast source with
                      | [ SDef("value", Some(EStr "beforeafter")) ] -> ()
                      | other -> failtestf "%s continuation in %s decoded incorrectly: %A" label consumer other
          }

          test "double-quoted named keys use the same escape decoding as string values" {
              match ast "f(\"line\\nname\": \"line\\nname\")\n" with
              | [ SExpr(ECall(FreeCall "f", [ ANamed(name, EStr value) ], None)) ] ->
                  Expect.equal name value "the key and value share the string decoder"
                  Expect.equal name "line\nname" "the escape is decoded, not retained as source text"
              | other -> failtestf "wrong AST: %A" other

              match ast "f(\"\\$branch\": 1)\n" with
              | [ SExpr(ECall(FreeCall "f", [ ANamed("$branch", EInt 1L) ], None)) ] -> ()
              | other -> failtestf "an escaped dollar is literal key text, got: %A" other
          }

          test "raw physical breaks are refused and line-continuation escapes stay fail-closed" {
              for source in
                  [ "f(\"line\nname\": 1)\n"
                    "f(\"line\rname\": 1)\n"
                    "f(\"line\r\nname\": 1)\n" ] do
                  Expect.isFalse (parses source) $"an ordinary double-quoted key is single-line: {source}"

              for source in
                  [ "f(\"line\\\nname\": 1)\n"
                    "f(\"line\\\rname\": 1)\n"
                    "f(\"line\\\r\nname\": 1)\n" ] do
                  Expect.isFalse
                      (parses source)
                      $"physical line-continuation escapes are an explicit fail-closed residual: {source}"

              match ast "def label = \"\"\"line\nname\"\"\"\n" with
              | [ SDef("label", Some(EStr "line\nname")) ] -> ()
              | other -> failtestf "triple-double multiline literal regressed: %A" other
          }

          test "interpolated double-quoted names remain refused" {
              for source in
                  [ "f(\"$branch\": 1)\n"
                    "f(\"${branch}\": 1)\n"
                    "f(\"prefix-${branch}\": 1)\n" ] do
                  Expect.isFalse (parses source) $"a GString is not a constant key: {source.Trim()}"
          }

          test "mixed-quote duplicate names keep the FG-174 refusal" {
              for source in
                  [ "f('same': 1, \"same\": 2)\n"
                    "f(\"same\": 1, 'same': 2)\n"
                    "f 'same': 1, \"same\": 2\n"
                    "f('$branch': 1, \"\\$branch\": 2)\n" ] do
                  Expect.isFalse (parses source) $"decoded names collide regardless of quote kind: {source.Trim()}"
          }

          test "an index assignment is not swallowed into a command call" {
              match ast "builds['a'] = { echo 'x' }" with
              | [ SAssign(EIndex(EVar "builds", _), EClosure _) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "typed function declarations, return type and parameter types erased" {
              let s = ast "void report(Maven mvn) { echo 'x' }"
              Expect.contains (Ast.definedFunctions s) "report" "typed decl recognised"
              let s2 = ast "def helmLint(String chart_dir) { echo 'x' }"
              Expect.contains (Ast.definedFunctions s2) "helmLint" "typed param in def decl"
          }

          test "a default parameter value is REFUSED, not silently dropped" {
              // `SFunc` cannot carry the value; admitting the declaration and
              // losing the default would fault a valid zero-arg call at runtime.
              Expect.isFalse (parses "def f(x = true) { echo 'x' }\n") "honest refusal until the AST can hold it"
          }

          test "a bare return does not swallow the next line's step" {
              // The verifier's construction on this diff: `if (skip) return`
              // then `sh 'make'` must run the sh exactly when Groovy does —
              // on the fall-through path, never on the return path.
              match ast "if (skip) return\nsh 'make'" with
              | [ SIf(_, [ SReturn None ], []); SExpr(ECall(FreeCall "sh", _, _)) ] -> ()
              | other -> failtestf "the return swallowed the step: %A" other
          }

          test "a return value on the return's own line still parses" {
              match ast "def f() {\n  return tool 'M3'\n}" with
              | [ SFunc("f", [], [ SReturn(Some(ECall(FreeCall "tool", _, _))) ]) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "an operator chains after a slashy literal across a space" {
              // The slashy was the one literal not lexeme-wrapped; `/a/ + x`
              // consumed the slashy and stopped dead at the operator.
              match ast "def m = /Deploy; / + env.TARGET" with
              | [ SDef("m", Some(EBinary("+", EStr "Deploy; ", EProp(EVar "env", "TARGET")))) ] -> ()
              | other -> failtestf "wrong AST: %A" other
          }

          test "a multi-assignment's source is evaluated ONCE" {
              // The lowering copied the source into one binding per target, so
              // an effectful RHS ran once per name — step effects duplicated,
              // names possibly bound from different results (Codex P1, PR #98;
              // the copy predates the command form and bit paren calls too).
              let o = run "def (a, b) = sh 'once'\n"
              Expect.equal (stepNames o) [ "sh" ] "one call, not one per target"
          }

          test "a multi-assignment still binds by index" {
              let o = run "def (a, b) = ['first', 'second']\necho a\necho b\n"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "first" ]; "echo", [ "second" ] ]
                  "each name reads its own index of the one evaluation"
          }

          test "a typed declaration cannot merge two lines" {
              // `echo msg` then `(x) { … }` is two statements; reading them as
              // a declaration of `msg` drops the echo — FG-187's class.
              match ast "echo msg\n(x) { echo 'y' }" with
              | SExpr(ECall(FreeCall "echo", _, _)) :: _ -> ()
              | other -> failtestf "merged into a declaration: %A" other
          } ]

/// FG-015. Keep one named semantic repro per recovered construct so a closure
/// audit cannot mistake grammar acceptance for execution parity. Spread-dot's
/// measured boundary has adjacent pins because null omission, null retention and
/// chained safe navigation are different semantics hidden behind one token.
let fg015ClosureAudit =
    let runStrictWith budget src =
        Interpreter.runStrictVars budget steps Env.empty (parseOk src)

    let runStrict = runStrictWith Budget.defaults

    testList
        "FG-015 six-construct closure audit"
        [ test "nested-quote GString keeps whitespace in one shell argument" {
              let o =
                  runStrict
                      "def who = 'alpha beta'\nsh \"printf '<%s>' \\\"${who}\\\" > nested-quote-gstring.txt\"\n"

              Expect.isNone o.Fault "the GString evaluates"

              Expect.equal
                  (stepArgs o)
                  [ "sh", [ "printf '<%s>' \"alpha beta\" > nested-quote-gstring.txt" ] ]
                  "quotes remain load-bearing around the whitespace-bearing value"
          }

          test "range is inclusive when used as a for-in source" {
              let o = runStrict "def seen = ''\nfor (i in 1..3) { seen = seen + i }\necho seen\n"
              Expect.isNone o.Fault "the range evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "123" ] ] "both endpoints are visited"
          }

          test "switch selects an arm and break leaves the switch" {
              let src =
                  "def selected = 'miss'\n"
                  + "switch ('b') {\n"
                  + "  case 'a': selected = 'a'; break\n"
                  + "  case 'b': selected = 'b'; break\n"
                  + "  default: selected = 'default'; break\n"
                  + "}\necho selected\n"

              let o = runStrict src
              Expect.isNone o.Fault "the switch evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "b" ] ] "the matching arm wins"
          }

          test "instanceof recognises a String" {
              let o = runStrict "def value = 'text'\nreturn value instanceof String\n"
              Expect.isNone o.Fault "the type check evaluates"
              Expect.equal o.Returned (Some(VBool true)) "String is recognised"
          }

          test "instanceof keeps the older wide integral surface Long-capable" {
              let o = runStrict "def value = 4294967296\nreturn value instanceof Long\n"
              Expect.isNone o.Fault "the large-literal type check evaluates"
              Expect.equal o.Returned (Some(VBool true)) "a provenance-specific producer does not narrow every VInt"
          }

          test "string indexing checks int64 bounds before narrowing the index" {
              let o = runStrict "return 'x'[4294967296]\n"
              Expect.isNone o.Fault "an out-of-range int64 index does not escape as a host exception"
              Expect.equal o.Returned (Some VNull) "the existing out-of-range result is preserved"
          }

          test "multi-assign binds list elements by index" {
              let o = runStrict "def (left, right) = ['L', 'R']\necho \"${left}:${right}\"\n"
              Expect.isNone o.Fault "the assignment evaluates"
              Expect.equal (stepArgs o) [ "echo", [ "L:R" ] ] "both names are bound"
          }

          test "spread-dot projects one property from every non-null list element" {
              let o = runStrict "def rows = [[name: 'a'], [name: 'b']]\nreturn rows*.name\n"
              Expect.isNone o.Fault "the measured list projection evaluates"
              Expect.equal o.Returned (Some(VList(ref [ VStr "a"; VStr "b" ]))) "source order is preserved"
          }

          test "spread-dot keeps the measured null and receiver boundaries" {
              let source =
                  "def withoutNulls = [[name: 'a'], [name: 'b']]*.name\n"
                  + "def withNullElement = [[name: 'a'], null, [name: 'b']]*.name\n"
                  + "def missingMapValues = [[name: 'a'], [:], [name: null]]*.name\n"
                  + "def maybe = null\n"
                  + "def nullReceiver = maybe*.name\n"
                  + "def groups = [[child: [name: 'a']], [child: null], [child: [name: 'b']]]\n"
                  + "def nested = groups*.child*.name\n"
                  + "def safeAfterSpread = groups*.child?.name\n"
                  + "def mapValue = [left: 1, right: 2]\n"
                  + "def mapResult = mapValue*.key\n"
                  + "echo withoutNulls\necho withNullElement\necho missingMapValues\n"
                  + "echo nullReceiver\necho nested\necho safeAfterSpread\necho mapResult\n"

              let o = runStrict source
              Expect.isNone o.Fault "all measured successful receiver shapes evaluate"

              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[a, b]" ]
                    "echo", [ "[a, b]" ]
                    "echo", [ "[a, null, null]" ]
                    "echo", [ "null" ]
                    "echo", [ "[a, b]" ]
                    "echo", [ "[a, b]" ]
                    "echo", [ "null" ] ]
                  "null receivers are omitted, null property values remain, and non-lists use ordinary lookup"
          }

          test "spread-dot missing properties remain catchable" {
              let source =
                  "def scalarResult = 'not-caught'\n"
                  + "try { def scalarProjection = [[name: 'a'], 42]*.name } "
                  + "catch (MissingPropertyException e) { scalarResult = 'caught' }\n"
                  + "def stringResult = 'not-caught'\n"
                  + "try { def stringProjection = 'ab'*.length } "

                  + "catch (MissingPropertyException e) { stringResult = 'caught' }\n"
                  + "echo scalarResult\necho stringResult\n"

              let o = runStrict source
              Expect.isNone o.Fault "both measured missing-property failures are intercepted"
              Expect.equal (stepArgs o) [ "echo", [ "caught" ]; "echo", [ "caught" ] ] "catch runs"
          }

          test "ordinary property access on a list stays ordinary" {
              let o = runStrict "def rows = [[name: 'a'], [name: 'b']]\nreturn rows.name\n"

              match o.Fault with
              | Some(UnknownProperty "name") -> ()
              | other -> failtestf "ordinary EProp gained spread semantics: %A" other
          }

          test "spread projection is bounded like other collection iteration" {
              let budget = { Budget.defaults with MaxLoopIterations = 1 }
              let o = runStrictWith budget "return [[name: 'a'], [name: 'b']]*.name\n"

              match o.Fault with
              | Some(BudgetExhausted message) -> Expect.stringContains message "spread projection" "names the bound"
              | other -> failtestf "expected bounded spread refusal, got %A" other
          }


          test "spread assignment refuses before its RHS and is not claimed catchable" {
              let source =
                  "def rows = [[name: 'a'], [name: 'b']]\n"
                  + "try { rows*.name = sh 'touch rhs.txt' } "
                  + "catch (Throwable e) { echo 'caught' }\n"
                  + "echo 'after'\n"

              let o = runStrict source

              match o.Fault with
              | Some(Unsupported why) ->
                  Expect.equal why Interpreter.spreadAssignmentRefusal "stable named boundary"
              | other -> failtestf "expected the defensive spread-assignment refusal, got %A" other

              Expect.isEmpty o.Effects "target, RHS, catch and later statements produce no effects"
          }

          test "direct projected indexes preserve temporary and source-backed mutation boundaries" {
              let source =
                  "def rows = [[child: ['a'], count: 1, children: [['a0', 'a1']]]]\n"
                  + "def projected = rows*.child\n"
                  + "rows*.child[0] = 'temporary'\n"
                  + "rows*.count[0] += 2\n"
                  + "rows*.count[0]++\n"
                  + "rows*.count[0]--\n"
                  + "rows*.children[0][1] = 'nested-direct'\n"
                  + "rows*.children.first()[0] = 'method-direct'\n"
                  + "echo rows[0].child\n"
                  + "echo projected\n"
                  + "echo rows[0].count\n"
                  + "echo rows[0].children\n"

              let o = runStrict source
              Expect.isNone o.Fault "the measured projected-index forms execute"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[a]" ]
                    "echo", [ "[[a]]" ]
                    "echo", [ "1" ]
                    "echo", [ "[method-direct, nested-direct]" ] ]
                  "writes to a fresh projection stay temporary; selected nested lists retain identity"
          }

          test "list index writes retain aliases, extend, and preserve compound evaluation order" {
              let source =
                  "def events = []\n"
                  + "def target = { xs -> events << 'receiver'; xs }\n"
                  + "def key = { events << 'index'; 0 }\n"
                  + "def rhs = { events << 'rhs'; 2 }\n"
                  + "def xs = [1, 10]\ndef alias = xs\n"
                  + "target(xs)[key()] += rhs()\n"
                  + "target(xs)[key()]++\n"
                  + "target(xs)[key()]--\n"
                  + "xs[-1] = 11\nxs[4] = 5\n"
                  + "echo xs\necho alias\necho events\n"

              let o = runStrict source
              Expect.isNone o.Fault "all typed integer writes execute"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[3, 11, null, null, 5]" ]
                    "echo", [ "[3, 11, null, null, 5]" ]
                    "echo", [ "[receiver, index, rhs, receiver, index, receiver, index]" ] ]
                  "receiver/key run once, RHS timing is retained, and aliases share the same list"
          }

          test "index assignment expressions retain Groovy's new-versus-old result values" {
              let plain = runStrict "def xs = [1]\nxs[0] = 7\n"
              Expect.isNone plain.Fault "plain write succeeds"
              Expect.equal plain.Returned (Some(VInt 7L)) "plain assignment returns the RHS"

              let compound = runStrict "def xs = [1]\nxs[0] += 2\n"
              Expect.isNone compound.Fault "compound write succeeds"
              Expect.equal compound.Returned (Some(VInt 3L)) "compound assignment returns the new value"

              let postfix = runStrict "def xs = [3]\nxs[0]++\n"
              Expect.isNone postfix.Fault "postfix write succeeds"
              Expect.equal postfix.Returned (Some(VInt 3L)) "postfix assignment returns the old value"

              let mutation = runStrict "def xs = [3]\nxs[0]++\necho xs\n"
              Expect.equal (stepArgs mutation) [ "echo", [ "[4]" ] ] "the receiver still mutates"
          }

          test "map index updates remain reference-backed map writes, including after spread reads" {
              let source =
                  "def m = [slot: 1]\ndef alias = m\n"
                  + "m['slot'] += 2\nm['slot']++\nm['slot']--\n"
                  + "def rows = [[holder: [slot: 'a']]]\n"
                  + "rows*.holder.first()['slot'] = 'spread-map'\n"
                  + "echo m\necho alias\necho rows[0].holder.slot\n"

              let o = runStrict source
              Expect.isNone o.Fault "the existing map writer remains available"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[slot:3]" ]; "echo", [ "[slot:3]" ]; "echo", [ "spread-map" ] ]
                  "map identity and spread-derived runtime receiver dispatch are preserved"
          }

          test "too-negative index faults at the measured RHS boundary and is catchable" {
              let source =
                  "def events = []\ndef xs = ['a']\n"
                  + "def rhs = { events << 'plain-rhs'; 'x' }\n"
                  + "try { xs[-2] = rhs() } catch (ArrayIndexOutOfBoundsException e) { events << 'plain-caught' }\n"
                  + "try { xs[-2] += (events << 'compound-rhs') } catch (ArrayIndexOutOfBoundsException e) { events << 'compound-caught' }\n"
                  + "echo xs\necho events\n"

              let o = runStrict source
              Expect.isNone o.Fault "both runtime exceptions are intercepted"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[a]" ]
                    "echo", [ "[plain-rhs, plain-caught, compound-caught]" ] ]
                  "plain assignment evaluates RHS first; compound update faults while reading the old value"
          }

          test "compound and postfix extension indexes preserve null-operation timing" {
              let source =
                  "def events = []\n"
                  + "def rhs = { events << 'compound-rhs'; 2 }\n"
                  + "def ints = [1]\n"
                  + "try { ints[1] += rhs() } catch (NullPointerException e) { events << 'compound-caught' }\n"
                  + "def strings = [1]\nstrings[1] += 'x'\n"
                  + "def postfix = []\n"
                  + "try { postfix[0]++ } catch (NullPointerException e) { events << 'postfix-caught' }\n"
                  + "echo ints\necho strings\necho postfix\necho events\n"

              let o = runStrict source
              Expect.isNone o.Fault "both NPE-class boundaries are catchable"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "[1]" ]
                    "echo", [ "[1, nullx]" ]
                    "echo", [ "[]" ]
                    "echo", [ "[compound-rhs, compound-caught, postfix-caught]" ] ]
                  "compound evaluates RHS before null arithmetic; postfix has no RHS and neither failed update mutates"
          }

          test "scalar String index updates read and evaluate RHS before the catchable write rejection" {
              let source =
                  "def events = []\n"
                  + "def receiver = { events << 'receiver'; 'ab' }\n"
                  + "def key = { events << 'index'; 0 }\n"
                  + "def rhs = { events << 'rhs'; 'X' }\n"
                  + "try { receiver()[key()] += rhs() } catch (SecurityException e) { events << 'plus-caught' }\n"
                  + "try { receiver()[key()] -= rhs() } catch (SecurityException e) { events << 'minus-caught' }\n"
                  + "try { receiver()[key()]++ } catch (SecurityException e) { events << 'inc-caught' }\n"
                  + "try { receiver()[key()]-- } catch (SecurityException e) { events << 'dec-caught' }\n"
                  + "try { receiver()[key()] = rhs() } catch (SecurityException e) { events << 'plain-caught' }\n"
                  + "echo events\n"

              let outcome = runStrict source
              Expect.isNone outcome.Fault "every sandbox rejection is caught by its measured SecurityException ancestry"
              Expect.equal
                  (stepArgs outcome)
                  [ "echo",
                    [ "[receiver, index, rhs, plus-caught, receiver, index, rhs, minus-caught, receiver, index, inc-caught, receiver, index, dec-caught, receiver, index, rhs, plain-caught]" ] ]
                  "receiver/index run once; compound and plain RHS effects precede the write fault"
          }

          test "scalar index read failures precede RHS while negative String indexes retain their split" {
              let source =
                  "def events = []\n"
                  + "try { 'ab'[9] += (events << 'positive-oob-rhs') } catch (StringIndexOutOfBoundsException e) { events << 'positive-oob-caught' }\n"
                  + "try { 'ab'[-3] += (events << 'negative-oob-rhs') } catch (ArrayIndexOutOfBoundsException e) { events << 'negative-oob-caught' }\n"
                  + "try { 'ab'['zero'] += (events << 'string-key-rhs') } catch (SecurityException e) { events << 'string-key-caught' }\n"
                  + "def integer = 7\ntry { integer[0] += (events << 'integer-rhs') } catch (SecurityException e) { events << 'integer-caught' }\n"
                  + "def booleanValue = true\ntry { booleanValue[0] += (events << 'boolean-rhs') } catch (SecurityException e) { events << 'boolean-caught' }\n"
                  + "def nullValue = null\ntry { nullValue[0] += (events << 'null-rhs') } catch (NullPointerException e) { events << 'null-caught' }\n"
                  + "try { 'ab'[-1] += (events << 'negative-valid-rhs') } catch (SecurityException e) { events << 'negative-valid-caught' }\n"
                  + "echo events\n"

              let outcome = runStrict source
              Expect.isNone outcome.Fault "all measured read/write faults are caught"
              Expect.equal
                  (stepArgs outcome)
                  [ "echo",
                    [ "[positive-oob-caught, negative-oob-caught, string-key-caught, integer-caught, boolean-caught, null-caught, negative-valid-rhs, negative-valid-caught]" ] ]
                  "only an in-range negative String read reaches its RHS; every failed read suppresses it"
          }

          test "spread index results support bounded property and safe-property writes" {
              let source =
                  "def rows = [[child: [name: 'a', count: 1]], [child: [name: 'b', count: 10]]]\n"
                  + "rows*.child[0].name = 'plain'\n"
                  + "rows*.child[0]?.name = 'safe'\n"
                  + "rows*.child[0].count += 2\n"
                  + "rows*.child[0].count++\n"
                  + "rows*.child[0].count--\n"
                  + "echo rows[0].child.name\n"
                  + "echo rows[1].child.name\n"
                  + "echo rows[0].child.count\n"
                  + "echo rows[1].child.count\n"

              let o = runStrict source
              Expect.isNone o.Fault "the measured outer writes execute"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "safe" ]; "echo", [ "b" ]; "echo", [ "3" ]; "echo", [ "10" ] ]
                  "only the selected first child mutates and all assignment forms compose"
          }

          test "safe-property assignment on a null indexed result is catchable and does not mutate" {
              let source =
                  "def rows = [[child: null], [child: [name: 'b']]]\n"
                  + "try { rows*.child[0]?.name = 'x' } catch (Exception e) { echo 'caught' }\n"
                  + "echo rows[0].child\n"
                  + "echo rows[1].child.name\n"

              let o = runStrict source
              Expect.isNone o.Fault "the null-target fault is intercepted"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "caught" ]; "echo", [ "null" ]; "echo", [ "b" ] ]
                  "catch runs and neither source child changes"
          }

          test "spread read through first supports an ordinary returned-child write" {
              let source =
                  "def rows = [[child: [name: 'a']], [child: [name: 'b']]]\n"
                  + "rows*.child.first().name = 'x'\n"
                  + "echo rows[0].child.name\n"
                  + "echo rows[1].child.name\n"

              let o = runStrict source
              Expect.isNone o.Fault "the method result is an ordinary map receiver"
              Expect.equal
                  (stepArgs o)
                  [ "echo", [ "x" ]; "echo", [ "b" ] ]
                  "only the child returned by first is mutated"
          }


          test "safe-property writes after method calls mutate maps for every assignment form" {
              let source =
                  "def rows = [[child: [name: 'a', count: 1]], [child: [name: 'b', count: 10]]]\n"
                  + "rows*.child.first()?.name = 'safe'\n"
                  + "rows*.child.first()?.count += 2\n"
                  + "rows*.child.first()?.count++\n"
                  + "rows*.child.first()?.count--\n"
                  + "echo \"${rows[0].child.name}:${rows[1].child.name}:${rows[0].child.count}:${rows[1].child.count}\"\n"

              let o = runStrict source
              Expect.isNone o.Fault "all safe-property assignment forms execute"
              Expect.equal (stepArgs o) [ "echo", [ "safe:b:3:10" ] ] "only first()'s returned map mutates"
          }

          test "safe-property null and missing receivers fault catchably after RHS effects" {
              let source =
                  "try { sh('null-target')?.name = sh('null-rhs') } "
                  + "catch (NullPointerException e) { echo 'null-caught' }\n"
                  + "def rows = [[child: 'text']]\n"
                  + "try { rows*.child.first()?.name = sh('missing-rhs') } "
                  + "catch (MissingPropertyException e) { echo 'missing-caught' }\n"

              let o = runStrict source
              Expect.isNone o.Fault "both exact runtime classes are intercepted"

              Expect.equal
                  (stepArgs o)
                  [ "sh", [ "null-target" ]
                    "sh", [ "null-rhs" ]
                    "echo", [ "null-caught" ]
                    "sh", [ "missing-rhs" ]
                    "echo", [ "missing-caught" ] ]
                  "receiver precedes RHS; the fault follows the RHS and never skips the catch"
          }

          test "narrow null and missing-property catches never intercept each other" {
              let nullThroughMissing =
                  runStrict (
                      "def target = null\n"
                      + "try { target?.name = 'x' } catch (MissingPropertyException e) { echo 'wrong' }\n"
                  )

              match nullThroughMissing.Fault with
              | Some(NullReceiverAssignment "name") -> ()
              | other -> failtestf "MissingPropertyException over-caught the null fault: %A" other

              Expect.isEmpty nullThroughMissing.Effects "the wrong catch did not run"

              let missingThroughNull =
                  runStrict (
                      "def target = 'text'\n"
                      + "try { target?.name = 'x' } catch (NullPointerException e) { echo 'wrong' }\n"
                  )

              match missingThroughNull.Fault with
              | Some(UnknownProperty "name") -> ()
              | other -> failtestf "NullPointerException over-caught the missing-property fault: %A" other

              Expect.isEmpty missingThroughNull.Effects "the wrong catch did not run"
          }

          test "unsupported assignment receivers and targets never degrade to RHS-only success" {
              let listOutcome = runStrict "def xs = [1]\nxs['zero'] = sh 'list-rhs'\necho 'after'\n"

              match listOutcome.Fault with
              | Some(Unsupported why) ->
                  Expect.equal why Interpreter.listIndexAssignmentRefusal "stable list-index boundary"
              | other -> failtestf "expected list-index refusal, got %A" other

              Expect.equal
                  (stepArgs listOutcome)
                  [ "sh", [ "list-rhs" ] ]
                  "plain assignment evaluates its RHS before Jenkins rejects the non-integer key; later effects stay blocked"

              let invalidTarget =
                  Interpreter.runStrictVars
                      Budget.defaults
                      steps
                      Env.empty
                      [ SAssign(EInt 1L, ECall(FreeCall "sh", [ APos(EStr "target-rhs") ], None)) ]

              match invalidTarget.Fault with
              | Some(Unsupported why) ->
                  Expect.equal why Interpreter.assignmentTargetRefusal "stable unsupported-target boundary"
              | other -> failtestf "expected target refusal, got %A" other

              Expect.isEmpty invalidTarget.Effects "an unsupported target cannot evaluate its RHS"
          }
          ]

/// FG-248. The escape grammar of a quoted scripted-Groovy literal, measured on
/// Jenkins 2.568.1 one transient job per (form, spelling): exactly nine simple
/// letters, unicode and octal decode; every other spelling is a compile
/// refusal there and a positioned admission refusal here. FG-124a already
/// pinned the numeric grammar; this list pins the letters and the refusal.
let fg248EscapeGrammar =
    let quotedForms (body: string) =
        [ "single", "'" + body + "'"
          "triple-single", "'''" + body + "'''"
          "double", "\"" + body + "\""
          "triple-double", "\"\"\"" + body + "\"\"\"" ]

    let expectRefusal (label: string) (spelling: char) (source: string) =
        let assertError route result =
            match result with
            | Ok _ -> failtestf "%s/%s admitted an invalid escape" label route
            | Error(e: AdmissionError) ->
                Expect.equal e.Code MalformedSyntax $"{label}/{route}: named admission code"
                Expect.equal e.Message (GroovyEscapes.invalidMessage spelling) $"{label}/{route}: the shared diagnostic"
                Expect.isGreaterThan e.Position.Line 0L $"{label}/{route}: positive line"
                Expect.isGreaterThan e.Position.Column 0L $"{label}/{route}: positive column"

        assertError "parse" (Fogell.Groovy.Parser.Parser.parse source)

        assertError
            "parseWithLimits"
            (Fogell.Groovy.Parser.Parser.parseWithLimits { Limits.defaults with MaxSourceBytes = 100_000 } source)

    testList
        "FG-248 quoted escape letters and the refusal of every other spelling"
        [ test "the nine measured letters decode identically in all four quoted forms" {
              let body = "[\\b\\f\\n\\t\\r\\\\\\'\\\"\\$]"
              let expected = "[\b\f\n\t\r\\'\"$]"

              for label, literal in quotedForms body do
                  match parseOk ("def v = " + literal + "\n") with
                  | [ SDef("v", Some(EStr value)) ] -> Expect.equal value expected label
                  | other -> failtestf "%s did not stay a plain string: %A" label other

              match parseOk "f(\"[\\b\\f\\n\\t\\r\\\\\\'\\\"\\$]\": 1)\n" with
              | [ SExpr(ECall(FreeCall "f", [ ANamed(name, EInt 1L) ], None)) ] ->
                  Expect.equal name expected "constant named-argument key"
              | other -> failtestf "wrong constant-name AST: %A" other
          }

          test "every measured-invalid spelling is refused by name in all four quoted forms" {
              for spelling in [ '/'; 's'; 'a'; 'e'; 'v'; 'x'; 'q'; 'z'; '8'; '9'; ' '; '{'; '('; '%' ] do
                  for label, literal in quotedForms ("[\\" + string spelling + "41]") do
                      expectRefusal (label + "/" + string spelling) spelling ("def v = " + literal + "\n")
          }

          test "a unicode escape without four hex digits is refused as backslash-u" {
              for label, literal in quotedForms "[\\uZZZZ]" @ quotedForms "[\\u]" @ quotedForms "[\\uu12]" do
                  expectRefusal label 'u' ("def v = " + literal + "\n")
          }

          test "the refusal names the offending character's position" {
              match Fogell.Groovy.Parser.Parser.parse "def ok = 'fine'\ndef v = '[\\q]'\n" with
              | Error e ->
                  Expect.equal e.Position.Line 2L "second statement"
                  Expect.equal e.Position.Column 12L "the character after the backslash"
              | Ok _ -> failtest "admitted"
          }

          test "constant named-argument keys, interpolated GStrings and closure bodies refuse the same spellings" {
              expectRefusal "constant name" 'q' "f(\"[\\q]\": 1)\n"
              expectRefusal "gstring tail" 's' "def v = \"${x} [\\s]\"\n"
              expectRefusal "gstring head" '/' "def v = \"[\\/] ${x}\"\n"
              expectRefusal "inside a closure" 'q' "node {\n    sh 'ok'\n    sh '[\\q]'\n}\n"
          }

          test "a constant named-argument key still fails closed on a backslash-newline without an escape diagnostic" {
              match Fogell.Groovy.Parser.Parser.parse "f(\"a\\\nb\": 1)\n" with
              | Error e ->
                  Expect.equal e.Code MalformedSyntax "refused"
                  Expect.isFalse (e.Message.StartsWith "invalid Groovy escape") "not misreported as an invalid letter"
              | Ok _ -> failtest "admitted a physical break in a constant name"
          }

          test "slashy strings keep every spelling literally except the delimiter escape" {
              match parseOk "def pattern = /[\\q\\s\\a\\8\\/]/\n" with
              | [ SDef("pattern", Some(EStr value)) ] -> Expect.equal value "[\\q\\s\\a\\8/]" "slashy"
              | other -> failtestf "wrong slashy AST: %A" other
          } ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "Fogell.Groovy" [ grammar; fg190192TriviaState; fg015ClosureAudit; fg015bSortAndRangeReview; fg205SortFallback; fg241RegexPatternFault; fg180Grammar; fg248EscapeGrammar; sandbox; budgets; semantics; predicateValues; stepValueUse; hostedSteps; scmMapValues; junitSummaryValues; callableResolution; mapIdentity; cyclicValues ])
