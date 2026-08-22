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
///   - direct-step return values publish through the host, and hosted wrappers return the
///     typed value captured when their body thunk runs;
///   - per-step journaling is POSSIBLE here — the hooks are keyed
///     `stage -> stepIndex`, and per-CHILD identity inside a block is FG-204:
///     FG-135's stage-level attempt markers do not deliver it, and FG-171 closed
///     by measuring that a crash mid-block refuses rather than re-runs.
/// The earlier wording claimed all four as delivered.
///
/// [RunBody] is present when the call had a trailing block; invoking it evaluates that
/// block, so a wrapper can set up its context, call it, and tear down.
type HostedCallPhase =
    | OrdinaryCall
    | FinallyUnwind

/// A fresh terminal status introduced by a hosted call while `finally` is
/// unwinding an older branch halt. Kept independent of the differential
/// layer's BuildStatus so the interpreter/host boundary stays acyclic.
type HostedStepHaltKind =
    | HostedFailure
    | HostedAborted

type PerformStep =
    { /// The phase is explicit because a hosted branch that has already halted
      /// still executes calls in a `finally` while its failure unwinds. Hosts
      /// must not infer this permission from a false CanContinue value.
      Perform: HostedCallPhase -> string -> Value list -> (string * Value) list -> (unit -> unit) option -> Value
      /// Whether the hosted branch can continue evaluating Groovy at all. Asked
      /// before every call and again after a hosted step returns, so a step that
      /// halts the branch unwinds the script before a later helper, builtin, or
      /// argument can acquire a replacement fault or effect.
      CanContinue: unit -> bool
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
      ///
      /// MUST NOT THROW. FG-202's exit re-refresh calls this inside a FINALLY while a
      /// body fault may be propagating, and a throw there would REPLACE the body's
      /// exception with its own — misreporting the failure. The production host is pure
      /// list/map folding over an always-populated ref; keep any future host that way.
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

/// The measured runtime class Jenkins raises while binding a hosted step call.
type BindingExceptionClass =
    | IllegalArgumentException
    | NullPointerException

module BindingExceptionClass =
    let fullName = function
        | IllegalArgumentException -> "java.lang.IllegalArgumentException"
        | NullPointerException -> "java.lang.NullPointerException"

/// The operation whose script-value walk encountered a reference cycle.
/// Every case maps to Jenkins' StackOverflowError ancestry; the operation is
/// retained only so diagnostics identify the boundary that faulted.
type CycleOperation =
    | Display
    | Equality
    | Ordering
    | HostedArgumentCoercion
    | HashKey

/// Why evaluation stopped.
type Fault =
    | Denied of Denial
    | Unsupported of construct: string
    | Thrown of Value
    /// FG-177. Jenkins rejected the call while binding its descriptor. Unlike an
    /// engine refusal, this is ordinary scripted-Groovy control flow and may be caught.
    | StepBindingFailed of stepName: string * exceptionClass: BindingExceptionClass * detail: string
    /// FG-176/FG-186. A SHELL STEP inside a hosted body FAILED — the outcome
    /// Jenkins surfaces as a catchable, retryable AbortException. Distinct from
    /// a refusal on purpose: a refusal says Fogell cannot MODEL the call, and
    /// letting a script catch that would let a pipeline recover from a gap in
    /// this engine while Jenkins ran the real step — a silent divergence. Only
    /// the measured shell and missing-unstash paths carry this fault; every other
    /// failure keeps the old fail-loud path until its own measurement moves it.
    | StepFailed of stepName: string * exceptionText: string * diagnosticText: string
    /// A malformed hosted call which Jenkins stops before dispatch. This remains
    /// opaque to Groovy catch clauses, but is deferred until it escapes the
    /// current control-flow scope so `finally` and retry retain ownership.
    | HostedCallRefused of detail: string
    /// A cleanup call returned normally after introducing a new terminal build
    /// status. This is catch-opaque like the existing hosted halt: it stops
    /// successor cleanup statements, but still participates in nested `finally`
    /// and return precedence before the wrapper/top-level owner commits it.
    | HostedStepHalted of stepName: string * haltKind: HostedStepHaltKind * diagnosticText: string
    | BudgetExhausted of what: string
    /// A bare name bound nowhere, read under [Interpreter.runStrictVars]. Groovy
    /// throws MissingPropertyException here; the default mode's late-binding null
    /// is kept for consumers that model scripted Groovy's laxer contexts.
    | UnknownProperty of name: string
    /// A write attempted to assign through a null receiver. Jenkins raises a
    /// catchable NullPointerException here even for safe-navigation syntax.
    | NullReceiverAssignment of target: string
    /// FG-015b. Groovy's too-negative list subscript is an ordinary, catchable
    /// ArrayIndexOutOfBoundsException. Keep it distinct so typed catches retain
    /// the measured ancestry and unsupported list-key shapes remain refusals.
    | ListIndexOutOfBounds of index: int64 * size: int
    /// A compound/postfix list update read null (an explicit null element or a
    /// positive index outside the current size). Jenkins raises a catchable NPE
    /// when the requested operator cannot accept null; unlike a too-negative
    /// index, compound assignment has already evaluated its RHS at this point.
    | NullListIndexUpdate of index: int64
    /// Jenkins' sandbox permits scalar index reads such as String.getAt but
    /// rejects the eventual putAt, while other scalar getAt shapes are rejected
    /// before an update RHS. Keep that catchable SecurityException boundary
    /// separate from a modelling refusal.
    | RejectedIndexOperation of phase: string
    /// Positive String indexes beyond the end raise this concrete class. A
    /// too-negative String index follows Groovy's ArrayIndexOutOfBounds path and
    /// therefore uses [ListIndexOutOfBounds] instead.
    | StringIndexOutOfBounds of index: int64 * size: int
    /// A display, equality, ordering, hosted-argument coercion, or hash-key walk
    /// met a reference cycle. Jenkins raises StackOverflowError for each measured
    /// path. Keep one typed fault family so Error ancestry cannot drift again.
    | CyclicValue of operation: CycleOperation
    /// IntRange implements list reads but not replacement. Jenkins raises a
    /// catchable UnsupportedOperationException at the write phase.
    | RangeMutation

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
    type HostedBodyHalt =
        | BranchHalt
        | CallRefusalHalt of detail: string
        | StepStatusHalt of stepName: string * haltKind: HostedStepHaltKind * diagnosticText: string


    let spreadAssignmentRefusal =
        "unsupported_spread_assignment: Jenkins 2.568.1 raises a catchable runtime exception when an assignment target contains spread-property syntax; Fogell does not model writes through a projected value. Refusing before effects instead of silently discarding the write"

    let listIndexAssignmentRefusal =
        "unsupported_list_index_assignment: list index writes require an integer key; refusing the unmeasured key shape instead of silently discarding or misdirecting the write"

    let assignmentTargetRefusal =
        "unsupported_assignment_target: Fogell cannot model this assignment target; refusing before the RHS instead of reporting success without a write"

    let collectionOrderingRefusal =
        "unsupported_collection_ordering: Fogell cannot safely order this value type; refusing instead of invoking the host runtime comparer"

    exception private Stop of Fault
    exception private ReturnSignal of Value
    exception private BreakSignal
    exception private ContinueSignal
    exception private HostedHaltSignal

    type private State =
        { mutable Steps: int
          mutable Depth: int
          /// A `finally` entered while HostedHaltSignal is unwinding must execute
          /// cleanup calls even though the host branch is already halted. This is
          /// a depth, not a boolean, because nested try/finally blocks unwind inner
          /// then outer and each must retain the narrow exception to reachability.
          mutable FinallyUnwindDepth: int
          mutable Effects: Effect list
          Budget: Budget
          RegisteredSteps: Set<string>
          /// None keeps the BATCH model — effects are collected and the call yields null.
          /// Existing consumers (`when { expression }`, the tests) rely on that and are
          /// deliberately unchanged; only a caller that supplies this gets live steps.
          Host: PerformStep option
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
          /// FG-179. The cells that ARE the Jenkins environment, by IDENTITY.
          ///
          /// `env.FOO = …` is a Jenkins environment write only when `env` resolves to one
          /// of these. It used to be decided syntactically — any `EProp(EVar "env", _)` was
          /// routed to the host — so a script's own `def env = [:]` had its ordinary map
          /// mutation sent to `host.SetEnv`, which refuses, killing the step after it while
          /// Jenkins runs on. Provenance by cell identity answers it without naming a
          /// syntactic form, exactly as the hosted refresh does.
          JenkinsEnvCells: System.Collections.Generic.HashSet<Value ref>
          /// FG-193. The MAPS that are the Jenkins environment, by reference — the
          /// value-level twin of JenkinsEnvCells. `def saved = env; def env = saved`
          /// mints a fresh CELL outside the cell set, but the VALUE it carries is
          /// still the Jenkins map, and a write through it must reach the host's
          /// refusal exactly as the direct spelling does — measured: the aliased
          /// write reached Jenkins' shell and silently vanished here.
          JenkinsEnvMaps: System.Collections.Generic.HashSet<Map<string, Value> ref>
          mutable Binding: Map<string, Value> }

    let private tick (st: State) =
        st.Steps <- st.Steps + 1

        if st.Steps > st.Budget.MaxSteps then
            raise (Stop(BudgetExhausted $"evaluation exceeded {st.Budget.MaxSteps} steps"))

    let private scriptDisplay value =
        if Value.containsScmMap value then
            raise (Stop(Unsupported "SCM return-map rendering is not modelled; read one measured string key"))
        elif Value.containsJUnitSummary value then
            raise (Stop(Unsupported "JUnit TestResultSummary rendering is not modelled; read totalCount, failCount, or skipCount"))
        else
            match Value.tryToDisplay value with
            | Value.Text rendered -> rendered
            | Value.DisplayCycleDetected -> raise (Stop(CyclicValue Display))

    let private scriptTruthy value =
        match value with
        | VScmMap _
        | VScmKeySet _ ->
            raise (Stop(Unsupported "SCM return-map truthiness is not modelled; read one measured string key"))
        | VJUnitSummary _ ->
            raise (Stop(Unsupported "JUnit TestResultSummary truthiness is not modelled; read a measured count property"))
        | _ -> Value.isTruthy value

    let private scriptHashKey value =
        if Value.hasReferenceCycle value then
            raise (Stop(CyclicValue HashKey))

        scriptDisplay value

    let private tooNegativeListIndex index size = index < -(int64 size)

    let private listRead (items: Value list ref) index =
        let size = items.Value.Length

        if tooNegativeListIndex index size then
            raise (Stop(ListIndexOutOfBounds(index, size)))
        elif index < 0L then
            items.Value.[size + int index]
        elif index < int64 size then
            items.Value.[int index]
        else
            VNull

    let private stringIndexRead (value: string) index =
        let size = value.Length

        if tooNegativeListIndex index size then
            raise (Stop(ListIndexOutOfBounds(index, size)))

        let normalized = if index < 0L then int64 size + index else index

        if normalized >= int64 size then
            raise (Stop(StringIndexOutOfBounds(index, size)))

        VStr(string value.[int normalized])

    let private listWrite (st: State) (items: Value list ref) index value =
        let size = items.Value.Length

        if tooNegativeListIndex index size then
            raise (Stop(ListIndexOutOfBounds(index, size)))

        let normalized = if index < 0L then int64 size + index else index

        if normalized > int64 st.Budget.MaxLoopIterations then
            raise (Stop(BudgetExhausted "list index extension exceeds the iteration budget"))

        let position = int normalized

        if position < size then
            items.Value <- items.Value |> List.mapi (fun i prior -> if i = position then value else prior)
        else
            items.Value <- items.Value @ List.replicate (position - size) VNull @ [ value ]

    let private (|ListLike|_|) value =
        match value with
        | VList items -> Some items
        | VRange values -> Some(ref (Value.rangeItems values))
        | _ -> None

    /// Jenkins 2.568.1's CPS collection traversal is live and index-based. The
    /// value at the current slot is captured before the closure runs; later
    /// slots are read from the ref cell after earlier mutations. Appends and
    /// positive-index extension are visited, while shrinkage shortens the walk.
    /// Count actual visits so a closure that appends forever still hits budget.
    let private iterateLiveList (st: State) operation (items: Value list ref) keepGoing visit =
        let mutable index = 0
        let mutable iterations = 0

        while keepGoing () && index < items.Value.Length do
            iterations <- iterations + 1

            if iterations > st.Budget.MaxLoopIterations then
                raise (Stop(BudgetExhausted $"{operation} exceeds the loop-iteration budget"))

            let current = items.Value.[index]
            index <- index + 1
            visit current

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
                | GExpr x -> scriptDisplay (evalExpr st env x))
            |> String.concat ""
            |> VStr
        | EList xs -> VList(ref (xs |> List.map (evalExpr st env)))
        | EMap kvs -> VMap(ref (kvs |> List.map (fun (k, v) -> k, evalExpr st env v) |> Map.ofList))
        | EVar n ->
            // locals shadow the binding; the binding shadows declared functions
            match Map.tryFind n env.Vars with
            // FG-179: locals are ref CELLS now, so a read derefs. What the closure shares is
            // the cell, which is why a write made inside one is visible here afterwards.
            | Some cell -> cell.Value
            | None ->
                match Map.tryFind n st.Binding with
                | Some v -> v
                | None ->
                    match Map.tryFind n env.Funcs with
                    // FG-195: a name may carry several candidates; reading it as a VALUE
                    // takes the first declaration. Groovy raises MissingPropertyException
                    // for ANY value read of a method name — rendering `<function n>` here
                    // is a pre-existing divergence the verifier re-flagged, carried as a
                    // residual on the FG-195 ticket; resolution by arity happens at a
                    // CALL, not here.
                    | Some((ps, body) :: _) -> VFunc(n, ps, body)
                    | Some []
                    | None ->
                        if st.StrictVars then
                            // MEASURED (receipt `gstring-unresolved-property`): Groovy's
                            // property lookup FAILS the build for a name bound nowhere.
                            raise (Stop(UnknownProperty n))
                        else
                            VNull // Groovy resolves unknown bindings late; treat as null
        | ESpreadProp(target, name) -> evalSpreadProp st (evalExpr st env target) name
        | ESafeProp((ESpreadProp _ as target), name) ->
            // Jenkins 2.568.1: `rows*.child?.name` keeps projecting across the
            // spread result, while still short-circuiting a null receiver. Keep
            // this syntax-bounded; ordinary safe property access is unchanged.
            evalSpreadProp st (evalExpr st env target) name
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
            | ListLike xs, VInt i -> listRead xs i
            | VMap _, key when Value.hasReferenceCycle key -> raise (Stop(CyclicValue HashKey))
            | VMap m, VStr k -> defaultArg (Map.tryFind k m.Value) VNull
            | VScmMap scm, VStr key ->
                scm.Entries |> Map.tryFind key |> Option.map VStr |> Option.defaultValue VNull
            | VScmMap _, _ ->
                raise (Stop(Unsupported "SCM return-map indexing is modelled only for string keys"))
            | VScmKeySet _, _ ->
                raise (Stop(Unsupported "SCM return-map key-set indexing is not modelled; use join(String)"))
            | VJUnitSummary _, _ ->
                raise (Stop(Unsupported "JUnit TestResultSummary indexing is not modelled; read a measured count property"))
            | VStr s, VInt i when i >= 0L && int i < s.Length -> VStr(string s.[int i])
            | _ -> VNull
        | EUnary(op, x) ->
            let v = evalExpr st env x

            match op with
            | "!" -> VBool(not (scriptTruthy v))
            | "-" ->
                match v with
                | VInt i -> VInt(-i)
                | _ when st.StrictVars ->
                    raise (Stop(Unsupported "unary '-' is not modelled for this operand type"))
                | _ -> VNull
            | _ -> raise (Stop(Unsupported $"unary operator {op}"))
        | EBinary(op, l, r) -> evalBinary st env op l r
        | ETernary(c, a, b) ->
            if scriptTruthy (evalExpr st env c) then
                evalExpr st env a
            else
                evalExpr st env b
        | EElvis(a, b) ->
            let v = evalExpr st env a
            if scriptTruthy v then v else evalExpr st env b
        | EClosure c ->
            // FG-191. EVERY EVALUATION MINTS A DISTINCT CLOSURE, as Groovy's does:
            // equality compares the captured env RECORD by reference, and a loop
            // body whose iterations assign through cells never changes the record
            // — so without this fresh allocation, two closures minted by one
            // literal in a `while` compared EQUAL where Groovy says false
            // (measured, loopEq:true, a false equality this same branch would
            // otherwise have shipped). The copy shares the cells, so capture
            // stays by reference; only the record's identity is new.
            VClosure(c, { env with Vars = env.Vars })
        | ECall(target, args, trailing) -> evalCall st env target args trailing

    and private evalProp (st: State) (recv: Value) (name: string) : Value =
        match recv with
        | VMap m -> defaultArg (Map.tryFind name m.Value) VNull
        | VScmMap scm ->
            scm.Entries |> Map.tryFind name |> Option.map VStr |> Option.defaultValue VNull
        | VScmKeySet _ ->
            raise (Stop(Unsupported "SCM return-map key-set properties are not modelled; use join(String)"))
        | VJUnitSummary summary ->
            match name with
            | "totalCount" -> VInt summary.Value.TotalCount
            | "failCount" -> VInt summary.Value.FailCount
            | "skipCount" -> VInt summary.Value.SkipCount
            | _ ->
                raise (Stop(Unsupported $"JUnit TestResultSummary property `{name}` is not modelled; supported properties: totalCount, failCount, skipCount"))
        // MEASURED (receipt `gstring-string-property-fails`, Jenkins 2.568.1): the
        // sandbox REJECTS property access on a String — `${env.TARGET.length}` is
        // "No such field found: field java.lang.String length" and the build
        // FAILS; only the METHOD form `.length()` returns the count. The lenient
        // convenience below is therefore confined to non-strict consumers.
        | ListLike xs when not st.StrictVars && (name = "size" || name = "length") -> VInt(int64 xs.Value.Length)
        | VStr s when not st.StrictVars && (name = "size" || name = "length") -> VInt(int64 s.Length)
        | _ when st.StrictVars -> raise (Stop(UnknownProperty name))
        | _ -> VNull

    and private evalSpreadProp (st: State) (recv: Value) (name: string) : Value =
        match recv with
        | VNull -> VNull
        | VScmMap _
        | VScmKeySet _ ->
            raise (Stop(Unsupported "spread access on an SCM return map is not modelled"))
        | VJUnitSummary _ ->
            raise (Stop(Unsupported "spread access on JUnit TestResultSummary is not modelled"))
        | ListLike xs ->
            if xs.Value |> List.exists Value.containsScmMap then
                raise (Stop(Unsupported "spread access over an SCM return map is not modelled"))
            elif xs.Value |> List.exists Value.containsJUnitSummary then
                raise (Stop(Unsupported "spread access over a JUnit TestResultSummary is not modelled"))
            elif xs.Value.Length > st.Budget.MaxLoopIterations then
                raise (Stop(BudgetExhausted "spread projection exceeds the iteration budget"))

            xs.Value
            |> List.choose (fun item ->
                tick st

                match item with
                // Jenkins' spread-safe operator omits a null RECEIVER. A non-null
                // receiver whose property value is null stays in the result.
                | VNull -> None
                | value -> Some(evalProp st value name))
            |> ref
            |> VList
        // Jenkins applies the ordinary property rule to non-list receivers:
        // map key lookup stays map lookup, and unsupported scalar properties fault.
        | value -> evalProp st value name

    and private evalBinary (st: State) (env: Env) op l r : Value =
        // short-circuit before evaluating the right side
        match op with
        | "&&" ->
            if scriptTruthy (evalExpr st env l) then
                VBool(scriptTruthy (evalExpr st env r))
            else
                VBool false
        | "||" ->
            if scriptTruthy (evalExpr st env l) then
                VBool true
            else
                VBool(scriptTruthy (evalExpr st env r))
        | _ ->

        let a = evalExpr st env l
        let b = evalExpr st env r
        evalBinaryValues st op a b

    and private evalBinaryValues (st: State) op a b : Value =
        match op, a, b with
        | _, VScmMap _, _
        | _, _, VScmMap _ ->
            raise (Stop(Unsupported $"operator '{op}' is not modelled for an SCM return map"))
        | _, VScmKeySet _, _
        | _, _, VScmKeySet _ ->
            raise (Stop(Unsupported $"operator '{op}' is not modelled for an SCM return-map key set"))
        | _, VJUnitSummary _, _
        | _, _, VJUnitSummary _ ->
            raise (Stop(Unsupported $"operator '{op}' is not modelled for JUnit TestResultSummary"))
        | "+", VInt x, VInt y -> VInt(x + y)
        | "+", VStr x, _ -> VStr(x + scriptDisplay b)
        | "+", _, VStr y -> VStr(scriptDisplay a + y)
        | "+", ListLike x, ListLike y -> VList(ref (x.Value @ y.Value))
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
        | "<<", VList x, _ ->
            x.Value <- x.Value @ [ b ]
            VList x
        | "<<", VStr x, _ -> VStr(x + scriptDisplay b)
        // FG-191. THROUGH the cycle-aware equality, never bare structural `=`:
        // two self-referential maps compared here KILLED THE PROCESS (measured,
        // exit 134), which no catch, receipt or budget can see. A detected cycle
        // raises what Groovy's own chase produces — the JVM dies of
        // StackOverflowError and the build fails — as a fault this runtime
        // survives. (Groovy's Error is not caught by `catch (Exception)`; this
        // Thrown is — a named residual on the FG-191 row, not a silent one.)
        | "==", _, _ ->
            match Value.tryEq a b with
            | Value.Answer r -> VBool r
            | Value.CycleDetected -> raise (Stop(CyclicValue Equality))
            | Value.Unmodelled -> raise (Stop(Unsupported "equality is not modelled for nominal Jenkins return values"))
        | "!=", _, _ ->
            match Value.tryEq a b with
            | Value.Answer r -> VBool(not r)
            | Value.CycleDetected -> raise (Stop(CyclicValue Equality))
            | Value.Unmodelled -> raise (Stop(Unsupported "equality is not modelled for nominal Jenkins return values"))
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
                | VRange _, ("List" | "Collection" | "Range" | "IntRange") -> true
                | VMap _, ("Map" | "HashMap" | "LinkedHashMap") -> true
                | VBool _, "Boolean" -> true
                | _ -> false)
        | "..", VInt x, VInt y ->
            let distance = System.Numerics.BigInteger.Abs(bigint y - bigint x)

            if distance > bigint st.Budget.MaxLoopIterations then
                raise (Stop(BudgetExhausted "range exceeds the loop-iteration budget"))

            let step = if x <= y then 1L else -1L

            let rec valuesFrom current acc =
                if current = y then List.rev (current :: acc)
                else valuesFrom (current + step) (current :: acc)

            VRange(valuesFrom x [])
        | _ when st.StrictVars ->
            // An operator on operand types this interpreter does not model. Groovy
            // THROWS for `1 - 'x'`; inventing null instead ran `deploy null` on a
            // green build — the same silent-wrong-command shape as the erased name
            // and the unmodelled method. Refuse by name.
            raise (Stop(Unsupported $"operator '{op}' is not modelled for these operand types"))
        | _ -> VNull

    /// FG-195. Groovy's argument-forming rules, in ONE place for every callable kind
    /// — helper, overload, closure value: a named-argument group is a single Map
    /// argument passed FIRST, and a trailing closure is a single FINAL argument
    /// closing over the call site. The fourth measured shape existed because a guard
    /// counted positionals while looking at only one of these three forms.
    and private effectiveArgList (env: Env) (positional: Value list) (named: (string * Value) list) (trailing: Closure option) =
        (if List.isEmpty named then [] else [ VMap(ref (Map.ofList named)) ])
        @ positional
        @ (match trailing with
           // fresh record per evaluation, same as EClosure — a trailing block is
           // one more closure minting site (FG-191)
           | Some c -> [ VClosure(c, { env with Vars = env.Vars }) ]
           | None -> [])

    /// FG-189, folded into FG-195: a closure VALUE is callable. A bare closure has
    /// the implicit `it` — bound null when no argument arrives, which is Groovy's
    /// rule — and declared parameters bind by exact arity. A mismatch is a refusal
    /// naming the attempted signature, the same contract as helper resolution.
    ///
    /// NAMED RESIDUAL (verifier, this diff): the parser erases `{ -> … }` into
    /// `Params = []`, so an explicitly zero-parameter closure called WITH an
    /// argument takes the implicit-`it` arm and RUNS where Groovy throws — a false
    /// success one spelling from shape (c). The fix needs the arrow kept in the
    /// AST, which is parser scope; carried on the FG-195 ticket, not silently.
    and private invokeClosureValue (st: State) (name: string) (c: Closure) (closureEnv: Env) (callArgs: Value list) =
        let bound =
            match c.Params, callArgs with
            | [], [] -> Env.withVar "it" VNull closureEnv
            | [], [ one ] -> Env.withVar "it" one closureEnv
            | ps, args when List.length ps = List.length args ->
                List.zip ps args |> List.fold (fun acc (p, v) -> Env.withVar p v acc) closureEnv
            | ps, args ->
                raise (
                    Stop(
                        Unsupported
                            $"closure '{name}' takes {List.length ps} parameter(s) and was called with {List.length args} argument(s); a named-argument group counts as one Map argument"
                    )
                )

        st.Depth <- st.Depth + 1

        // implicit-return discipline shared with `applyClosure`: the trailing
        // expression is the value, LastValue saved/restored on both exits.
        // Depth decrements in a FINALLY: a fault escaping the body can be caught
        // by a script-level try/catch and execution then CONTINUES — a decrement
        // skipped on that path leaks depth until an innocent loop of caught
        // faults exhausts the call-depth budget where Jenkins runs on. Raised by
        // the verifier on this diff, which had copied the fragile shape.
        try
            try
                let outer = st.LastValue

                try
                    st.LastValue <- None
                    execBlock st bound c.Body |> ignore
                    defaultArg st.LastValue VNull
                finally
                    st.LastValue <- outer
            with ReturnSignal v ->
                v
        finally
            st.Depth <- st.Depth - 1

    and private evalCall (st: State) (env: Env) (target: CallTarget) (args: Arg list) (trailing: Closure option) : Value =
        tick st

        if st.Depth >= st.Budget.MaxCallDepth then
            raise (Stop(BudgetExhausted $"call depth exceeded {st.Budget.MaxCallDepth}"))

        // RESOLVE A FREE CALL BEFORE REACHABILITY IS APPLIED. A script helper wins
        // over a registered step in Sandbox.admitCall; checking RegisteredSteps here
        // instead made `def sh(x) { ... }; sh(MISSING)` look like a hosted step after
        // halt, discarded MISSING, then invoked the helper with ZERO arguments. Keep
        // the authoritative resolution result and use it again at dispatch.
        let admittedFreeCall =
            match target with
            | FreeCall name -> Some(Sandbox.admitCall st.RegisteredSteps st.Defined name)
            | _ -> None

        // A hosted halt ends Groovy evaluation; it is not a null-valued call and it
        // is not an interpreter fault. Unwind before forcing any call argument, and
        // check again around nested evaluation and after Perform because the call
        // itself can be what halts the branch.
        let ensureCanContinue () =
            match st.Host with
            | Some host when st.FinallyUnwindDepth = 0 && not (host.CanContinue()) -> raise HostedHaltSignal
            | _ -> ()

        let evalArgument e =
            ensureCanContinue ()
            // Jenkins injects `scm` as a job binding object. Fogell never
            // exposes that object generically; the only measured consumer is
            // the registered `checkout(scm)` step. Preserve its existing
            // walker token without making `scm` readable/renderable by echo,
            // interpolation, helpers, or any other hosted call. A script-defined
            // function named checkout does not qualify because resolution above
            // must have selected the registered Step capability.
            let value =
                match admittedFreeCall, target, e with
                | Some(Ok(Step "checkout")), FreeCall "checkout", EVar "scm"
                    when not (Map.containsKey "scm" env.Vars)
                         && not (Map.containsKey "scm" st.Binding) ->
                    VStr "scm"
                | _ -> evalExpr st env e
            ensureCanContinue ()
            value

        ensureCanContinue ()

        // LAZY: a safe call on a null receiver short-circuits the WHOLE call, its
        // arguments included — `a?.m(sideEffect())` runs nothing when a is null.
        let positionalLazy =
            lazy
                (args
                 |> List.choose (function
                     | APos e -> Some(evalArgument e)
                     | _ -> None))

        let namedLazy =
            lazy
                (args
                 |> List.choose (function
                     | ANamed(n, e) -> Some(n, evalArgument e)
                     | _ -> None))

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

            // FG-195/FG-189. A LOCAL BINDING SHADOWS A HELPER, and a local CLOSURE is
            // callable — Groovy resolves the local, and the refusal that stood here sent
            // ordinary code away (measured shape (a): `def x = { 'LOCAL' }` after a
            // preamble `def x()` runs the LOCAL on Jenkins). A registered STEP name keeps
            // its current routing: a local sharing a step's name is unmeasured territory
            // and the boundary is stated here rather than silently decided. A NON-closure
            // local beside a helper stays a refusal — Groovy's method fallback for that
            // shape is unmeasured, and a named refusal beats a guessed dispatch.
            let shadowingLocal =
                if st.RegisteredSteps.Contains name then
                    None
                else
                    match Map.tryFind name env.Vars with
                    | Some cell ->
                        match cell.Value with
                        | VClosure(c, closureEnv) -> Some(Choice1Of2(c, closureEnv))
                        | other when Map.containsKey name env.Funcs -> Some(Choice2Of2 other)
                        | _ -> None
                    | None -> None

            match shadowingLocal with
            | Some(Choice1Of2(c, closureEnv)) ->
                invokeClosureValue st name c closureEnv (effectiveArgList env positionalLazy.Value namedLazy.Value trailing)
            | Some(Choice2Of2 other) ->
                raise (
                    Stop(
                        Unsupported
                            $"'{name}' is bound as a non-closure local ({Value.toDisplay other}) and declared as a helper; Groovy's fallback for this shape is unmeasured, so Fogell refuses rather than guesses"
                    )
                )
            | None ->

            match admittedFreeCall with
            | None -> failwith "FreeCall must have an admission result"
            | Some(Error d) -> raise (Stop(Denied d))
            | Some(Ok(Step s)) ->
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
                // reached `validateHostedCall` as a valid one-positional `sh` with a
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

                // FG-179. THE CLOSURE'S OWN ENV TRAVELS WITH IT. A closure reaching a
                // wrapper as a VALUE was reduced to its `Closure` and the `Env` beside it in
                // `VClosure` was dropped, so the body ran against the CALL SITE's scope —
                // where the names it captured need not exist at all.
                //
                // ONCE LOCALS BECAME CELLS THAT STOPPED BEING MERELY LOSSY AND BECAME A
                // WRONG WRITE. `def makeBody(v) { return { v = 'changed' } }` invoked as
                // `dir('sub', makeBody('inner'))` with a caller-local `v` assigned THE
                // CALLER'S `v`, because that was the only `v` in the scope it was handed.
                // Jenkins changes the captured parameter and leaves the caller's alone.
                // Before cells the write was discarded and the damage stayed inside; after,
                // it escaped. Caught in review on PR #54 as a regression of my own change.
                let trailingArg, capturedEnv, positionalArgs =
                    match trailing, List.rev positionalLazy.Value with
                    | None, VClosure(c, closureEnv) :: rest when takesBlock ->
                        Some c, Some closureEnv, List.rev rest
                    | _ -> None, None, positionalLazy.Value

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
                //   THAT LIMIT IS CLOSED, ten lines below this one. It said variables
                //   DECLARED inside the body persist between invocations where Groovy
                //   creates them afresh, and a later hedge added that it was UNPROVEN since
                //   ref cells landed. Neither is true: `captured` is the name set present
                //   BEFORE the invocation, and the post-body environment is filtered back to
                //   it, so a body's declarations are dropped exactly as Groovy drops them.
                //   The answer was in the same function as the doubt.
                // A closure passed by VALUE brings its own scope; a trailing block is
                // written at the call site and shares that one. `capturedEnv` is `Some` only
                // in the first case, which is exactly when they differ.
                let bodyEnv = ref (defaultArg capturedEnv env)
                // The thunk stays unit-returning so the established host seam and every
                // wrapper dispatcher remain unchanged. Its result travels in this fresh
                // per-call cell instead: a retry may invoke the thunk repeatedly, and the
                // last successful invocation naturally becomes the wrapper's answer.
                let bodyResult: Value option ref = ref None

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
                            // FG-179. PROVENANCE IS THE CELL'S IDENTITY, and that is the
                            // whole redesign. A binding is OURS if it is the very cell this
                            // code installed; anything else in that slot was bound by the
                            // script, whatever syntax put it there.
                            //
                            // WHAT IT REPLACES: one interpreter-wide shadow bit, DELETED
                            // by this change, which was set when the interpreter
                            // happened to notice `def env` or `env = …`. It was wrong for FIVE spellings — a wrapper-local
                            // `def env`, a local `env` map, a closure-local `def env` that
                            // poisoned a SIBLING wrapper for the rest of the run, a captured
                            // wrapper environment, and a PARAMETER named `env`, where the
                            // same code gave a different answer depending on what the
                            // parameter was called. Each fix was a guard keyed to syntax,
                            // and the next syntax was always outside it.
                            //
                            // Reference equality answers the question the bit was
                            // approximating, and it answers it PER FRAME: a `def`, a
                            // parameter and a catch variable all go through `Env.withVar`,
                            // which mints a NEW cell, so every one of them is distinguishable
                            // from ours without naming a single syntactic form. A sibling
                            // closure's binding lives in a different `Env` entirely and can
                            // no longer reach across.
                            //
                            // UPDATING THE CELL rather than rebinding the name is still
                            // required: a fresh `ref` detaches any closure that already
                            // captured `env`, which is the capture-by-value defect this
                            // change exists to remove, reintroduced at one name.
                            st.Host
                            |> Option.iter (fun h ->
                                let current = h.CurrentEnv() |> List.map (fun (k, v) -> k, VStr v)
                                let freshMap = ref (Map.ofList current)
                                // FG-193: the minted map IS the Jenkins environment,
                                // by value identity — an aliased write must route to
                                // the host however many rebinds separate it
                                st.JenkinsEnvMaps.Add freshMap |> ignore
                                let fresh = VMap freshMap

                                // FG-201. "Ours" is membership in JenkinsEnvCells — the set of
                                // every cell hosted machinery installed — NOT the one cell THIS
                                // invocation minted. The first version compared against a
                                // per-invocation local, so a wrapper NESTED inside another
                                // (dir { withEnv { … } }) found the outer wrapper's cell,
                                // failed the identity check, took the leave-it-alone arm, and
                                // its refresh silently never ran: `${env.A}` read the OUTER
                                // environment and interpolated null. Receipt
                                // `script-nested-wrappers-env` diverged on exactly that —
                                // green build, a:null/t:null — for four days before the
                                // FG-196 suite run caught it. Script-owned bindings still
                                // refuse the refresh: a `def env` mints its cell through
                                // `Env.withVar`, which never enters the set.
                                match Map.tryFind "env" bodyEnv.Value.Vars with
                                | Some cell when st.JenkinsEnvCells.Contains cell -> cell.Value <- fresh
                                | Some _ ->
                                    // the script's own `env` — leave it alone entirely
                                    ()
                                | None ->
                                    let cell = ref fresh
                                    st.JenkinsEnvCells.Add cell |> ignore

                                    bodyEnv.Value <-
                                        { bodyEnv.Value with Vars = Map.add "env" cell bodyEnv.Value.Vars })

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

                            // Same implicit/explicit return discipline as an ordinary
                            // closure invocation. Save LastValue because the body is a
                            // nested evaluation: its trailing expression is the wrapper's
                            // value, not the enclosing script's trailing expression.
                            let outerValue = st.LastValue

                            try
                                // A host owns invocation count and fault policy. Never let
                                // an earlier successful invocation survive into a later
                                // invocation which faults and is absorbed by that host.
                                bodyResult.Value <- None

                                try
                                    st.LastValue <- None
                                    let after = execBlock st bodyEnv.Value c.Body

                                    bodyEnv.Value <-
                                        { after with
                                            Vars = after.Vars |> Map.filter (fun k _ -> captured.Contains k) }

                                    bodyResult.Value <- Some(defaultArg st.LastValue VNull)
                                with ReturnSignal value ->
                                    bodyResult.Value <- Some value
                            finally
                                st.LastValue <- outerValue
                                st.Depth <- st.Depth - 1)

                match st.Host with
                | Some host ->
                    // LIVE: the host performs it here and its value is the call's value.
                    // The body is handed over UNEVALUATED so a wrapper can run it inside
                    // whatever context it establishes — the batch model evaluated it
                    // immediately and flattened it, which is how `dir('x') { … }` lost its
                    // directory.
                    // FG-202. A block-taking step has POPPED whatever overlay it pushed by
                    // the time Perform finishes, and a hosted `env` cell visible at THIS
                    // call site may still carry the body's last entry-refresh — so a read
                    // AFTER the block held the inner snapshot where Jenkins restores the
                    // outer (receipt `post-exit-env-read`: jenkins after:null — the shape
                    // the FG-201 verifier constructed, then measured). Re-refresh from the
                    // host, which reports the enclosing overlay again. IN A FINALLY,
                    // because a fault raised inside the body — thrown, divide-by-zero —
                    // leaves Perform by exception, can be caught by a script-level
                    // try/catch, and execution then CONTINUES with the stale cell; the
                    // first version refreshed after a plain return only, and the verifier
                    // constructed exactly that escape. The walker restores its overlay in
                    // its own finally before the exception reaches here, so CurrentEnv()
                    // is already the enclosing scope's on both exits. Script-owned
                    // bindings stay untouched for the FG-201 reason: their cells never
                    // enter JenkinsEnvCells. Refreshing even when the host declined to
                    // run the body only moves the cell TOWARD the live environment,
                    // which is where every hosted read should land.
                    try
                        ensureCanContinue ()
                        let phase = if st.FinallyUnwindDepth > 0 then FinallyUnwind else OrdinaryCall
                        let namedArgs = namedLazy.Value

                        // Jenkins hashes/coerces collection arguments before a
                        // hosted step runs. Even a direct-self collection whose
                        // ordinary toString uses `(this Collection)` overflows at
                        // this boundary. Check the typed values themselves: a
                        // display fallback must never become an executable arg.
                        if
                            (positionalArgs |> List.exists Value.hasReferenceCycle)
                            || (namedArgs |> List.exists (snd >> Value.hasReferenceCycle))
                        then
                            raise (Stop(CyclicValue HostedArgumentCoercion))

                        if
                            (positionalArgs |> List.exists Value.containsScmMap)
                            || (namedArgs |> List.exists (snd >> Value.containsScmMap))
                        then
                            raise (
                                Stop(
                                    HostedCallRefused
                                        "passing an SCM return map directly or inside a collection to a hosted step is not modelled"
                                )
                            )

                        let transportValue = host.Perform phase s positionalArgs namedArgs runBody
                        ensureCanContinue ()

                        // Only a host-declared block step gets the closure's result.
                        // Other hosted calls retain the value returned by Perform even if
                        // their syntax happened to carry a closure. A declared wrapper
                        // which returns without invoking its supplied body must refuse:
                        // value use has now been admitted, so inventing null here would be
                        // the same silent wrong answer this gate exists to prevent.
                        if takesBlock && runBody.IsSome then
                            match bodyResult.Value with
                            | Some value -> value
                            | None ->
                                raise (
                                    Stop(
                                        HostedCallRefused
                                            $"hosted wrapper `{s}` completed without executing its body, so no return value exists"
                                    )
                                )
                        else
                            transportValue
                    finally
                        if runBody.IsSome then
                            match Map.tryFind "env" env.Vars with
                            | Some cell when st.JenkinsEnvCells.Contains cell ->
                                let current = host.CurrentEnv() |> List.map (fun (k, v) -> k, VStr v)
                                let freshMap = ref (Map.ofList current)
                                st.JenkinsEnvMaps.Add freshMap |> ignore
                                cell.Value <- VMap freshMap
                            | _ -> ()
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
            | Some(Ok(Builtin b)) ->
                match Map.tryFind b env.Funcs with
                | Some candidates ->
                    // FG-195. RESOLUTION IS BY SIGNATURE, as Groovy's is — the model that
                    // replaced four one-at-a-time refusals, checked against all four shapes
                    // at once because the fourth was found inside the guard written for the
                    // third. The effective argument list counts Groovy's argument forms:
                    // a named-argument group is ONE Map argument, passed FIRST, and a
                    // trailing closure is a FINAL argument. Candidates are matched on
                    // arity; no match, or two declarations sharing one arity, is a refusal
                    // NAMING the attempted signature — never a silent nearest fit.
                    let effectiveArgs = effectiveArgList env positionalLazy.Value namedLazy.Value trailing

                    let arityOf (ps: string list, _) = List.length ps
                    let wanted = List.length effectiveArgs

                    match candidates |> List.filter (fun c -> arityOf c = wanted) with
                    | [] ->
                        let have =
                            candidates |> List.map (arityOf >> string) |> String.concat ", "

                        raise (
                            Stop(
                                Unsupported
                                    $"no declaration of '{b}' takes {wanted} argument(s) — candidates take {have}; a named-argument group counts as one Map argument and a trailing block as one final argument"
                            )
                        )
                    | _ :: _ :: _ ->
                        raise (
                            Stop(
                                Unsupported
                                    $"'{b}' is declared more than once with {wanted} parameter(s); Groovy rejects the duplicate signature and Fogell will not guess which body a call means"
                            )
                        )
                    | [ (ps, body) ] ->

                    st.Depth <- st.Depth + 1

                    // FG-179. A FUNCTION BODY DOES NOT SEE THE CALLER'S LOCALS. This
                    // folded the parameters onto the CALLER's `env`, which was merely
                    // wasteful while locals were copied and became a WRONG WRITE once they
                    // were cells: `def x = 'outer'; def change() { x = 'inner' }; change()`
                    // reached through and assigned the caller's `x`. A Groovy method cannot
                    // do that — it has no access to the calling frame's locals, and an
                    // assignment there goes to the script binding, which `st.Binding`
                    // already models and which the `EVar` fallback still reaches.
                    //
                    // `Funcs` is carried so recursion and mutual calls still resolve; only
                    // the VARIABLE scope is isolated. Raised in review on PR #54 as the
                    // second regression of the ref-cell change, and it is the same shape as
                    // the first: cells made an existing sloppiness observable.
                    let callEnv =
                        List.zip ps effectiveArgs
                        |> List.fold (fun acc (p, v) -> Env.withVar p v acc) { Env.empty with Funcs = env.Funcs }

                    // Depth decrements in a FINALLY — same leak class as
                    // `invokeClosureValue`, same verifier finding: a fault caught by a
                    // script-level try/catch skipped the decrement here too.
                    let result =
                        try
                            try
                                execBlock st callEnv body |> ignore
                                VNull
                            with ReturnSignal v ->
                                v
                        finally
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

            // Depth decrements in a FINALLY — the same leak class the verifier found
            // in `invokeClosureValue`, pre-existing here: a fault caught by a
            // script-level try/catch skipped the decrement and an innocent loop of
            // caught faults exhausted the call-depth budget where Jenkins runs on.
            let r =
                try
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
                finally
                    st.Depth <- st.Depth - 1

            r

        match name, recv, args with
        | "get", VScmMap scm, [ VStr key ] ->
            scm.Entries |> Map.tryFind key |> Option.map VStr |> Option.defaultValue VNull
        | "containsKey", VScmMap scm, [ VStr key ] -> VBool(Map.containsKey key scm.Entries)
        | "keySet", VScmMap scm, [] ->
            scm.Entries
            |> Map.toList
            |> List.map fst
            |> VScmKeySet
        | _, VScmMap _, _ ->
            raise (
                Stop(
                    Unsupported
                        $"method `{name}` is not modelled for an SCM return map; supported methods: get(String), containsKey(String), keySet()"
                )
            )
        | "join", VScmKeySet keys, [ VStr delimiter ] -> VStr(String.concat delimiter keys)
        | _, VScmKeySet _, _ ->
            raise (Stop(Unsupported $"method `{name}` is not modelled for an SCM return-map key set; supported method: join(String)"))
        | ("get" | "containsKey"), _, _ ->
            // These names are globally admitted by the sandbox only so the
            // nominal SCM receiver can reach the narrow arms above. Ordinary
            // maps and free calls remain unmodelled in both strict hosted
            // scripts and the lax `when` evaluator; falling through to null
            // there would silently change stage selection.
            raise (Stop(Unsupported $"method `{name}` is modelled only for an SCM return map"))
        | _, VJUnitSummary _, _ ->
            raise (Stop(Unsupported $"method `{name}` is not modelled for JUnit TestResultSummary; read totalCount, failCount, or skipCount"))
        // FG-189/FG-195. `f.call(x)` is the explicit spelling of closure invocation
        // with the same binding rules and refusal contract as `f(x)` — for a closure
        // held in a LOCAL. A closure held in the script BINDING (assigned without
        // `def`) reaches this arm through EVar and runs, while the bare `f()`
        // spelling consults `env.Vars` only and still denies it — a named residual
        // on the FG-195 ticket, in the safe (false-refusal) direction. The
        // named-argument group and a trailing block count as arguments here too,
        // through the same one place that forms every callable's argument list.
        | "call", VClosure(c, closureEnv), _ ->
            invokeClosureValue st "call" c closureEnv (effectiveArgList env args namedArgs trailing)
        | "size", ListLike xs, _
        | "length", ListLike xs, _ -> VInt(int64 xs.Value.Length)
        | "size", VStr s, _
        | "length", VStr s, _ -> VInt(int64 s.Length)
        | "size", VMap m, _ -> VInt(int64 m.Value.Count)
        | "isEmpty", ListLike xs, _ -> VBool(List.isEmpty xs.Value)
        | "isEmpty", VStr s, _ -> VBool(s = "")
        | "toString", v, _ -> VStr(scriptDisplay v)
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
        | "contains", ListLike xs, [ v ] ->
            // FG-191: through the cycle-aware equality, like `==` — a cyclic
            // element reports rather than recurses
            VBool(
                xs.Value
                |> List.exists (fun x ->
                    match Value.tryEq v x with
                    | Value.Answer r -> r
                    | Value.CycleDetected -> raise (Stop(CyclicValue Equality))
                    | Value.Unmodelled ->
                        raise (Stop(Unsupported "equality is not modelled for nominal Jenkins return values")))
            )
        | "replace", VStr s, [ VStr a; VStr b ] -> VStr(s.Replace(a, b))
        | ("split" | "tokenize"), VStr s, [ VStr d ] ->
            VList(ref (s.Split(d) |> Array.toList |> List.map VStr))
        | "readLines", VStr s, _ ->
            VList(ref (s.Replace("\r\n", "\n").Split('\n') |> Array.toList |> List.map VStr))
        | "join", ListLike xs, [ VStr d ] -> VStr(xs.Value |> List.map scriptDisplay |> String.concat d)
        | "reverse", ListLike xs, _ -> VList(ref (List.rev xs.Value))
        | "reverse", VStr s, _ -> VStr(System.String(s.ToCharArray() |> Array.rev))
        | "sort", _, _ when Option.isSome trailing ->
            raise (Stop(Unsupported "method 'sort' with a comparator/key closure is not modelled; Jenkins CPS/sandbox behavior is not ordinary list sorting"))
        | "sort", VRange _, _ -> raise (Stop RangeMutation)
        | "sort", VList xs, _ ->
            let compareSafely left right =
                match Value.tryCompare left right with
                | Value.Order order -> order
                | Value.OrderingCycleDetected -> raise (Stop(CyclicValue Ordering))
                | Value.Unorderable -> raise (Stop(Unsupported collectionOrderingRefusal))

            let sorted =
                try
                    List.sortWith compareSafely xs.Value
                with
                // FSharp.Core delegates to Array.Sort, which wraps comparator
                // exceptions. Unwrap only Fogell's own typed stop; every other
                // host exception remains visible instead of being misclassified.
                | :? System.InvalidOperationException as wrapped ->
                    match wrapped.InnerException with
                    | :? Stop as stopped -> raise stopped
                    | _ -> reraise ()

            xs.Value <- sorted
            VList xs
        | "first", ListLike xs, _ when not xs.Value.IsEmpty -> List.head xs.Value
        | "last", ListLike xs, _ when not xs.Value.IsEmpty -> List.last xs.Value
        | "keySet", VMap m, _ -> VList(ref (m.Value |> Map.toList |> List.map (fst >> VStr)))
        | "values", VMap m, _ -> VList(ref (m.Value |> Map.toList |> List.map snd))
        | "each", ListLike xs, _ ->
            match trailing with
            | Some c ->
                iterateLiveList st "each" xs (fun () -> true) (fun x -> applyClosure c env x |> ignore)
                recv
            | None -> recv
        | "collect", ListLike xs, _ ->
            match trailing with
            | Some c ->
                let collected = ResizeArray<Value>()
                iterateLiveList st "collect" xs (fun () -> true) (fun x -> collected.Add(applyClosure c env x))
                VList(ref (List.ofSeq collected))
            | None -> recv
        | "find", ListLike xs, _ ->
            match trailing with
            | Some c ->
                let mutable found = None

                iterateLiveList
                    st
                    "find"
                    xs
                    (fun () -> Option.isNone found)
                    (fun x -> if scriptTruthy (applyClosure c env x) then found <- Some x)

                Option.defaultValue VNull found
            | None -> recv
        | "findAll", ListLike xs, _ ->
            match trailing with
            | Some c ->
                let matches = ResizeArray<Value>()

                iterateLiveList
                    st
                    "findAll"
                    xs
                    (fun () -> true)
                    (fun x -> if scriptTruthy (applyClosure c env x) then matches.Add x)

                VList(ref (List.ofSeq matches))
            | None -> recv
        | "any", ListLike xs, _ ->
            match trailing with
            | Some c ->
                let mutable matched = false
                iterateLiveList st "any" xs (fun () -> not matched) (fun x -> matched <- scriptTruthy (applyClosure c env x))
                VBool matched
            | None -> VBool false
        | "every", ListLike xs, _ ->
            match trailing with
            | Some c ->
                let mutable allMatched = true
                iterateLiveList st "every" xs (fun () -> allMatched) (fun x -> allMatched <- scriptTruthy (applyClosure c env x))
                VBool allMatched
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

            // FG-179. WRITE THROUGH THE CELL, and return the SAME env. This used to call
            // `Env.withVar`, which mints a fresh cell in a fresh map — so the write was
            // visible only to whoever went on to hold that new map, and a closure assigning
            // to a captured name changed nothing its creator could see. Groovy captures by
            // REFERENCE: `marker = 'after'` inside a closure updates the enclosing
            // `marker`. Ten findings, each looking like a separate bug, were this one line.
            if Env.assign n value env then
                // The shared BINDING still never sees it — a local is a local. What changed
                // is which scopes count as sharing this local: every one holding the cell,
                // which is exactly the set that can see the variable in Groovy.
                env
            else
                // the script BINDING: created on first assignment (the advisory
                // case), mutated in place — immediately visible everywhere,
                // including after this closure returns and after a later fault
                if not (Map.containsKey n st.Binding) then
                    st.NewBindings <- (n, value) :: st.NewBindings

                st.Binding <- Map.add n value st.Binding
                env
        | SAssign(target, _) when Ast.assignmentTargetContainsSpreadProperty target ->
            // The generic fallback used to evaluate this target, evaluate the RHS,
            // and silently perform no write. Jenkins instead throws without mutating.
            // The public execution seam rejects the whole pipeline before workspace
            // preparation; this guard keeps direct interpreter consumers from ever
            // recovering the old RHS-only success.
            raise (Stop(Unsupported spreadAssignmentRefusal))
        // REVIEW FIX (Codex, PR #14 round 7): only the EVar form recorded a value, so
        // a predicate ending in `env.DEPLOY = false` or `values[0] = false` reached
        // here and left LastValue absent or STALE — reported unevaluable, or worse
        // reusing an earlier truthy value. Groovy assignments yield their RHS whatever
        // the target shape is.
        | SAssign(target, v) ->
            // The RECEIVER (and a computed index) evaluate BEFORE the RHS —
            // Groovy's order, which the blanket whole-target eval this replaces
            // also preserved. Only the receiver: evaluating the property READ too
            // double-evaluated computed receivers and faulted on shapes whose
            // WRITE is the thing being decided below.
            let recvAndKey =
                match target with
                | EProp(r, name) -> evalExpr st env r, VStr name, false
                | ESafeProp(r, name) ->
                    // Assignment is not a safe READ. Jenkins 2.568.1 mutates a
                    // non-null map exactly as ordinary property assignment does,
                    // and raises a catchable NPE for null — including after an
                    // index or method-call result. Never short-circuit the write.
                    evalExpr st env r, VStr name, false
                | EIndex(r, idx) ->
                    let rv = evalExpr st env r
                    rv, evalExpr st env idx, true
                | _ -> raise (Stop(Unsupported assignmentTargetRefusal))

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
            // FG-179/FG-193. WHICH env, and WHICH map? Decided by IDENTITY, never by
            // name — first the cell's (a script's own `def env = [:]` owns a plain
            // map), and now the VALUE's: `def saved = env; def env = saved` mints a
            // fresh cell outside the cell set while the value it carries is still
            // the Jenkins map, and `def other = local` aliases a plain map that
            // mutation must reach THROUGH the shared ref, not by replacing one
            // binding's copy. Both measured: the aliased env write reached Jenkins'
            // shell and vanished here; the aliased map write printed alias:x there
            // and alias:null here — this arm's old name-keyed match dropped every
            // non-`env`-named target at `| _ -> ()`.
            let assignInto (recv: Value) (keyValue: Value) (isIndex: bool) =
                match recv, keyValue, isIndex with
                | VList items, VInt index, true -> listWrite st items index value
                | VList _, _, true -> raise (Stop(Unsupported listIndexAssignmentRefusal))
                | VRange _, _, true -> raise (Stop RangeMutation)
                | VMap mr, _, _ ->
                    let key = scriptHashKey keyValue

                    match st.Host with
                    | Some host when st.JenkinsEnvMaps.Contains mr ->
                        // the Jenkins environment, however many rebinds away: the
                        // host decides, and today it refuses — the same named
                        // refusal as the direct spelling, instead of a silent local
                        // write the shell never sees
                        host.SetEnv key (scriptDisplay value)
                    | _ -> mr.Value <- Map.add key value mr.Value
                | VScmMap _, _, _
                | VScmKeySet _, _, _ ->
                    raise (Stop(Unsupported "SCM return-map mutation is not modelled"))
                | VJUnitSummary _, _, _ ->
                    raise (Stop(Unsupported "JUnit TestResultSummary mutation is not modelled"))
                | VNull, _, _ ->
                    let targetName = if isIndex then "index" else scriptDisplay keyValue
                    raise (Stop(NullReceiverAssignment targetName))
                // Not a supported receiver: Groovy REJECTS the write
                // (MissingPropertyException on a String) and a script-level catch INTERCEPTS it —
                // measured: `try { s.FOO = 'x' } catch (Exception e)` runs on and
                // SUCCEEDS on Jenkins. So the strict fault must be CATCHABLE:
                // UnknownProperty is what the old blanket target-eval raised here,
                // and the first spelling of this arm raised Unsupported instead —
                // uncatchable, a false refusal the verifier caught by asking what
                // class the fault was, not whether it fired. Assignment cannot
                // inherit lax READ semantics: an absent write is never success.
                | _, _, true -> raise (Stop(RejectedIndexOperation "write"))
                | _, _, false -> raise (Stop(UnknownProperty(scriptDisplay keyValue)))

            match recvAndKey with
            | recv, key, isIndex -> assignInto recv key isIndex

            env
        | SIndexCompoundAssign(target, op, rhs) ->
            let recv, keyValue =
                match target with
                | EIndex(r, idx) ->
                    let receiver = evalExpr st env r
                    receiver, evalExpr st env idx
                | _ -> raise (Stop(Unsupported assignmentTargetRefusal))

            // Read the old value before the RHS. Jenkins faults here for a
            // too-negative index, null receiver, or non-integer list key, so RHS
            // effects must not run for those compound-update failures.
            let oldValue, writeBack, listIndex =
                match recv, keyValue with
                | VList items, VInt index ->
                    listRead items index, (fun value -> listWrite st items index value), Some index
                | VList _, _ -> raise (Stop(Unsupported listIndexAssignmentRefusal))
                | VRange values, VInt index ->
                    let items = ref (Value.rangeItems values)
                    listRead items index, (fun _ -> raise (Stop RangeMutation)), Some index
                | VRange _, _ -> raise (Stop(Unsupported listIndexAssignmentRefusal))
                | VMap mr, _ ->
                    let key = scriptHashKey keyValue
                    let prior = defaultArg (Map.tryFind key mr.Value) VNull

                    prior,
                    (fun value ->
                        match st.Host with
                        | Some host when st.JenkinsEnvMaps.Contains mr -> host.SetEnv key (scriptDisplay value)
                        | _ -> mr.Value <- Map.add key value mr.Value),
                    None
                | VStr value, VInt index ->
                    stringIndexRead value index,
                    (fun _ -> raise (Stop(RejectedIndexOperation "write"))),
                    None
                | VNull, _ -> raise (Stop(NullReceiverAssignment "index"))
                | VScmMap _, _
                | VScmKeySet _, _ ->
                    raise (Stop(Unsupported "SCM return-map compound index mutation is not modelled"))
                | VJUnitSummary _, _ ->
                    raise (Stop(Unsupported "JUnit TestResultSummary compound index mutation is not modelled"))
                | _, _ -> raise (Stop(RejectedIndexOperation "read"))

            let rhsValue = evalExpr st env rhs

            let value =
                try
                    evalBinaryValues st op oldValue rhsValue
                with
                | Stop(Unsupported _) when oldValue = VNull && Option.isSome listIndex ->
                    raise (Stop(NullListIndexUpdate listIndex.Value))
                | Stop(Unsupported _) when Option.isNone listIndex && (match recv with VStr _ -> true | _ -> false) ->
                    raise (Stop(RejectedIndexOperation "operator"))

            writeBack value
            st.LastValue <- Some value
            env
        | SIndexPostfixAssign(target, op) ->
            let recv, keyValue =
                match target with
                | EIndex(r, idx) ->
                    let receiver = evalExpr st env r
                    receiver, evalExpr st env idx
                | _ -> raise (Stop(Unsupported assignmentTargetRefusal))

            let oldValue, writeBack, listIndex =
                match recv, keyValue with
                | VList items, VInt index ->
                    listRead items index, (fun value -> listWrite st items index value), Some index
                | VList _, _ -> raise (Stop(Unsupported listIndexAssignmentRefusal))
                | VRange values, VInt index ->
                    let items = ref (Value.rangeItems values)
                    listRead items index, (fun _ -> raise (Stop RangeMutation)), Some index
                | VRange _, _ -> raise (Stop(Unsupported listIndexAssignmentRefusal))
                | VMap mr, _ ->
                    let key = scriptHashKey keyValue
                    let prior = defaultArg (Map.tryFind key mr.Value) VNull

                    prior,
                    (fun value ->
                        match st.Host with
                        | Some host when st.JenkinsEnvMaps.Contains mr -> host.SetEnv key (scriptDisplay value)
                        | _ -> mr.Value <- Map.add key value mr.Value),
                    None
                | VStr value, VInt index ->
                    stringIndexRead value index,
                    (fun _ -> raise (Stop(RejectedIndexOperation "write"))),
                    None
                | VNull, _ -> raise (Stop(NullReceiverAssignment "index"))
                | VScmMap _, _
                | VScmKeySet _, _ ->
                    raise (Stop(Unsupported "SCM return-map postfix index mutation is not modelled"))
                | VJUnitSummary _, _ ->
                    raise (Stop(Unsupported "JUnit TestResultSummary postfix index mutation is not modelled"))
                | _, _ -> raise (Stop(RejectedIndexOperation "read"))

            let value =
                match oldValue, listIndex with
                | VNull, Some index -> raise (Stop(NullListIndexUpdate index))
                | _ ->
                    try
                        evalBinaryValues st op oldValue (VInt 1L)
                    with
                    | Stop(Unsupported _) when (match recv with VStr _ -> true | _ -> false) ->
                        raise (Stop(RejectedIndexOperation "operator"))
            writeBack value
            st.LastValue <- Some oldValue
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

            if scriptTruthy (evalExpr st env c) then
                execBlock st env t
            else
                execBlock st env f
        | SForIn(v, src, body) ->
            st.LastValue <- None

            match evalExpr st env src with
            | ListLike xs ->
                // FG-194. `break` LEAVES THE LOOP. This caught `BreakSignal` per ITERATION
                // and carried on with the next element, so `for (x in xs) { … break }` ran
                // the body for every element — `SWhile` one arm below has always stopped
                // correctly, which is what makes this a transcription slip rather than a
                // design question.
                //
                // PRE-EXISTING, NOT A REF-CELL REGRESSION, and worth separating because
                // three findings on this branch WERE regressions of that change: the
                // `with BreakSignal -> ()` line predates it. What cells changed is
                // visibility — an assignment made before the break now survives, so the
                // wrong number reaches a step instead of being discarded with the
                // environment. Raised in review on PR #54.
                // The ref cell makes this a mutable ArrayList analogue. Jenkins CPS
                // re-reads each not-yet-visited index, including appended/extended
                // elements, so retaining an immutable F# tail is observably stale.
                let mutable cur = env
                let mutable running = true

                iterateLiveList
                    st
                    "loop"
                    xs
                    (fun () -> running)
                    (fun x ->
                        try
                            cur <- execBlock st (Env.withVar v x cur) body
                        with
                        | ContinueSignal -> ()
                        | BreakSignal -> running <- false)

                cur
            | VScmKeySet _ ->
                raise (Stop(Unsupported "iteration over an SCM return-map key set is not modelled; use join(String)"))
            | _ -> env
        | SWhile(c, body) ->
            st.LastValue <- None

            let mutable cur = env
            let mutable iterations = 0
            let mutable running = true

            while running && scriptTruthy (evalExpr st cur c) do
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
                    // The SAME equality `==` uses, not a second opinion: a
                    // switch that matched by a different rule than the operator would be
                    // its own defect, and the lowered form got this right by construction
                    // because it literally built an `EBinary("==", …)`. FG-191: the same
                    // cycle-aware walk too, for the same process-death reason.
                    | Some case ->
                        match Value.tryEq v (evalExpr st env case) with
                        | Value.Answer r -> r
                        | Value.CycleDetected -> raise (Stop(CyclicValue Equality))
                        | Value.Unmodelled -> raise (Stop(Unsupported "switch equality is not modelled for nominal Jenkins return values"))
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
                // THAT LIMIT IS GONE, and it went the way the note predicted. This said an
                // assignment inside a NESTED block before a break — `case 'a': if (x) { y = 1;
                // break }` — was still lost, because the block is its own `execBlock` whose
                // return the unwind skips, and that both this and `SWhile`'s identical shape
                // "dissolve when FG-179 makes variables ref cells". FG-179 landed and they
                // did: receipt `script-break-keeps-assignment` holds both shapes, the switch
                // arm yielding `y:[1]` and the `while` body `z:[1]`, where each previously
                // kept the pre-block value.
                // A write now goes THROUGH the variable's cell, so no unwind can skip it.
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
        | SThrow e ->
            let thrown = evalExpr st env e

            if Value.containsScmMap thrown then
                raise (Stop(Unsupported "throwing an SCM return map directly or inside a collection is not modelled"))
            elif Value.containsJUnitSummary thrown then
                raise (Stop(Unsupported "throwing a JUnit TestResultSummary directly or inside a collection is not modelled"))
            else
                raise (Stop(Thrown thrown))
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

            // A null assignment receiver has Java/Groovy NPE ancestry. Keep this
            // separate from MissingPropertyException so a narrow typed catch does
            // not intercept the wrong runtime class.
            let catchesNullReceiverAssignment =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    [ "Exception"; "Throwable"; "RuntimeException"; "NullPointerException" ]
                    |> List.contains t
                | None -> false

            let catchesListIndexOutOfBounds =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    [ "Exception"
                      "Throwable"
                      "RuntimeException"
                      "IndexOutOfBoundsException"
                      "ArrayIndexOutOfBoundsException" ]
                    |> List.contains t
                | None -> false

            let catchesRejectedIndexOperation =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    [ "Exception"
                      "Throwable"
                      "RuntimeException"
                      "SecurityException"
                      "RejectedAccessException" ]
                    |> List.contains t
                | None -> false

            let catchesStringIndexOutOfBounds =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    [ "Exception"
                      "Throwable"
                      "RuntimeException"
                      "IndexOutOfBoundsException"
                      "StringIndexOutOfBoundsException" ]
                    |> List.contains t
                | None -> false

            let catchesStackOverflow =
                match catch with
                | Some(None, _, _) -> false
                | Some(Some t, _, _) ->
                    [ "Throwable"; "Error"; "VirtualMachineError"; "StackOverflowError" ]
                    |> List.contains t
                | None -> false

            let catchesRangeMutation =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    [ "Exception"; "Throwable"; "RuntimeException"; "UnsupportedOperationException" ]
                    |> List.contains t
                | None -> false

            // Which declared types can intercept a failed shell step. Jenkins
            // surfaces it as hudson.AbortException < IOException < Exception —
            // NOT a RuntimeException, so `catch (RuntimeException e)` must let
            // it escape where the MissingProperty list would catch. Measured at
            // the untyped/`Exception` spellings (receipt `script-try-catches-sh-failure`);
            // IOException is Groovy-default-imported ancestry, unprobed, noted.
            // BARE `AbortException` IS DELIBERATELY ABSENT: `hudson.*` is not a
            // Groovy default import and the script arm discards preamble
            // imports, so Jenkins likely fails to RESOLVE the name where a
            // catch here would recover — a false-success risk the verifier
            // constructed. Escaping instead fails the build on both engines.
            // (This absence shipped one commit late: the edit died in an
            // aborted batch script while the board already claimed it, and
            // Copilot caught the code contradicting the row on PR #95.)
            let catchesStepFailure =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) -> [ "Exception"; "Throwable"; "IOException" ] |> List.contains t
                | None -> false

            let catchesBindingFailure exceptionClass =
                match catch with
                | Some(None, _, _) -> true
                | Some(Some t, _, _) ->
                    let concrete =
                        match exceptionClass with
                        | IllegalArgumentException -> "IllegalArgumentException"
                        | NullPointerException -> "NullPointerException"

                    [ "Exception"; "Throwable"; "RuntimeException"; concrete ]
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
                    | Stop(StepBindingFailed(name, exceptionClass, detail)) when catchesBindingFailure exceptionClass ->
                        handle (VStr $"{BindingExceptionClass.fullName exceptionClass}: {name}: {detail}")
                    | Stop(StepFailed(name, exceptionText, _)) when catchesStepFailure ->
                        // FG-176. The step's own ERROR narration already reached the
                        // console at dispatch; what the handler binds is the exception
                        // Jenkins hands a catch.
                        handle (VStr exceptionText)
                    | Stop(UnknownProperty n) when catchesMissingProperty ->
                        handle (VStr $"groovy.lang.MissingPropertyException: No such property: {n}")
                    | Stop(NullReceiverAssignment target) when catchesNullReceiverAssignment ->
                        handle (VStr $"java.lang.NullPointerException: Cannot assign through null {target} receiver")
                    | Stop(ListIndexOutOfBounds(index, size)) when catchesListIndexOutOfBounds ->
                        handle (VStr $"java.lang.ArrayIndexOutOfBoundsException: Negative array index [{index}] too large for array size {size}")
                    | Stop(NullListIndexUpdate index) when catchesNullReceiverAssignment ->
                        handle (VStr $"java.lang.NullPointerException: Cannot apply index update to null value at [{index}]")
                    | Stop(RejectedIndexOperation phase) when catchesRejectedIndexOperation ->
                        handle (VStr $"org.jenkinsci.plugins.scriptsecurity.sandbox.RejectedAccessException: scalar index {phase} rejected")
                    | Stop(StringIndexOutOfBounds(index, size)) when catchesStringIndexOutOfBounds ->
                        handle (VStr $"java.lang.StringIndexOutOfBoundsException: String index out of range: {index}; size {size}")
                    | Stop(CyclicValue operation) when catchesStackOverflow ->
                        let boundary =
                            match operation with
                            | Display -> "display"
                            | Equality -> "equality comparison"
                            | Ordering -> "ordering comparison"
                            | HostedArgumentCoercion -> "hosted argument coercion"
                            | HashKey -> "hash-key coercion"

                        handle (VStr $"java.lang.StackOverflowError: cyclic structure in {boundary}")
                    | Stop RangeMutation when catchesRangeMutation ->
                        handle (VStr "java.lang.UnsupportedOperationException: IntRange is immutable")
                with
                | HostedHaltSignal as halted ->
                    // A hosted failure has already stopped ordinary evaluation, but
                    // Groovy still executes `finally` while that control flow unwinds.
                    // Only this dynamic extent bypasses CanContinue. A cleanup fault,
                    // return, break or continue naturally replaces the original signal;
                    // a normal cleanup completion rethrows the original halt.
                    st.FinallyUnwindDepth <- st.FinallyUnwindDepth + 1

                    try
                        execBlock st cur fin |> ignore
                    finally
                        st.FinallyUnwindDepth <- st.FinallyUnwindDepth - 1

                    raise halted
                | e ->
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
        // FG-188. A function bound in the INCOMING env is defined too. This counted only
        // declarations inside the script, so a caller that supplied helpers in `env.Funcs`
        // had them denied by name at `Sandbox.admitCall` before the call could resolve —
        // the env said the function existed and the sandbox said it did not.
        let defined =
            Set.union
                (Ast.definedFunctions script)
                (env.Funcs |> Map.toSeq |> Seq.map fst |> Set.ofSeq)


        // FG-179. The `env` the CALLER supplied is Jenkins'; anything the script binds
        // later is its own. Identity is recorded once, here, rather than inferred from
        // syntax at each assignment.
        let jenkinsEnvCells = System.Collections.Generic.HashSet<Value ref>(HashIdentity.Reference)

        let jenkinsEnvMaps =
            System.Collections.Generic.HashSet<Map<string, Value> ref>(HashIdentity.Reference)

        match Map.tryFind "env" env.Vars with
        | Some cell ->
            jenkinsEnvCells.Add cell |> ignore

            // FG-193: the caller-seeded env VALUE is the Jenkins map by identity,
            // exactly as its cell is the Jenkins cell
            match cell.Value with
            | VMap mr -> jenkinsEnvMaps.Add mr |> ignore
            | _ -> ()
        | None -> ()

        let st =
            { Steps = 0
              Depth = 0
              FinallyUnwindDepth = 0
              Effects = []
              Budget = budget
              RegisteredSteps = registeredSteps
              Host = host
              Defined = defined
              LastValue = None
              StrictVars = strictVars
              NewBindings = []
              JenkinsEnvCells = jenkinsEnvCells
              JenkinsEnvMaps = jenkinsEnvMaps
              // FG-179: the BINDING is a value map, not cells — it is Groovy's script
              // binding, already shared and mutable in its own right, so it needs no second
              // sharing mechanism. Seeded from a SNAPSHOT of the caller's locals.
              Binding = Env.snapshot env }

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
              Env = { Env.ofValues st.Binding with Funcs = final.Funcs }
              NewBindings = List.rev st.NewBindings
              // Groovy's last-expression-is-the-value, for any statement block.
              Returned = st.LastValue }
        with
        | HostedHaltSignal ->
            { Effects = List.rev st.Effects
              Fault = None
              Env = { Env.ofValues st.Binding with Funcs = hoisted.Funcs }
              NewBindings = List.rev st.NewBindings
              Returned = None }
        | Stop f ->
            { Effects = List.rev st.Effects
              Fault = Some f
              // NOT the pristine environment: assignments made BEFORE the fault were
              // performed — `${x = 'kept'; MISSING}` fails, and Jenkins' post block
              // still reads x. The mutable binding survives the raise by nature.
              Env = { Env.ofValues st.Binding with Funcs = hoisted.Funcs }
              NewBindings = List.rev st.NewBindings
              Returned = None }
        | ReturnSignal v ->
            { Effects = List.rev st.Effects
              Fault = None
              Env = { Env.ofValues st.Binding with Funcs = hoisted.Funcs }
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

    /// FG-176. The HOST's one sanctioned channel for surfacing a failed shell
    /// step INTO the running script as the catchable, retryable fault Jenkins
    /// raises there. `Stop` itself stays private — a host that could fabricate
    /// arbitrary interpreter control flow would make every fault contract here
    /// advisory — so this constructor is deliberately narrow: one fault kind,
    /// carrying only the step's name.
    // NoInlining on all three: F#'s cross-module optimizer inlines small
    // bodies into the CALLER'S assembly, where the private Stop constructor
    // is not accessible — a MethodAccessException at runtime, found by the
    // first probe of this seam.
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let raiseStepFailed (stepName: string) (exceptionText: string) (diagnosticText: string) : 'a =
        raise (Stop(StepFailed(stepName, exceptionText, diagnosticText)))

    /// FG-177. The host's narrow channel for one measured Jenkins binding failure.
    /// It is distinct from a modelling refusal: scripted try/catch may absorb this,
    /// while it must never absorb a gap in Fogell's implementation.
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let raiseStepBindingFailed (stepName: string) (exceptionClass: BindingExceptionClass) (detail: string) : 'a =
        raise (Stop(StepBindingFailed(stepName, exceptionClass, detail)))

    /// A call-shape refusal is not a Groovy-catchable Jenkins exception, but it
    /// cannot commit failure before surrounding `finally` control flow decides
    /// whether the refusal escapes. The wrapper/top-level owner converts an
    /// escaping signal into the same durable refusal as before.
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let raiseHostedCallRefused (detail: string) : 'a =
        raise (Stop(HostedCallRefused detail))

    /// A status-only cleanup halt is deliberately deferred until it escapes
    /// surrounding finally control flow. The call has already narrated its own
    /// failure; the eventual owner only publishes status and durability state.
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let raiseHostedStepHalted (stepName: string) (haltKind: HostedStepHaltKind) (diagnosticText: string) : 'a =
        raise (Stop(HostedStepHalted(stepName, haltKind, diagnosticText)))

    /// A hosted wrapper owns the context it gives its body. When that child
    /// context halts, the interpreter signal must return control to the wrapper
    /// so retry can inspect the attempt and either retry or publish its failure;
    /// letting it escape to runWith skips that bookkeeping and can turn an
    /// exhausted retry into success. It also transfers the one deliberately
    /// deferred, catch-opaque call-refusal signal: wrappers turn that signal
    /// into their child's durable halt only after surrounding `finally` control
    /// flow has allowed it to escape.
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let catchHostedHalt (body: unit -> unit) : HostedBodyHalt option =
        try
            body ()
            None
        with
        | HostedHaltSignal ->
            Some BranchHalt
        | Stop(HostedCallRefused detail) ->
            Some(CallRefusalHalt detail)
        | Stop(HostedStepHalted(stepName, haltKind, diagnosticText)) ->
            Some(StepStatusHalt(stepName, haltKind, diagnosticText))

    /// FG-186. Run a retry attempt's body, converting the CATCHABLE fault
    /// classes — a throw, a missing property, a failed shell step — into a
    /// returned fault the loop treats as attempt failure. A refusal or an
    /// exhausted budget re-raises untouched: catching Fogell's own modelling
    /// gaps would diverge silently from an engine that has no such gap.
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let catchRetryable (body: unit -> unit) : Fault option =
        try
            body ()
            None
        with Stop((Thrown _ | UnknownProperty _ | NullReceiverAssignment _ | ListIndexOutOfBounds _ | NullListIndexUpdate _ | RejectedIndexOperation _ | StringIndexOutOfBounds _ | CyclicValue _ | RangeMutation | StepFailed _ | StepBindingFailed _) as f) ->
            Some f

    /// FG-186's other half: the FINAL attempt re-raises the held fault so an
    /// uncaught failure carries its own diagnostic out, not a generic
    /// exhausted-retries one. Narrow on purpose, like [raiseStepFailed].
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
    let reraiseFault (f: Fault) : 'a = raise (Stop f)
