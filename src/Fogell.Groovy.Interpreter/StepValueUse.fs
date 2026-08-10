namespace Fogell.Groovy.Interpreter

open Fogell.Groovy

/// FG-160. Finds step calls whose VALUE is used.
///
/// WHY IT STILL EXISTS, restated after FG-172 — this header described the BATCH model
/// long after `script { }` stopped using it, which is the overclaim class this project
/// treats as a defect.
///
/// TWO MODES NOW. `Interpreter.run` is the batch consumer (`when { expression }`, the
/// tests): it collects `StepCall` effects and a call evaluates to `VNull`. `script { }`
/// uses `Interpreter.runHosted`, where the host performs each step LIVE and its return
/// value is the call's value.
///
/// The refusal survives the change because of the HOST, not the interpreter: the walker's
/// dispatch returns unit, so the host has no value to hand back and yields null anyway. So
///
///     def out = sh(script: 'git rev-parse HEAD', returnStdout: true)
///     if (out.startsWith('abc')) { … }
///
/// would still proceed with `out = null` and decide the branch wrongly — a silent wrong
/// answer, which ADR 0001 calls worse than explicit rejection. FG-174 makes `sh` honour
/// `returnStdout`/`returnStatus` and carries a value back; THAT is when this refusal can
/// be lifted, and not before.
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
    /// `callAnswers name hasReturnStdout hasReturnStatus` decides whether a call can
    /// supply a value at all. It is a PARAMETER, and it takes the flags rather than just
    /// the step name, because the answer depends on the COMBINATION — see
    /// `WalkerRules.returnContract`, which is the single definition both this refusal and
    /// the runtime publisher read. Passing a bare `honoursReturnFlags` predicate was the
    /// previous shape and it could not express precedence, which let the two ends resolve
    /// `returnStdout: true, returnStatus: true` differently.
    let find
        (isStep: string -> bool)
        (callAnswers: string -> bool -> bool -> bool)
        (script: Script)
        : Use list =
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
                // FG-174. A call that OPTS IN to returning a value is no longer refused:
                // `sh(returnStdout: true)` and `sh(returnStatus: true)` now carry one back
                // through the host. Everything else still yields null.
                //
                // LIFTED, on the third attempt, and the two earlier attempts are why this
                // is now believable rather than merely plumbed. Both times the refusal
                // went back on because measuring showed the value would be WRONG, which
                // is worse than absent:
                //   - Jenkins does NOT echo captured stdout; Fogell streamed it, so the
                //     compared output gained lines Jenkins never printed;
                //   - `result.Stdout` carried the `sh -x` TRACE, so `out` came back as
                //     "+ printf value\nvalue" instead of "value\n".
                // Both are closed at the source. The trace was never `sh`'s doing: the
                // process wrapper merged stderr into stdout with `2>&1`, and it now does
                // that only when stdout is NOT being captured. So a captured `sh` yields
                // the program's own output alone while the trace still streams — see
                // ProcessGroup's note, and receipts script-sh-returnstdout and
                // script-sh-returnstatus.
                //
                // The flag must be a LITERAL `true`. `sh(returnStdout: someVar)` cannot be
                // decided here — a static reader does not know what `someVar` holds — so
                // it stays refused rather than assumed, which is the direction that fails
                // safe. That is a property of THIS CALL's own arguments, visible without
                // guessing, unlike the env pre-scan that had to be replaced.
                let optsIn (flag: string) =
                    args
                    |> List.exists (fun a ->
                        match a with
                        | ANamed(k, EBool true) -> k = flag
                        | _ -> false)

                // WHETHER THE CALL ANSWERS IS NOT DECIDED HERE. The step and the
                // COMBINATION of flags decide it together, and that rule lives in one
                // place the runtime publisher reads too. Two findings came from deciding
                // it locally: the flags are not universal (`echo(returnStdout: true)`
                // returned a value where Jenkins returns null), and they are not
                // orthogonal (`returnStatus` wins when both are set).
                let returnsAValue (n: string) =
                    callAnswers n (optsIn "returnStdout") (optsIn "returnStatus")

                // The CALL ITSELF, only when its own value is consumed.
                (match target with
                 | FreeCall n when isStep n ->
                     if valuePos && not (returnsAValue n) then
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
