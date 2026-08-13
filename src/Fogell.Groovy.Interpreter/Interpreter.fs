namespace Fogell.Groovy.Interpreter

open Fogell.Groovy
open Fogell.Admission

/// A side effect the interpreter wants the host to perform. The interpreter
/// itself never touches the outside world — it *requests*. That is what makes
/// the sandbox structural rather than aspirational.
type Effect =
    | StepCall of name: string * positional: Value list * named: (string * Value) list

/// FG-160 slice 2. The host performing a step DURING evaluation, rather than receiving a
/// batch of requests afterwards.
///
/// WHY THE BATCH MODEL WAS THE WRONG BOUNDARY, measured over six review rounds on one
/// ticket: collecting `StepCall` effects and replaying them afterwards preserves ORDER and
/// loses everything else that crosses the boundary — a step's RETURN VALUE (the call
/// evaluates to null on the spot), a wrapper's BODY (the closure runs immediately and
/// flattens), `env` MUTATION (the host's overlay never sees it), and per-step DURABLE
/// identity (the journal only wraps the outer step). Each was refused in turn until the
/// supported surface was a fraction of `script { }`.
///
/// A callback makes all four ANSWERABLE at the source, which is not the same as answered.
/// WHAT IS TRUE TODAY, audited rather than asserted:
///   - a body executes in the host's own context — DONE, `dir`/`timeout`/`retry`;
///   - an `env` mutation is reported at the assignment — DONE, and the host currently
///     REFUSES it, because Fogell's overlay does not yet cross the script boundary;
///   - a return value CAN be returned — the walker's dispatch still yields unit, so the
///     host returns null and value uses stay refused;
///   - per-step journaling is POSSIBLE here — FG-171 is open; the hooks are keyed
///     `stage -> stepIndex` and a nested identity needs a journal format change.
/// The earlier wording claimed all four as delivered.
///
/// [RunBody] is present when the call had a trailing block; invoking it evaluates that
/// block, so a wrapper can set up its context, call it, and tear down.
type PerformStep =
    { Perform: string -> Value list -> (string * Value) list -> (unit -> unit) option -> Value
      /// `env.NAME = value` inside the script. The interpreter updates its own binding
      /// either way; this tells the host so that steps AFTER the block see it too, which
      /// is what Jenkins does and what the batch model could not express.
      SetEnv: string -> string -> unit
      /// FG-178. The host's environment AS IT IS NOW, asked for each time a hosted body
      /// is about to run.
      ///
      /// A wrapper can CHANGE the environment for its body — `withEnv(['TARGET=prod'])`
      /// is the whole point of the step — and the interpreter's `env` binding is built
      /// ONCE, before `runHosted`. So the body's Groovy read a pre-wrapper snapshot:
      /// measured, `withEnv(['TARGET=prod']) { sh "echo saw:${env.TARGET}" }` printed
      /// `saw:prod` on Jenkins and `saw:null` here. FG-172 delivered the wrapper's
      /// context to the body's STEPS; this delivers it to the body's EVALUATION, which is
      /// the half that was missing.
      ///
      /// A callback rather than a value because the answer differs per invocation: the
      /// host points at the wrapper's context for the body's duration and restores it
      /// afterwards, so only the host can say what the environment is at the moment the
      /// body runs.
      CurrentEnv: unit -> (string * string) list
      /// FG-184. Does this step take a BLOCK? Asked before a final closure argument is
      /// normalised into a hosted body.
      ///
      /// The interpreter cannot answer it. Which steps take a block is a property of the
      /// step VOCABULARY, which the host owns — `WalkerRules.scriptWrappersWithHostedBody`
      /// is the one place it is written down, and copying that set in here is how the
      /// three copies of the timestamp rule came to disagree.
      ///
      /// It is asked because normalising unconditionally was a FALSE SUCCESS: `def body =
      /// {}; sh('touch ran.txt', body)` had its closure stripped before validation, so the
      /// host saw a valid one-positional `sh` plus a body its dispatcher ignores, and the
      /// shell RAN. Jenkins rejects the two-argument call — measured, jenkins=failure with
      /// an EMPTY workspace against fogell=SUCCESS with the file written.
      TakesBlock: string -> bool }

/// Why evaluation stopped.
type Fault =
    | Denied of Denial
    | Unsupported of construct: string
    | Thrown of Value
    | BudgetExhausted of what: string
    /// A bare name bound nowhere, read under [Interpreter.runStrictVars]. Groovy
    /// throws MissingPropertyException here; the default mode's late-binding null
    /// is kept for consumers that model scripted Groovy's laxer contexts.
    | UnknownProperty of name: string

type Outcome =
    { Effects: Effect list
      Fault: Fault option
      Env: Env
      /// Names ASSIGNED into being without `def` — Groovy's Binding-field creation,
      /// the shape its "Did you forget the `def` keyword?" advisory fires on — in
      /// EXECUTION ORDER, because the advisories print as the assignments run and
      /// the lines are compared as output now: a set's name-sorted enumeration made
      /// `${z = 1; a = 2}` announce a before z, a false divergence. Each event
      /// carries the value AT CREATION: the advisory names that value's type, and
      /// `${x = 1; x = 'later'}` announces an Integer even though the binding ends
      /// as a String. A `def` declaration is a local and is NOT recorded here.
      NewBindings: (string * Value) list
      /// FG-048. The script's VALUE, when it has one — needed because
      /// `when { expression { … } }` is a predicate, not a side effect. Two
      /// shapes occur in the corpus and both must work: an explicit `return X`,
      /// and a bare trailing expression (Groovy's last-expression-is-the-value).
      /// None means the script produced no value, which is NOT the same as
      /// producing false, and a `when` must not treat it as such.
      Returned: Value option }

/// Evaluation budgets. An untrusted script must not be able to spin forever or
/// allocate without bound — the interpreter is on the admission path, so a
/// runaway loop is a denial-of-service on the controller.
type Budget =
    { MaxSteps: int
      MaxLoopIterations: int
      MaxCallDepth: int }

    static member defaults =
        { MaxSteps = 100_000
          MaxLoopIterations = 10_000
          MaxCallDepth = 64 }

module Interpreter =

    exception private Stop of Fault
    exception private ReturnSignal of Value
    exception private BreakSignal
    exception private ContinueSignal

    type private State =
        { mutable Steps: int
          mutable Depth: int
          mutable Effects: Effect list
          Budget: Budget
          RegisteredSteps: Set<string>
          /// None keeps the BATCH model — effects are collected and the call yields null.
          /// Existing consumers (`when { expression }`, the tests) rely on that and are
          /// deliberately unchanged; only a caller that supplies this gets live steps.
          Host: PerformStep option
          /// FG-178. Has the SCRIPT declared or assigned its own `env`?
          ///
          /// FOURTH SHAPE OF THIS FLAG, and the first that is a FACT rather than an
          /// inference. The refresh must not overwrite a user binding, and the question
          /// "is this binding the user's?" was answered twice by comparing VALUES:
          ///   - seeded per thunk from its CALL SITE, where `def env = …` had already run,
          ///     so the shadowed value was compared against itself and never detected;
          ///   - then anchored on state, which is SHARED, so one wrapper's refresh made
          ///     the next sibling look user-shadowed and its refresh was skipped —
          ///     reintroducing the staleness the whole thing exists to fix, in nested and
          ///     sequential wrappers.
          ///
          /// The interpreter does not need to infer any of it: it EXECUTES the `def`. This
          /// records that, and the refresh reads it. No value comparison, nothing to be
          /// coincidentally equal to, and nothing shared that should not be.
          ///
          /// SCOPE LIMIT, stated: a `def env` anywhere in the script disables the refresh
          /// for the whole run, where Groovy would scope it to its block. Fogell's
          /// variable model has no block scoping to hang that on — FG-179 — and erring
          /// toward NOT overwriting the user's binding is the safe direction.
          mutable EnvUserShadowed: bool
          Defined: Set<string>
          /// FG-048. The value of the most recent expression statement.
          ///
          /// REVIEW FIX (Codex, PR #13): Groovy returns the last expression of a
          /// closure even when earlier statements exist, but only a script that was
          /// EXACTLY one expression produced a value here. So
          /// `expression { def deploy = true; deploy }` returned None, which a
          /// `when` reads as unevaluable and fails the build on — where Jenkins
          /// simply runs the stage.
          mutable LastValue: Value option
          /// When set, reading a name bound nowhere faults with [UnknownProperty]
          /// instead of yielding null — Groovy's own behaviour for a GString in a
          /// step argument. Runtime enforcement is the ONLY correct place for it:
          /// laziness means no static scan can know which ternary arm is read.
          StrictVars: bool
          /// See [Outcome.NewBindings]; mutable so a FAULT still reports the
          /// assignments made before it — Groovy performed them. REVERSED order.
          mutable NewBindings: (string * Value) list
          /// Groovy's script BINDING: one shared, MUTABLE map, exactly as the real
          /// thing behaves. `Env.Vars` holds LOCALS only — `def`s, closure and loop
          /// parameters, catch variables — threaded functionally for lexical scope.
          /// The split is what earlier patch-on-patch state (a recovery snapshot, a
          /// declared-locals name set) approximated and kept getting wrong one
          /// closure at a time: an assignment to a non-local name mutates HERE and
          /// is immediately visible to every later expression, survives a fault,
          /// and never confuses a parameter for a binding.
          mutable Binding: Map<string, Value> }

    let private tick (st: State) =
        st.Steps <- st.Steps + 1

        if st.Steps > st.Budget.MaxSteps then
            raise (Stop(BudgetExhausted $"evaluation exceeded {st.Budget.MaxSteps} steps"))

    /// Method names the table dispatches WITHOUT reading `args` — zero-argument in
    /// Groovy too. Strict mode rejects a call that passes any: `'abc'.length(1)` has
    /// no such signature and Groovy throws, while a table that ignores args returned
    /// 3 and let a wrong expression reach a command line.
    let private zeroArgBuiltins =
        set
            [ "length"
              "size"
              "trim"
              "toUpperCase"
              "toLowerCase"
              "toString"
              "toInteger"
              "reverse"
              "first"
              "last"
              "isEmpty"
              "keySet"
              "values"
              "readLines" ]

    /// Methods whose only Groovy input is the trailing CLOSURE — a positional or
    /// named argument has no matching overload, and the table used to silently
    /// ignore it: `[1].any(123)` returned false where Groovy throws.
    let private closureBuiltins =
        set [ "each"; "collect"; "find"; "findAll"; "any"; "every"; "sort" ]


    let rec private evalExpr (st: State) (env: Env) (e: Expr) : Value =
        tick st

        match e with
        | ENull -> VNull
        | EBool b -> VBool b
        | EInt i -> VInt i
        | EStr s -> VStr s
        | EGString parts ->
            parts
            |> List.map (function
                | GLit s -> s
                | GExpr x -> Value.toDisplay (evalExpr st env x))
            |> String.concat ""
            |> VStr
        | EList xs -> VList(xs |> List.map (evalExpr st env))
        | EMap kvs -> VMap(kvs |> List.map (fun (k, v) -> k, evalExpr st env v) |> Map.ofList)
        | EVar n ->
            // locals shadow the binding; the binding shadows declared functions
            match Map.tryFind n env.Vars with
            | Some v -> v
            | None ->
                match Map.tryFind n st.Binding with
                | Some v -> v
                | None ->
                    match Map.tryFind n env.Funcs with
                    | Some(ps, body) -> VFunc(n, ps, body)
                    | None ->
                        if st.StrictVars then
                            // MEASURED (receipt `gstring-unresolved-property`): Groovy's
                            // property lookup FAILS the build for a name bound nowhere.
                            raise (Stop(UnknownProperty n))
                        else
                            VNull // Groovy resolves unknown bindings late; treat as null
        | ESafeProp(target, name) ->
            // Safe navigation: a NULL receiver short-circuits to null — no lookup,
            // no strict fault. A non-null receiver behaves exactly like [EProp],
            // strictness included.
            (match evalExpr st env target with
             | VNull -> VNull
             | recv -> evalProp st recv name)
        | EProp(target, name) -> evalProp st (evalExpr st env target) name
        | EIndex(target, idx) ->
            match evalExpr st env target, evalExpr st env idx with
            | VList xs, VInt i when i >= 0L && int i < xs.Length -> xs.[int i]
            | VMap m, VStr k -> defaultArg (Map.tryFind k m) VNull
            | VStr s, VInt i when i >= 0L && int i < s.Length -> VStr(string s.[int i])
            | _ -> VNull
        | EUnary(op, x) ->
            let v = evalExpr st env x

            match op with
            | "!" -> VBool(not (Value.isTruthy v))
            | "-" ->
                match v with
                | VInt i -> VInt(-i)
                | _ when st.StrictVars ->
                    raise (Stop(Unsupported "unary '-' is not modelled for this operand type"))
                | _ -> VNull
            | _ -> raise (Stop(Unsupported $"unary operator {op}"))
        | EBinary(op, l, r) -> evalBinary st env op l r
        | ETernary(c, a, b) ->
            if Value.isTruthy (evalExpr st env c) then
                evalExpr st env a
            else
                evalExpr st env b
        | EElvis(a, b) ->
            let v = evalExpr st env a
            if Value.isTruthy v then v else evalExpr st env b
        | EClosure c -> VClosure(c, env)
        | ECall(target, args, trailing) -> evalCall st env target args trailing

    and private evalProp (st: State) (recv: Value) (name: string) : Value =
        match recv with
        | VMap m -> defaultArg (Map.tryFind name m) VNull
        // MEASURED (receipt `gstring-string-property-fails`, Jenkins 2.568.1): the
        // sandbox REJECTS property access on a String — `${env.TARGET.length}` is
        // "No such field found: field java.lang.String length" and the build
        // FAILS; only the METHOD form `.length()` returns the count. The lenient
        // convenience below is therefore confined to non-strict consumers.
        | VList xs when not st.StrictVars && (name = "size" || name = "length") -> VInt(int64 xs.Length)
        | VStr s when not st.StrictVars && (name = "size" || name = "length") -> VInt(int64 s.Length)
        | _ when st.StrictVars -> raise (Stop(UnknownProperty name))
        | _ -> VNull

    and private evalBinary (st: State) (env: Env) op l r : Value =
        // short-circuit before evaluating the right side
        match op with
        | "&&" ->
            if Value.isTruthy (evalExpr st env l) then
                VBool(Value.isTruthy (evalExpr st env r))
            else
                VBool false
        | "||" ->
            if Value.isTruthy (evalExpr st env l) then
                VBool true
            else
                VBool(Value.isTruthy (evalExpr st env r))
        | _ ->

        let a = evalExpr st env l
        let b = evalExpr st env r

        match op, a, b with
        | "+", VInt x, VInt y -> VInt(x + y)
        | "+", VStr x, _ -> VStr(x + Value.toDisplay b)
        | "+", _, VStr y -> VStr(Value.toDisplay a + y)
        | "+", VList x, VList y -> VList(x @ y)
        | "-", VInt x, VInt y -> VInt(x - y)
        | "*", VInt x, VInt y -> VInt(x * y)
        | "/", VInt _, VInt 0L -> raise (Stop(Thrown(VStr "division by zero")))
        | "/", VInt x, VInt y when x % y = 0L -> VInt(x / y)
        | "/", VInt _, VInt _ when st.StrictVars ->
            // Groovy's `/` is DECIMAL: `1 / 2` renders `0.5`. This interpreter has no
            // decimal value, so a non-integral quotient must refuse — truncating to
            // VInt 0 sent `test 0 = 0.5` to a shell where Jenkins sends the truth.
            raise (Stop(Unsupported "non-integral division; Groovy decimals are not modelled"))
        | "/", VInt x, VInt y -> VInt(x / y)
        | "%", VInt _, VInt 0L -> raise (Stop(Thrown(VStr "division by zero")))
        | "%", VInt x, VInt y -> VInt(x % y)
        | "<<", VList x, _ -> VList(x @ [ b ])
        | "<<", VStr x, _ -> VStr(x + Value.toDisplay b)
        | "==", _, _ -> VBool(a = b)
        | "!=", _, _ -> VBool(a <> b)
        | "<", VInt x, VInt y -> VBool(x < y)
        | "<=", VInt x, VInt y -> VBool(x <= y)
        | ">", VInt x, VInt y -> VBool(x > y)
        | ">=", VInt x, VInt y -> VBool(x >= y)
        | ("=~" | "==~"), VStr s, VStr p ->
            // regex is evaluated with a hard timeout: an untrusted pattern is a
            // catastrophic-backtracking vector.
            try
                let re =
                    System.Text.RegularExpressions.Regex(
                        p,
                        System.Text.RegularExpressions.RegexOptions.None,
                        System.TimeSpan.FromMilliseconds 100.0)

                VBool(if op = "==~" then re.IsMatch s && re.Match(s).Length = s.Length else re.IsMatch s)
            with _ ->
                VBool false
        | "instanceof", _, VStr t ->
            VBool(
                match a, t with
                | VStr _, "String" -> true
                | VInt _, ("Integer" | "Long" | "Number") -> true
                | VList _, ("List" | "Collection" | "ArrayList") -> true
                | VMap _, ("Map" | "HashMap" | "LinkedHashMap") -> true
                | VBool _, "Boolean" -> true
                | _ -> false)
        | "..", VInt x, VInt y when y - x <= int64 st.Budget.MaxLoopIterations ->
            VList [ for i in x..y -> VInt i ]
        | "..", VInt _, VInt _ -> raise (Stop(BudgetExhausted "range exceeds the loop-iteration budget"))
        | _ when st.StrictVars ->
            // An operator on operand types this interpreter does not model. Groovy
            // THROWS for `1 - 'x'`; inventing null instead ran `deploy null` on a
            // green build — the same silent-wrong-command shape as the erased name
            // and the unmodelled method. Refuse by name.
            raise (Stop(Unsupported $"operator '{op}' is not modelled for these operand types"))
        | _ -> VNull

    and private evalCall (st: State) (env: Env) (target: CallTarget) (args: Arg list) (trailing: Closure option) : Value =
        tick st

        if st.Depth >= st.Budget.MaxCallDepth then
            raise (Stop(BudgetExhausted $"call depth exceeded {st.Budget.MaxCallDepth}"))

        // LAZY: a safe call on a null receiver short-circuits the WHOLE call, its
        // arguments included — `a?.m(sideEffect())` runs nothing when a is null.
        let positionalLazy =
            lazy (args |> List.choose (function APos e -> Some(evalExpr st env e) | ANamed _ -> None))

        let namedLazy =
            lazy (args |> List.choose (function ANamed(n, e) -> Some(n, evalExpr st env e) | APos _ -> None))

        match target with
        | SafeMethodCall(recv, name) ->
            // The whole call short-circuits: `env.OPTIONAL?.trim()` is null when the
            // receiver is, with the method never dispatched — not a property read
            // followed by a rejected `call`. Groovy's order: the RECEIVER evaluates
            // first (its side effects are performed and survive a later denial),
            // THEN the sandbox rules on the method — before the null test, so `?.`
            // is no doorway past it — and only then does a non-null receiver
            // dispatch.
            (match evalExpr st env recv with
             | VNull -> VNull // short-circuit: arguments never evaluate either
             | r ->
                 // Groovy's order on a non-null receiver: ARGUMENTS evaluate (their
                 // side effects are performed and survive a denial), THEN the
                 // sandbox rules, then dispatch.
                 let args = positionalLazy.Value
                 let named = namedLazy.Value

                 match Sandbox.admitMethod name with
                 | Error d -> raise (Stop(Denied d))
                 | Ok _ -> evalBuiltin st env name r args named trailing)
        | MethodCall(recv, name) ->
            // Same order as the safe-call arm: receiver, then arguments — their side
            // effects performed and kept — then the sandbox, then dispatch.
            let r = evalExpr st env recv
            let args = positionalLazy.Value
            let named = namedLazy.Value

            match Sandbox.admitMethod name with
            | Error d -> raise (Stop(Denied d))
            | Ok _ -> evalBuiltin st env name r args named trailing
        | FreeCall name ->
            // arguments evaluate BEFORE resolution can fail — Groovy performs their
            // side effects even when the call is then denied
            positionalLazy.Value |> ignore
            namedLazy.Value |> ignore

            match Sandbox.admitCall st.RegisteredSteps st.Defined name with
            | Error d -> raise (Stop(Denied d))
            | Ok(Step s) ->
                // ONE NORMALISED BODY, from either representation. A block reaches a call
                // as `trailing` OR as a FINAL CLOSURE ARGUMENT depending on which parser
                // path matched — `dir('sub') { … }` versus `dir 'sub', { … }` — and taking
                // it only from `trailing` meant the second spelling stringified its closure
                // into a positional argument and the wrapper then rejected itself as
                // body-less. `StepValueUse.findWrapperCalls` already had to learn this
                // exact lesson; the interpreter had not. Normalising HERE means every host
                // sees one shape instead of each host rediscovering the two.
                // FG-184. ONLY FOR A STEP THAT TAKES A BLOCK, and the restriction is the
                // fix. Normalising unconditionally REMOVED the closure before the host
                // could validate the call, so `def body = {}; sh('touch ran.txt', body)`
                // reached `hostedSignatureError` as a valid one-positional `sh` with a
                // hosted body its dispatcher ignores — and the shell ran. Jenkins rejects
                // the two-argument call: measured, jenkins=FAILURE with an empty workspace
                // against fogell=SUCCESS with the file written. A green build doing work
                // Jenkins refused, which is the ADR 0001 class.
                //
                // THE STATIC SCAN CANNOT COVER THIS, which is why the rule belongs here
                // rather than in another pre-flight arm: the argument is an `EVar`, so it
                // is only a closure at RUNTIME. A scan of the source sees a variable.
                //
                // Left in place for a step that takes no block, the closure stays a second
                // positional argument and the arity default-deny refuses the call — the
                // rule FG-177 already established, now actually reached.
                //
                // WITHOUT A HOST the question is unanswerable and the shape is unchanged.
                // That is not a second behaviour: the batch model never DISPATCHES a step
                // — the call yields VNull and the effect is only recorded — so there is no
                // call for the answer to be wrong about.
                let takesBlock =
                    match st.Host with
                    | Some h -> h.TakesBlock name
                    | None -> true

                let trailingArg, positionalArgs =
                    match trailing, List.rev positionalLazy.Value with
                    | None, VClosure(c, _) :: rest when takesBlock -> Some c, List.rev rest
                    | _ -> None, positionalLazy.Value

                let bodyClosure =
                    match trailing with
                    | Some c -> Some c
                    | None -> trailingArg

                // FG-178. THE BODY IS A CLOSURE, not a detached block, and the first
                // version of this thunk treated it as one. Three consequences, all
                // measured against Jenkins:
                //
                //   RETURN escaped it. `ReturnSignal` propagated past the thunk and ended
                //   the whole script, so `script { dir('sub') { return }; sh 'after' }`
                //   SKIPPED the following step where Jenkins runs it. The ordinary
                //   closure-call path below already catches this signal; the hosted path
                //   did not, which is the whole bug.
                //
                //   MUTATED CAPTURES were discarded. `execBlock` RETURNS the environment
                //   it produced and this ignored it, so `def n = 0; retry(2) { n = n + 1;
                //   … }` saw n = 1 on every attempt: Jenkins SUCCEEDS on the second
                //   attempt and Fogell failed on both. A retry that cannot make progress
                //   is not a retry. The cell carries the environment ACROSS invocations,
                //   which is what a closure does.
                //
                //   KNOWN LIMIT, stated rather than left to be discovered: variables
                //   DECLARED inside the body also persist between invocations, where
                //   Groovy would create them afresh. That is visible only to a body that
                //   both declares a name and depends on it being unset next time round —
                //   far narrower than losing every mutation, and the opposite direction
                //   from the defect it replaces.
                let bodyEnv = ref env

                let runBody =
                    bodyClosure
                    |> Option.map (fun c ->
                        fun () ->
                            st.Depth <- st.Depth + 1

                            // THE WRAPPER'S ENVIRONMENT, ASKED FOR NOW — but ONLY the
                            // `env` MAP, never the bare names.
                            //
                            // The first version rebound both, reasoning that refreshing
                            // one spelling and not the other is half a fix. That was
                            // wrong, and measured wrong: a bare name may be a LOCAL, and
                            // folding the environment over the body's bindings CLOBBERS
                            // it. `def TARGET = 'staging'; withEnv(['TARGET=prod']) { sh
                            // "printf ${TARGET}" }` gives Jenkins `staging` — the local
                            // SHADOWS the environment — and gave `prod` here. A defect I
                            // introduced while fixing the one above it, caught in review.
                            //
                            // Bare names cannot be refreshed safely without knowing which
                            // of them are environment-backed and which are locals, and
                            // this scope keeps no such provenance: at refresh time a name
                            // bound from `env` at script start and a name a `def`
                            // overwrote are indistinguishable. Guessing from equality
                            // ("refresh it if it still looks like the old value") is the
                            // kind of cleverness that fails silently on a local that
                            // happens to match.
                            //
                            // RESIDUAL LIMIT, stated: a BARE name inside a wrapper does
                            // not see the wrapper's override — `env.NAME` does, and the
                            // shell sees it through the overlay either way. Narrower than
                            // clobbering locals, and in the safe direction. FG-179 carries
                            // the environment model that would settle both.
                            // AND `env` ITSELF CAN BE SHADOWED. Removing the bare-name
                            // clobbering left one binding still overwritten
                            // unconditionally, and it is a legal local:
                            // `def env = [TARGET: 'local']` gives Jenkins `local` and gave
                            // `wrapper` here. Second instance of the same class inside two
                            // rounds, which says the fix was aimed at the SYMPTOM (bare
                            // names) rather than the rule (never overwrite a user binding).
                            //
                            // PROVENANCE BY LAST-WRITE, which this scope can actually
                            // establish: refresh `env` only while it still holds the value
                            // this refresh last put there (or the one `runHosted` bound).
                            // A `def env = …` makes it differ, so it is left alone. That is
                            // not the equality GUESS rejected for bare names — there the
                            // candidate was a scalar that could coincide by accident; here
                            // it is a whole environment map compared against a value this
                            // code itself wrote, which a user local does not reproduce.
                            st.Host
                            |> Option.iter (fun h ->
                                let current = h.CurrentEnv() |> List.map (fun (k, v) -> k, VStr v)
                                let fresh = VMap(Map.ofList current)

                                if not st.EnvUserShadowed then
                                    bodyEnv.Value <-
                                        { bodyEnv.Value with Vars = Map.add "env" fresh bodyEnv.Value.Vars })

                            // NAMES THE BODY DECLARES DO NOT SURVIVE IT; mutations to
                            // names it CAPTURED do.
                            //
                            // Carrying the whole environment forward was the previous
                            // shape, and I recorded the gap as a known limit: a `def`
                            // inside the body persisted into the next invocation where
                            // Groovy creates it afresh. Review then MEASURED it, and
                            // receipt `script-body-local-does-not-persist` holds it — a
                            // `retry(2)` whose first attempt declares `marker` and whose
                            // second reads it FAILS on Jenkins and succeeded here — which
                            // is the reminder that writing a limit down does not make it
                            // acceptable, it only makes it honest.
                            //
                            // The split needs no scope machinery: a name present BEFORE
                            // the invocation is captured, a name present only after was
                            // declared by the body. Keeping the first and dropping the
                            // second is exactly Groovy's behaviour for this case. Proper
                            // block scoping — which would also fix `def` inside `if` and
                            // friends — is FG-179's variable model, and this does not
                            // pretend to be it.
                            let captured =
                                bodyEnv.Value.Vars |> Map.toSeq |> Seq.map fst |> Set.ofSeq

                            try
                                try
                                    let after = execBlock st bodyEnv.Value c.Body

                                    bodyEnv.Value <-
                                        { after with
                                            Vars = after.Vars |> Map.filter (fun k _ -> captured.Contains k) }
                                with ReturnSignal _ ->
                                    // A closure-local return ends the BODY. The value is
                                    // dropped because a wrapper does not consume one:
                                    // `dir('x') { return 5 }` is not `dir` returning 5.
                                    ()
                            finally
                                st.Depth <- st.Depth - 1)

                match st.Host with
                | Some host ->
                    // LIVE: the host performs it here and its value is the call's value.
                    // The body is handed over UNEVALUATED so a wrapper can run it inside
                    // whatever context it establishes — the batch model evaluated it
                    // immediately and flattened it, which is how `dir('x') { … }` lost its
                    // directory.
                    host.Perform s positionalArgs namedLazy.Value runBody
                | None ->
                    // BATCH: a step is a request, not something we perform.
                    //
                    // `positionalArgs`, NOT `positionalLazy.Value` — the NORMALISED list.
                    // This recorded the raw one, so `dir 'sub', { … }` logged the trailing
                    // closure as a positional argument AND ran it through `runBody` below:
                    // the same block counted twice, once as data and once as an effect.
                    // The comment above says normalising here means every host sees ONE
                    // shape, and then this branch did not use it — raised in review on
                    // PR #53. No walker path was affected, since only batch consumers read
                    // `Effects`; a future one would have inherited the defect.
                    st.Effects <- StepCall(s, positionalArgs, namedLazy.Value) :: st.Effects
                    runBody |> Option.iter (fun run -> run ())
                    VNull
            | Ok(Builtin b) ->
                match Map.tryFind b env.Funcs with
                | Some(ps, body) ->
                    st.Depth <- st.Depth + 1

                    let callEnv =
                        List.zip (List.truncate positionalLazy.Value.Length ps) (List.truncate ps.Length positionalLazy.Value)
                        |> List.fold (fun acc (p, v) -> Env.withVar p v acc) env

                    let result =
                        try
                            execBlock st callEnv body |> ignore
                            VNull
                        with ReturnSignal v ->
                            v

                    st.Depth <- st.Depth - 1
                    result
                | None -> evalBuiltin st env b VNull positionalLazy.Value namedLazy.Value trailing

    and private evalBuiltin
        (st: State)
        (env: Env)
        (name: string)
        (recv: Value)
        (args: Value list)
        (namedArgs: (string * Value) list)
        (trailing: Closure option)
        : Value =
        // NAMED arguments count too: Groovy folds them into a Map argument, so
        // `'abc'.length(foo: 1)` is `String.length(Map)` — no such signature, throw.
        if
            st.StrictVars
            && (Set.contains name zeroArgBuiltins || Set.contains name closureBuiltins)
            && not (List.isEmpty args && List.isEmpty namedArgs)
        then
            raise (Stop(Unsupported $"method '{name}' does not accept {List.length args + List.length namedArgs} argument(s)"))

        let applyClosure (c: Closure) (closureEnv: Env) (item: Value) =
            st.Depth <- st.Depth + 1

            let bound =
                match c.Params with
                | [] -> Env.withVar "it" item closureEnv
                | p :: _ -> Env.withVar p item closureEnv

            let r =
                try
                    // REVIEW FIX (Codex, PR #13 round 3): the result was discarded and
                    // VNull returned unless the closure used an explicit `return`. So
                    // `[1].any { it == 1 }` was FALSE and skipped a stage Groovy —
                    // and therefore Jenkins — evaluates as true. Groovy's implicit
                    // closure return is the trailing expression, and LastValue is
                    // saved/restored around the call so an inner closure cannot
                    // clobber the enclosing block's trailing value.
                    // REVIEW FIX (Copilot, PR #14): the restore only happened on the
                    // NON-return path, so a closure using an explicit `return` still
                    // clobbered the enclosing block's trailing value. try/finally, so
                    // both exits restore it.
                    let outer = st.LastValue

                    try
                        st.LastValue <- None
                        execBlock st bound c.Body |> ignore
                        defaultArg st.LastValue VNull
                    finally
                        st.LastValue <- outer
                with ReturnSignal v ->
                    v

            st.Depth <- st.Depth - 1
            r

        match name, recv, args with
        | "size", VList xs, _
        | "length", VList xs, _ -> VInt(int64 xs.Length)
        | "size", VStr s, _
        | "length", VStr s, _ -> VInt(int64 s.Length)
        | "size", VMap m, _ -> VInt(int64 m.Count)
        | "isEmpty", VList xs, _ -> VBool(List.isEmpty xs)
        | "isEmpty", VStr s, _ -> VBool(s = "")
        | "toString", v, _ -> VStr(Value.toDisplay v)
        | "toInteger", VStr s, _ ->
            match System.Int64.TryParse s with
            | true, i -> VInt i
            | _ when st.StrictVars ->
                // Groovy throws NumberFormatException here — a failed conversion is
                // a FAULT, not the value null.
                raise (Stop(Thrown(VStr $"NumberFormatException: For input string: \"{s}\"")))
            | _ -> VNull
        | "trim", VStr s, _ -> VStr(s.Trim())
        | "toUpperCase", VStr s, _ -> VStr(s.ToUpperInvariant())
        | "toLowerCase", VStr s, _ -> VStr(s.ToLowerInvariant())
        | "startsWith", VStr s, [ VStr p ] -> VBool(s.StartsWith p)
        | "endsWith", VStr s, [ VStr p ] -> VBool(s.EndsWith p)
        | "contains", VStr s, [ VStr p ] -> VBool(s.Contains p)
        | "contains", VList xs, [ v ] -> VBool(List.contains v xs)
        | "replace", VStr s, [ VStr a; VStr b ] -> VStr(s.Replace(a, b))
        | ("split" | "tokenize"), VStr s, [ VStr d ] ->
            VList(s.Split(d) |> Array.toList |> List.map VStr)
        | "readLines", VStr s, _ ->
            VList(s.Replace("\r\n", "\n").Split('\n') |> Array.toList |> List.map VStr)
        | "join", VList xs, [ VStr d ] -> VStr(xs |> List.map Value.toDisplay |> String.concat d)
        | "reverse", VList xs, _ -> VList(List.rev xs)
        | "reverse", VStr s, _ -> VStr(System.String(s.ToCharArray() |> Array.rev))
        | "sort", VList xs, _ -> VList(List.sortWith compare xs)
        | "first", VList(x :: _), _ -> x
        | "last", VList xs, _ when not xs.IsEmpty -> List.last xs
        | "keySet", VMap m, _ -> VList(m |> Map.toList |> List.map (fst >> VStr))
        | "values", VMap m, _ -> VList(m |> Map.toList |> List.map snd)
        | "each", VList xs, _ ->
            match trailing with
            | Some c ->
                if xs.Length > st.Budget.MaxLoopIterations then
                    raise (Stop(BudgetExhausted "each exceeds the loop-iteration budget"))

                xs |> List.iter (fun x -> applyClosure c env x |> ignore)
                recv
            | None -> recv
        | "collect", VList xs, _ ->
            match trailing with
            | Some c -> VList(xs |> List.map (applyClosure c env))
            | None -> recv
        | ("find" | "findAll"), VList xs, _ ->
            match trailing with
            | Some c ->
                let matches = xs |> List.filter (fun x -> Value.isTruthy (applyClosure c env x))
                if name = "find" then (match matches with [] -> VNull | h :: _ -> h) else VList matches
            | None -> recv
        | "any", VList xs, _ ->
            match trailing with
            | Some c -> VBool(xs |> List.exists (fun x -> Value.isTruthy (applyClosure c env x)))
            | None -> VBool false
        | "every", VList xs, _ ->
            match trailing with
            | Some c -> VBool(xs |> List.forall (fun x -> Value.isTruthy (applyClosure c env x)))
            | None -> VBool true
        | _ when st.StrictVars ->
            // Fail CLOSED on a method this interpreter does not model. Groovy would
            // evaluate `${env.TARGET.substring(3)}`; stringifying the old wildcard's
            // null instead ran `deploy null` with the build green — the same
            // silently-wrong-command shape as the erased unknown name. The bounded
            // vocabulary is a MODELLING limit, so the honest outcome is a refusal
            // that names the method, not an invented value.
            raise (Stop(Unsupported $"method '{name}' is not modelled by the bounded interpreter"))
        | _ -> VNull

    and private execStmt (st: State) (env: Env) (s: Stmt) : Env =
        tick st

        match s with
        | SExpr e ->
            st.LastValue <- Some(evalExpr st env e)
            env
        // REVIEW FIX (Codex, PR #13 round 4): only SExpr updated LastValue, so a
        // predicate whose final statement is an assignment — `def deploy = true
        // deploy = false` — produced no value and FAILED the build as unevaluable,
        // where Groovy assignments are value-producing and Jenkins reads it as false.
        | SDef(n, Some e) ->
            let v = evalExpr st env e
            st.LastValue <- Some v
            // FG-178. The script declaring its OWN `env` is the fact the hosted refresh
            // needs; recorded where it happens rather than inferred later from values.
            if n = "env" then st.EnvUserShadowed <- true
            Env.withVar n v env
        // REVIEW FIX (Codex, PR #14 round 6): value tracking was added only for
        // INITIALISED declarations, so `[1].any { true; def x }` left `true` in
        // LastValue and the closure returned true. An uninitialised declaration
        // evaluates to null.
        | SDef(n, None) ->
            st.LastValue <- Some VNull
            if n = "env" then st.EnvUserShadowed <- true
            Env.withVar n VNull env
        | SAssign(EVar n, v) ->
            let value = evalExpr st env v
            st.LastValue <- Some value
            // Assignment counts too: `env = [...]` replaces the binding as surely as a
            // `def` does, and covering only `def` would leave the commoner spelling open —
            // the partial-enumeration shape this branch keeps finding.
            if n = "env" then st.EnvUserShadowed <- true

            if Map.containsKey n env.Vars then
                // a LOCAL (def, parameter, loop/catch variable): lexical update,
                // the shared binding never sees it
                Env.withVar n value env
            else
                // the script BINDING: created on first assignment (the advisory
                // case), mutated in place — immediately visible everywhere,
                // including after this closure returns and after a later fault
                if not (Map.containsKey n st.Binding) then
                    st.NewBindings <- (n, value) :: st.NewBindings

                st.Binding <- Map.add n value st.Binding
                env
        // REVIEW FIX (Codex, PR #14 round 7): only the EVar form recorded a value, so
        // a predicate ending in `env.DEPLOY = false` or `values[0] = false` reached
        // here and left LastValue absent or STALE — reported unevaluable, or worse
        // reusing an earlier truthy value. Groovy assignments yield their RHS whatever
        // the target shape is.
        | SAssign(target, v) ->
            evalExpr st env target |> ignore
            let value = evalExpr st env v
            st.LastValue <- Some value

            // FG-172. `env.X = …` IS TOLD TO THE HOST, at the assignment itself.
            //
            // A STATIC PRE-SCAN CANNOT DO THIS JOB. The `findEnvMutations` scanner this
            // replaces walked statements
            // and missed `[1].each { env.FOO = 'x' }`, because the assignment lives inside
            // a closure the scanner did not enter — and closure and control-flow shapes
            // will keep escaping any incomplete scan of a dynamic language. The pre-scan
            // was replaced by this, which sees every assignment that actually EXECUTES,
            // whatever syntax reached it. Raised by the pre-push verifier as the fifth of
            // its class, with exactly that argument.
            //
            // The host decides what it means: today it fails the build, because Fogell's
            // environment overlay does not yet cross the script boundary. That refusal is
            // now COMPLETE rather than best-effort.
            match st.Host, target with
            | Some host, EProp(EVar "env", name) -> host.SetEnv name (Value.toDisplay value)
            | Some host, EIndex(EVar "env", idx) ->
                // A COMPUTED index too: `env["FOO$suffix"] = …` is the same mutation, and
                // the pre-scan only recognised a literal string.
                host.SetEnv (Value.toDisplay (evalExpr st env idx)) (Value.toDisplay value)
            | _ -> ()

            env
        // REVIEW FIX (Codex, PR #14 round 3): `[1].any { true; if (false) { false } }`
        // returned TRUE, because `true` set the trailing value and the untaken `if`
        // left it there. The trailing value belongs to the FINAL statement, so a
        // statement producing nothing CLEARS it instead of inheriting what ran before.
        //
        // The first attempt at this edit silently did nothing — it matched
        // `SIf(cond, thenBranch, elseBranch)` while the code says `SIf(c, t, f)` — and
        // the build passed unchanged. Only the test caught it.
        | SIf(c, t, f) ->
            st.LastValue <- None

            if Value.isTruthy (evalExpr st env c) then
                execBlock st env t
            else
                execBlock st env f
        | SForIn(v, src, body) ->
            st.LastValue <- None

            match evalExpr st env src with
            | VList xs ->
                if xs.Length > st.Budget.MaxLoopIterations then
                    raise (Stop(BudgetExhausted "loop exceeds the iteration budget"))

                let mutable cur = env

                for x in xs do
                    try
                        cur <- execBlock st (Env.withVar v x cur) body
                    with
                    | ContinueSignal -> ()
                    | BreakSignal -> ()

                cur
            | _ -> env
        | SWhile(c, body) ->
            st.LastValue <- None

            let mutable cur = env
            let mutable iterations = 0
            let mutable running = true

            while running && Value.isTruthy (evalExpr st cur c) do
                iterations <- iterations + 1

                if iterations > st.Budget.MaxLoopIterations then
                    raise (Stop(BudgetExhausted $"while loop exceeded {st.Budget.MaxLoopIterations} iterations"))

                try
                    cur <- execBlock st cur body
                with
                | ContinueSignal -> ()
                | BreakSignal -> running <- false

            cur
        // FG-183. A SWITCH IS A BREAK BOUNDARY, which is the whole reason this node exists
        // rather than a lowering to nested ifs. `BreakSignal` is caught HERE, so an arm's
        // `break` leaves the switch and execution continues after it — including when the
        // switch sits inside a loop, where the lowered form let the signal reach the loop
        // handler and silently leave the LOOP instead. `ContinueSignal` is deliberately NOT
        // caught: `continue` belongs to an enclosing loop and must pass straight through.
        //
        // FALLTHROUGH IS REAL. Groovy runs from the matching arm ONWARD until a `break`, so
        // the arms are executed as a sequence starting at the match rather than as one
        // isolated body. The nested-if lowering could not express this at all — its
        // branches are mutually exclusive — and recorded it as a known gap justified by the
        // corpus rather than by the language.
        | SSwitch(subject, arms) ->
            st.LastValue <- None
            let v = evalExpr st env subject

            // `default` matches by POSITION, not by precedence: it is chosen only when no
            // case matches, and fallthrough then continues from where it sits in source
            // order. Hoisting it to the end would run the wrong arms after it.
            let entry =
                arms
                |> List.tryFindIndex (fun (k, _) ->
                    match k with
                    // The SAME equality `==` uses (line 340), not a second opinion: a
                    // switch that matched by a different rule than the operator would be
                    // its own defect, and the lowered form got this right by construction
                    // because it literally built an `EBinary("==", …)`.
                    | Some case -> v = evalExpr st env case
                    | None -> false)
                |> Option.orElseWith (fun () -> arms |> List.tryFindIndex (fst >> Option.isNone))

            match entry with
            | None -> env
            | Some i ->
                // STATEMENT BY STATEMENT, NOT `execBlock` PER ARM, and the difference is a
                // measured wrong answer rather than a style choice. `execBlock` folds a
                // whole body and RETURNS the environment, so a `break` part-way through
                // unwinds past the return and every assignment the arm had already made is
                // discarded. Measured against Jenkins with the arm-level fold:
                // `case 'b': log = log + 'B'; break` gave `log:[abDz]` where Jenkins gives
                // `log:[aBbDz]`, and the fallthrough arm gave `fall:[P]` against `PQ` —
                // the value was computed and then thrown away by the unwind.
                //
                // KNOWN LIMIT, stated rather than left to be found: an assignment made
                // inside a NESTED block before a break — `case 'a': if (x) { y = 1; break }`
                // — is still lost, because that block is itself an `execBlock` whose return
                // the unwind skips. `SWhile` has had the identical shape all along. Both
                // dissolve when FG-179 makes variables ref cells, which is where the fix
                // belongs; a second partial-env mechanism here would be the ninth guard
                // that ticket exists to stop.
                let mutable cur = env

                try
                    for _, body in List.skip i arms do
                        for s in body do
                            cur <- execStmt st cur s
                with BreakSignal ->
                    ()

                cur
        | SReturn e -> raise (ReturnSignal(match e with Some x -> evalExpr st env x | None -> VNull))
        | SBreak -> raise BreakSignal
        | SContinue -> raise ContinueSignal
        | SThrow e -> raise (Stop(Thrown(evalExpr st env e)))
        | STry(body, catch, fin) ->
            // MissingPropertyException is CATCHABLE — `try { MISSING } catch (e)`
            // renders the fallback on Jenkins — and `finally` runs whether or not
            // the fault is caught, Groovy's contract.
            // the body executes statement by statement so a fault hands the CATCH
            // the environment at the throw point — `x = 'after'; MISSING` must show
            // the handler 'after', not the pre-try snapshot
            let mutable cur = env

            let handle v =
                match catch with
                | Some(_, binding, handler) ->
                    let e2 =
                        match binding with
                        | Some n -> Env.withVar n v cur
                        | None -> cur

                    execBlock st e2 handler
                | None -> cur

            // Which declared types can intercept a MissingPropertyException. Its
            // Groovy ancestry: MissingPropertyException < GroovyRuntimeException <
            // RuntimeException < Exception < Throwable. `catch (name)` with no type
            // defaults to Exception — compatible. `catch (ArithmeticException e)`
            // is NOT, and must let the fault escape.
            let catchesMissingProperty =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    [ "Exception"; "Throwable"; "RuntimeException"; "GroovyRuntimeException"; "MissingPropertyException" ]
                    |> List.contains t
                | None -> false

            let afterTry =
                try
                    try
                        for stmt in body do
                            cur <- execStmt st cur stmt

                        cur
                    with
                    | Stop(Thrown v) -> handle v
                    | Stop(UnknownProperty n) when catchesMissingProperty ->
                        handle (VStr $"groovy.lang.MissingPropertyException: No such property: {n}")
                with e ->
                    // uncaught: finally still runs, then the fault continues out
                    execBlock st cur fin |> ignore
                    raise e

            execBlock st afterTry fin
        | SFunc(n, ps, body) -> Env.withFunc n ps body env

    and private execBlock (st: State) (env: Env) (stmts: Stmt list) : Env =
        stmts |> List.fold (execStmt st) env

    /// Evaluate a script. `registeredSteps` is the host's step vocabulary;
    /// anything else is denied by name. Effects are returned for the host to
    /// perform — the interpreter performs none itself.
    let private runWith (host: PerformStep option) (strictVars: bool) (budget: Budget) (registeredSteps: Set<string>) (env: Env) (script: Script) : Outcome =
        let defined = Ast.definedFunctions script

        let st =
            { Steps = 0
              Depth = 0
              Effects = []
              Budget = budget
              RegisteredSteps = registeredSteps
              Host = host
              EnvUserShadowed = false
              Defined = defined
              LastValue = None
              StrictVars = strictVars
              NewBindings = []
              Binding = env.Vars }

        // hoist declared functions so a call before its definition resolves
        let hoisted =
            script
            |> List.fold
                (fun acc s ->
                    match s with
                    | SFunc(n, ps, b) -> Env.withFunc n ps b acc
                    | _ -> acc)
                { env with Vars = Map.empty }

        try
            let final = execBlock st hoisted script

            { Effects = List.rev st.Effects
              Fault = None
              // the BINDING is the outcome's variable view — locals died with
              // their scopes, exactly as Groovy's did
              Env = { final with Vars = st.Binding }
              NewBindings = List.rev st.NewBindings
              // Groovy's last-expression-is-the-value, for any statement block.
              Returned = st.LastValue }
        with
        | Stop f ->
            { Effects = List.rev st.Effects
              Fault = Some f
              // NOT the pristine environment: assignments made BEFORE the fault were
              // performed — `${x = 'kept'; MISSING}` fails, and Jenkins' post block
              // still reads x. The mutable binding survives the raise by nature.
              Env = { hoisted with Vars = st.Binding }
              NewBindings = List.rev st.NewBindings
              Returned = None }
        | ReturnSignal v ->
            { Effects = List.rev st.Effects
              Fault = None
              Env = { hoisted with Vars = st.Binding }
              NewBindings = List.rev st.NewBindings
              Returned = Some v }

    /// Groovy's late-binding default: an unknown name reads as null.
    let run (budget: Budget) (registeredSteps: Set<string>) (env: Env) (script: Script) : Outcome =
        runWith None false budget registeredSteps env script

    /// Groovy's property-lookup contract for GStrings in step arguments: an unknown
    /// name faults with [UnknownProperty]. Enforced at READ time because laziness
    /// makes the read set undecidable statically — `${c ? A : B}` reads one arm.
    /// FG-160 slice 2. Evaluate with the host performing steps LIVE, strict variable
    /// reads included — a script block is a Jenkins context, where an unbound name raises
    /// MissingPropertyException rather than reading null.
    ///
    /// `Outcome.Effects` is EMPTY in this mode, and that is not an oversight: the steps
    /// already happened. A caller that reads `Effects` after this has misunderstood which
    /// model it asked for, so leaving the list empty makes that mistake visible rather
    /// than handing back a replayable-looking log of work already done.
    let runHosted (host: PerformStep) (budget: Budget) (registeredSteps: Set<string>) (env: Env) (script: Script) : Outcome =
        runWith (Some host) true budget registeredSteps env script

    let runStrictVars (budget: Budget) (registeredSteps: Set<string>) (env: Env) (script: Script) : Outcome =
        runWith None true budget registeredSteps env script

    let runDefault registeredSteps script =
        run Budget.defaults registeredSteps Env.empty script
