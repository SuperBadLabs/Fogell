namespace Fogell.Groovy.Interpreter

open Fogell.Groovy

/// FG-160. Finds step calls whose VALUE is used.
///
/// WHY THIS EXISTS BEFORE `script { }` RUNS ANYTHING. The interpreter is a BATCH model:
/// `Interpreter.run` collects `StepCall` effects for the host to perform later, and the
/// call evaluates to `VNull` on the spot. Order is preserved; VALUES are not. So
///
///     def out = sh(script: 'git rev-parse HEAD', returnStdout: true)
///     if (out.startsWith('abc')) { … }
///
/// would proceed with `out = null` and decide the branch wrongly — a silent wrong answer,
/// which ADR 0001 calls worse than explicit rejection. `script { }` is fail-closed at
/// runtime TODAY, so shipping the wiring without this check would be a REGRESSION from an
/// honest failure into a quiet one.
///
/// `when { expression { … } }` never needed this: `WalkerWhen` passes
/// `registeredSteps = Set.empty`, so `Sandbox.admitCall` refuses a step call outright
/// rather than returning null for it. Enabling steps for `script` is exactly what turns
/// that refusal into the hazard.
module StepValueUse =

    /// A step call found in a position where its value is consumed, with the name and a
    /// short description of the position — the receipt and the refusal both name it, so
    /// "why was my file rejected" has an answer more useful than a line number.
    type Use = { Step: string; Where: string }

    /// Every step call whose value is used, in source order.
    ///
    /// A bare `sh 'make'` as a STATEMENT is fine: Groovy discards the value and so does
    /// the batch model. It is the nested occurrences that lie — and the ARGUMENTS of a
    /// bare call are themselves value positions, so `echo sh(returnStdout: true, …)` is
    /// caught even though the outer call is a statement.
    let find (isStep: string -> bool) (script: Script) : Use list =
        let found = ResizeArray<Use>()

        let rec expr (where: string) (valuePos: bool) (e: Expr) =
            match e with
            | ENull
            | EBool _
            | EInt _
            | EStr _
            | EVar _ -> ()
            | EGString parts ->
                for p in parts do
                    match p with
                    | GLit _ -> ()
                    | GExpr x -> expr "a string interpolation" true x
            | EList xs -> for x in xs do expr where true x
            | EMap kvs -> for (_, v) in kvs do expr where true v
            | EProp(t, _)
            | ESafeProp(t, _) -> expr "a property read" true t
            | EIndex(t, i) ->
                expr "an index target" true t
                expr "an index" true i
            | EUnary(_, o) -> expr "an operand" true o
            | EBinary(_, l, r) ->
                expr "an operand" true l
                expr "an operand" true r
            | ETernary(c, a, b) ->
                expr "a condition" true c
                expr where true a
                expr where true b
            | EElvis(a, b) ->
                expr where true a
                expr where true b
            | EClosure c -> stmts c.Body
            | ECall(target, args, trailing) ->
                // The CALL ITSELF, only when its own value is consumed.
                (match target with
                 | FreeCall n when isStep n ->
                     if valuePos then
                         found.Add { Step = n; Where = where }
                 | FreeCall _ -> ()
                 | MethodCall(t, _)
                 | SafeMethodCall(t, _) -> expr "a method receiver" true t)

                // ARGUMENTS ARE ALWAYS VALUE POSITIONS, even for a bare statement call.
                for a in args do
                    match a with
                    | APos x -> expr "a call argument" true x
                    | ANamed(_, v) -> expr "a call argument" true v

                // A trailing block is STATEMENTS, not a value — `timeout(5) { sh 'x' }`
                // discards the inner call's value exactly as a bare statement does.
                trailing |> Option.iter (fun c -> stmts c.Body)

        and stmt (s: Stmt) =
            match s with
            // THE ONE NON-VALUE POSITION: a call as a statement discards its result.
            | SExpr e -> expr "a discarded expression" false e
            | SDef(_, v) -> v |> Option.iter (expr "a variable initialiser" true)
            | SAssign(t, v) ->
                expr "an assignment target" true t
                expr "an assignment" true v
            | SIf(c, a, b) ->
                expr "an if condition" true c
                stmts a
                stmts b
            | SForIn(_, src, body) ->
                expr "a for-in source" true src
                stmts body
            | SWhile(c, body) ->
                expr "a while condition" true c
                stmts body
            | SReturn e -> e |> Option.iter (expr "a return value" true)
            | SThrow e -> expr "a thrown value" true e
            | STry(body, catch, fin) ->
                stmts body
                catch |> Option.iter (fun (_, _, c) -> stmts c)
                stmts fin
            | SFunc(_, _, body) -> stmts body
            | SBreak
            | SContinue -> ()

        and stmts (xs: Stmt list) = for x in xs do stmt x

        stmts script
        List.ofSeq found
