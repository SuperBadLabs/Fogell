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

                // A trailing block is STATEMENTS, not a VALUE — so it is not recorded
                // here. It is not therefore RUNNABLE: see `findWrapperCalls` below, which
                // refuses it for an entirely different reason.
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

    /// FG-160. Step calls that carry a TRAILING BLOCK — `dir('x') { … }`,
    /// `timeout(5) { … }`, `withEnv([…]) { … }`.
    ///
    /// WHY THIS EXISTED, and what it means now. Under the BATCH model the interpreter
    /// executed a trailing closure immediately and flattened it into the same effect list,
    /// so a replayed `StepCall` arrived with no body — and the walker's wrappers are driven
    /// entirely by `step.Block`. `script { dir('x') { sh 'pwd' } }` ran `sh` in the STAGE
    /// root while reporting success.
    ///
    /// FG-172 replaced that: the body is handed over as a THUNK and `dir`, `timeout`,
    /// `retry` and `withEnv` run it inside the context they establish. So this finder is
    /// no longer "wrappers cannot work" — it is the guard for wrappers whose ARM has not
    /// been taught to accept a `HostedBody`, and the caller derives that set rather than
    /// hard-coding it.
    ///
    /// That is a wrong answer delivered quietly, so these are REFUSED. Raised by the
    /// pre-push verifier, which also caught the comment above claiming a trailing block
    /// was "perfectly runnable" — it is safe from the VALUE question and unsafe from this
    /// one, and one sentence cannot answer both.
    ///
    /// The batch-model fix once described here — a tree-structured `Effect` preserving
    /// nested bodies — was overtaken by the callback: there is no effect list to make a
    /// tree of any more.
    let findWrapperCalls (isStep: string -> bool) (script: Script) : Use list =
        let found = ResizeArray<Use>()

        let rec expr (e: Expr) =
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
                    | GExpr x -> expr x
            | EList xs -> for x in xs do expr x
            | EMap kvs -> for (_, v) in kvs do expr v
            | EProp(t, _)
            | ESafeProp(t, _) -> expr t
            | EIndex(t, i) ->
                expr t
                expr i
            | EUnary(_, o) -> expr o
            | EBinary(_, l, r) ->
                expr l
                expr r
            | ETernary(c, a, b) ->
                expr c
                expr a
                expr b
            | EElvis(a, b) ->
                expr a
                expr b
            | EClosure c -> stmts c.Body
            | ECall(target, args, trailing) ->
                // EITHER REPRESENTATION. A block can reach a call as `trailing` OR as a
                // closure ARGUMENT depending on which parser path matched, and a finder
                // that knows only one of them refuses half the shapes while believing it
                // covers all — measured: matching `trailing` alone found nothing for
                // `node { sh 'make' }`. Asking "is there a block anywhere in this call"
                // is representation-independent and errs toward refusing.
                let hasBlock =
                    trailing.IsSome
                    || args
                       |> List.exists (fun a ->
                           match a with
                           | APos(EClosure _) -> true
                           | ANamed(_, EClosure _) -> true
                           | _ -> false)

                (match target with
                 | FreeCall n when isStep n && hasBlock ->
                     found.Add
                         { Step = n
                           Where = "a trailing block, and this wrapper's arm cannot yet run one" }
                 | _ -> ())

                (match target with
                 | FreeCall _ -> ()
                 | MethodCall(t, _)
                 | SafeMethodCall(t, _) -> expr t)

                for a in args do
                    match a with
                    | APos x -> expr x
                    | ANamed(_, v) -> expr v

                trailing |> Option.iter (fun c -> stmts c.Body)

        and stmt (st: Stmt) =
            match st with
            | SExpr e -> expr e
            | SDef(_, v) -> v |> Option.iter expr
            | SAssign(t, v) ->
                expr t
                expr v
            | SIf(c, a, b) ->
                expr c
                stmts a
                stmts b
            | SForIn(_, src, body) ->
                expr src
                stmts body
            | SWhile(c, body) ->
                expr c
                stmts body
            | SReturn e -> e |> Option.iter expr
            | SThrow e -> expr e
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
