namespace Fogell.Groovy.Interpreter

open Fogell.Groovy
open Fogell.Admission

/// A side effect the interpreter wants the host to perform. The interpreter
/// itself never touches the outside world — it *requests*. That is what makes
/// the sandbox structural rather than aspirational.
type Effect =
    | StepCall of name: string * positional: Value list * named: (string * Value) list

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

                 match Sandbox.admitMethod name with
                 | Error d -> raise (Stop(Denied d))
                 | Ok _ -> evalBuiltin st env name r args trailing)
        | MethodCall(recv, name) ->
            match Sandbox.admitMethod name with
            | Error d -> raise (Stop(Denied d))
            | Ok _ -> evalBuiltin st env name (evalExpr st env recv) positionalLazy.Value trailing
        | FreeCall name ->
            match Sandbox.admitCall st.RegisteredSteps st.Defined name with
            | Error d -> raise (Stop(Denied d))
            | Ok(Step s) ->
                // a step is a request to the host, not something we perform
                st.Effects <- StepCall(s, positionalLazy.Value, namedLazy.Value) :: st.Effects

                trailing
                |> Option.iter (fun c ->
                    st.Depth <- st.Depth + 1
                    execBlock st env c.Body |> ignore
                    st.Depth <- st.Depth - 1)

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
                | None -> evalBuiltin st env b VNull positionalLazy.Value trailing

    and private evalBuiltin (st: State) (env: Env) (name: string) (recv: Value) (args: Value list) (trailing: Closure option) : Value =
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
            Env.withVar n v env
        // REVIEW FIX (Codex, PR #14 round 6): value tracking was added only for
        // INITIALISED declarations, so `[1].any { true; def x }` left `true` in
        // LastValue and the closure returned true. An uninitialised declaration
        // evaluates to null.
        | SDef(n, None) ->
            st.LastValue <- Some VNull
            Env.withVar n VNull env
        | SAssign(EVar n, v) ->
            let value = evalExpr st env v
            st.LastValue <- Some value

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
        | SReturn e -> raise (ReturnSignal(match e with Some x -> evalExpr st env x | None -> VNull))
        | SBreak -> raise BreakSignal
        | SContinue -> raise ContinueSignal
        | SThrow e -> raise (Stop(Thrown(evalExpr st env e)))
        | STry(body, catch, fin) ->
            let afterTry =
                try
                    execBlock st env body
                with Stop(Thrown v) ->
                    match catch with
                    | Some(binding, handler) ->
                        let e2 =
                            match binding with
                            | Some n -> Env.withVar n v env
                            | None -> env

                        execBlock st e2 handler
                    | None -> env

            execBlock st afterTry fin
        | SFunc(n, ps, body) -> Env.withFunc n ps body env

    and private execBlock (st: State) (env: Env) (stmts: Stmt list) : Env =
        stmts |> List.fold (execStmt st) env

    /// Evaluate a script. `registeredSteps` is the host's step vocabulary;
    /// anything else is denied by name. Effects are returned for the host to
    /// perform — the interpreter performs none itself.
    let private runWith (strictVars: bool) (budget: Budget) (registeredSteps: Set<string>) (env: Env) (script: Script) : Outcome =
        let defined = Ast.definedFunctions script

        let st =
            { Steps = 0
              Depth = 0
              Effects = []
              Budget = budget
              RegisteredSteps = registeredSteps
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
        runWith false budget registeredSteps env script

    /// Groovy's property-lookup contract for GStrings in step arguments: an unknown
    /// name faults with [UnknownProperty]. Enforced at READ time because laziness
    /// makes the read set undecidable statically — `${c ? A : B}` reads one arm.
    let runStrictVars (budget: Budget) (registeredSteps: Set<string>) (env: Env) (script: Script) : Outcome =
        runWith true budget registeredSteps env script

    let runDefault registeredSteps script =
        run Budget.defaults registeredSteps Env.empty script
