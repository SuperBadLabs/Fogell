namespace Fogell.Groovy

open Fogell.Ir

/// AST for the subset of Groovy that Jenkins pipelines actually use: scripted
/// bodies, `script { }` escapes inside Declarative, and shared-library globals.
///
/// Not full Groovy, and deliberately so. ADR 0002 accepted interpretation as
/// the price of coverage, but an interpreter's attack surface is its grammar —
/// so anything not measured in the corpus is not represented here. Corpus
/// demand among the 119 Jenkins-ready files drove every case below:
/// `def` 56, scripted `node{}` 51, `script{}` 32, method dispatch 40,
/// `if` 35, arithmetic 33, try/catch 18, closures 9.
type Expr =
    | ENull
    | EBool of bool
    | EInt of int64
    | EStr of string
    /// "double-quoted with ${...}" — parts kept separate so interpolation is an
    /// interpreter concern, not a lexer one.
    | EGString of GStringPart list
    | EList of Expr list
    | EMap of (string * Expr) list
    | EVar of string
    | EProp of target: Expr * name: string
    /// `a*.b` — Groovy's spread-safe property projection. This must remain
    /// distinct from [EProp]: a list receiver projects in order, while ordinary
    /// property access reads one receiver and must not acquire collection semantics.
    | ESpreadProp of target: Expr * name: string
    /// `a?.b` — Groovy's safe navigation. Distinct from [EProp] because the
    /// difference IS the semantics: a null receiver yields null instead of a
    /// property lookup, so collapsing the two made `${env.OPTIONAL?.value}`
    /// fail a build Jenkins runs.
    | ESafeProp of target: Expr * name: string
    | EIndex of target: Expr * index: Expr
    | EUnary of op: string * operand: Expr
    | EBinary of op: string * left: Expr * right: Expr
    | ETernary of cond: Expr * ifTrue: Expr * ifFalse: Expr
    | EElvis of Expr * Expr
    | ECall of target: CallTarget * args: Arg list * trailing: Closure option
    | EClosure of Closure

and CallTarget =
    /// A bare call: a pipeline step, or a user function defined in this file.
    | FreeCall of name: string
    | MethodCall of target: Expr * name: string
    /// `a?.b(...)` — the whole CALL short-circuits to null on a null receiver,
    /// which neither a safe property read nor an unsafe call can express.
    | SafeMethodCall of target: Expr * name: string

and GStringPart =
    | GLit of string
    | GExpr of Expr

and Arg =
    | APos of Expr
    | ANamed of name: string * value: Expr

and Closure =
    { Params: string list
      Body: Stmt list }

and Stmt =
    | SExpr of Expr
    | SDef of name: string * value: Expr option
    | SAssign of target: Expr * value: Expr
    | SIf of cond: Expr * thenBranch: Stmt list * elseBranch: Stmt list
    | SForIn of var: string * source: Expr * body: Stmt list
    | SWhile of cond: Expr * body: Stmt list
    /// FG-183. `switch (subject) { case a: … default: … }`, arms in SOURCE ORDER, `None`
    /// marking `default`.
    ///
    /// THIS NODE EXISTS BECAUSE ITS ABSENCE COST THREE CONSECUTIVE DEFECTS. The parser
    /// lowered a switch to nested `SIf`s — faithful for the arm bodies, and it discarded
    /// the one thing `break` depends on: a switch IS a break boundary, and afterwards
    /// nothing was left to say so. Each attempt to compensate downstream (consume the
    /// arm-final `break`; then refuse the rest) was right about the shape it was written
    /// for and wrong about the neighbouring one, ending in an over-refusal of
    /// `case 'a': if (true) break`, which Groovy accepts and runs on past. Context that a
    /// later stage needs cannot be destroyed by an earlier one.
    ///
    /// It also makes FALLTHROUGH expressible, which the lowering could not manage at all:
    /// nested ifs are mutually exclusive, so an arm without a `break` stopped rather than
    /// continuing into the next. That was recorded as a known gap on the grounds that
    /// corpus switches all break or return — true of the corpus, and not of Groovy.
    | SSwitch of subject: Expr * arms: (Expr option * Stmt list) list
    | SReturn of Expr option
    | SBreak
    | SContinue
    | SThrow of Expr
    /// catch is (declared exception type, binding, handler). The TYPE decides what
    /// the clause can catch: `catch (ArithmeticException e)` does not intercept a
    /// MissingPropertyException, and treating every clause as catch-all executed
    /// fallbacks Groovy never runs.
    | STry of body: Stmt list * catch: (string option * string option * Stmt list) option * finallyBlock: Stmt list
    /// `def name(a, b) { … }` — bound as a callable in the enclosing scope. This
    /// is how a Jenkinsfile's own helper becomes a live function, and it is the
    /// single most common escape construct in the corpus (56 files).
    | SFunc of name: string * parameters: string list * body: Stmt list

type Script = Stmt list

module Ast =

    let rec countStmts (stmts: Stmt list) : int =
        stmts
        |> List.sumBy (fun s ->
            1
            + match s with
              | SIf(_, a, b) -> countStmts a + countStmts b
              | SForIn(_, _, b)
              | SWhile(_, b) -> countStmts b
              // Every arm body counts: the admission bound must see the whole switch,
              // not just its subject.
              | SSwitch(_, arms) -> arms |> List.sumBy (snd >> countStmts)
              | STry(b, c, f) ->
                  countStmts b
                  + (match c with
                     | Some(_, _, cb) -> countStmts cb
                     | None -> 0)
                  + countStmts f
              | SFunc(_, _, b) -> countStmts b
              | SExpr _
              | SDef _
              | SAssign _
              | SReturn _
              | SBreak
              | SContinue
              | SThrow _ -> 0)

    /// True only when spread-property syntax participates in the l-value
    /// receiver chain. An index key, call arguments (including named and
    /// trailing-closure arguments), and the receiver feeding a method call all
    /// compute a value. Method and index results are fresh l-value receiver
    /// boundaries; spread reads below either are not writes through a projection.
    ///
    /// Jenkins 2.568.1 accepts actual spread write paths but raises a catchable
    /// runtime exception without mutating the elements. Fogell does not model
    /// that write boundary yet, so execution preflight and the interpreter's
    /// defensive fallback share this deliberately directional traversal.
    let rec assignmentTargetContainsSpreadProperty (expr: Expr) : bool =
        match expr with
        | ESpreadProp _ -> true
        | EProp(target, _)
        | ESafeProp(target, _) -> assignmentTargetContainsSpreadProperty target
        | EIndex _
        | ECall _ -> false
        | ENull
        | EBool _
        | EInt _
        | EStr _
        | EGString _
        | EList _
        | EMap _
        | EVar _
        | EUnary _
        | EBinary _
        | ETernary _
        | EElvis _
        | EClosure _ -> false

    /// A direct index l-value whose receiver was computed from spread projection
    /// is distinct from an outer write on the value returned by indexing. Jenkins
    /// executes both, but the direct form can be list-index assignment: simple and
    /// compound writes can update only the temporary projection, while another
    /// index or a method such as `first()` can select a referenced source list and
    /// persist. Calls on a spread-derived RECEIVER preserve that provenance; free
    /// calls and every call input remain value boundaries. Static analysis cannot
    /// prove whether the final receiver is a list or map, so the direct-index form
    /// is deliberately refused as FG-015b while outer writes stay admitted.
    let assignmentTargetIsSpreadDerivedIndex (expr: Expr) : bool =
        let rec receiverUsesSpreadBeforeBoundary value =
            match value with
            | ESpreadProp _ -> true
            | EProp(target, _)
            | ESafeProp(target, _)
            | EIndex(target, _) -> receiverUsesSpreadBeforeBoundary target
            | ECall(MethodCall(target, _), _, _)
            | ECall(SafeMethodCall(target, _), _, _) -> receiverUsesSpreadBeforeBoundary target
            | ECall(FreeCall _, _, _) -> false
            | ENull
            | EBool _
            | EInt _
            | EStr _
            | EGString _
            | EList _
            | EMap _
            | EVar _
            | EUnary _
            | EBinary _
            | ETernary _
            | EElvis _
            | EClosure _ -> false

        match expr with
        | EIndex(target, _) -> receiverUsesSpreadBeforeBoundary target
        | _ -> false

    let rec private containsAssignmentWhere targetMatches (stmts: Stmt list) : bool =
        let rec expressionHasNestedAssignment expr =
            match expr with
            | EClosure closure -> containsAssignmentWhere targetMatches closure.Body
            | EGString parts ->
                parts
                |> List.exists (function
                    | GLit _ -> false
                    | GExpr value -> expressionHasNestedAssignment value)
            | EList values -> values |> List.exists expressionHasNestedAssignment
            | EMap entries -> entries |> List.exists (snd >> expressionHasNestedAssignment)
            | EProp(target, _)
            | ESpreadProp(target, _)
            | ESafeProp(target, _)
            | EUnary(_, target) -> expressionHasNestedAssignment target
            | EIndex(target, index)
            | EBinary(_, target, index)
            | EElvis(target, index) ->
                expressionHasNestedAssignment target || expressionHasNestedAssignment index
            | ETernary(cond, yes, no) ->
                expressionHasNestedAssignment cond
                || expressionHasNestedAssignment yes
                || expressionHasNestedAssignment no
            | ECall(target, args, trailing) ->
                let receiverHasAssignment =
                    match target with
                    | FreeCall _ -> false
                    | MethodCall(receiver, _)
                    | SafeMethodCall(receiver, _) -> expressionHasNestedAssignment receiver

                receiverHasAssignment
                || (args
                    |> List.exists (function
                        | APos value -> expressionHasNestedAssignment value
                        | ANamed(_, value) -> expressionHasNestedAssignment value))
                || (trailing |> Option.exists (fun closure -> containsAssignmentWhere targetMatches closure.Body))
            | ENull
            | EBool _
            | EInt _
            | EStr _
            | EVar _ -> false

        stmts
        |> List.exists (function
            | SAssign(target, value) ->
                targetMatches target
                || expressionHasNestedAssignment target
                || expressionHasNestedAssignment value
            | SExpr expr -> expressionHasNestedAssignment expr
            | SDef(_, value)
            | SReturn value -> value |> Option.exists expressionHasNestedAssignment
            | SIf(cond, yes, no) ->
                expressionHasNestedAssignment cond
                || containsAssignmentWhere targetMatches yes
                || containsAssignmentWhere targetMatches no
            | SForIn(_, source, body)
            | SWhile(source, body) ->
                expressionHasNestedAssignment source || containsAssignmentWhere targetMatches body
            | SSwitch(subject, arms) ->
                expressionHasNestedAssignment subject
                || (arms
                    |> List.exists (fun (case, body) ->
                        (case |> Option.exists expressionHasNestedAssignment)
                        || containsAssignmentWhere targetMatches body))
            | SThrow expr -> expressionHasNestedAssignment expr
            | STry(body, catch, finallyBlock) ->
                containsAssignmentWhere targetMatches body
                || (catch |> Option.exists (fun (_, _, handler) -> containsAssignmentWhere targetMatches handler))
                || containsAssignmentWhere targetMatches finallyBlock
            | SFunc(_, _, body) -> containsAssignmentWhere targetMatches body
            | SBreak
            | SContinue -> false)

    let containsSpreadAssignment stmts =
        containsAssignmentWhere assignmentTargetContainsSpreadProperty stmts

    let containsSpreadDerivedIndexAssignment stmts =
        containsAssignmentWhere assignmentTargetIsSpreadDerivedIndex stmts

    /// Names called as bare functions — the step vocabulary a script needs.
    let rec freeCalls (stmts: Stmt list) : Set<string> =
        let rec ofExpr e =
            match e with
            | ECall(FreeCall n, args, trailing) ->
                Set.singleton n
                |> Set.union (args |> List.map ofArg |> Set.unionMany)
                |> Set.union (
                    match trailing with
                    | Some c -> freeCalls c.Body
                    | None -> Set.empty)
            | ECall((MethodCall(t, _) | SafeMethodCall(t, _)), args, trailing) ->
                ofExpr t
                |> Set.union (args |> List.map ofArg |> Set.unionMany)
                |> Set.union (
                    match trailing with
                    | Some c -> freeCalls c.Body
                    | None -> Set.empty)
            | EGString parts ->
                parts
                |> List.map (function
                    | GLit _ -> Set.empty
                    | GExpr x -> ofExpr x)
                |> Set.unionMany
            | EList xs -> xs |> List.map ofExpr |> Set.unionMany
            | EMap kvs -> kvs |> List.map (snd >> ofExpr) |> Set.unionMany
            | EProp(t, _)
            | ESpreadProp(t, _)
            | ESafeProp(t, _) -> ofExpr t
            | EIndex(t, i) -> Set.union (ofExpr t) (ofExpr i)
            | EUnary(_, x) -> ofExpr x
            | EBinary(_, l, r) -> Set.union (ofExpr l) (ofExpr r)
            | ETernary(c, a, b) -> Set.unionMany [ ofExpr c; ofExpr a; ofExpr b ]
            | EElvis(a, b) -> Set.union (ofExpr a) (ofExpr b)
            | EClosure c -> freeCalls c.Body
            | ENull
            | EBool _
            | EInt _
            | EStr _
            | EVar _ -> Set.empty

        and ofArg =
            function
            | APos e -> ofExpr e
            | ANamed(_, e) -> ofExpr e

        stmts
        |> List.map (fun s ->
            match s with
            | SExpr e -> ofExpr e
            | SDef(_, Some e) -> ofExpr e
            | SDef(_, None) -> Set.empty
            | SAssign(t, v) -> Set.union (ofExpr t) (ofExpr v)
            | SIf(c, a, b) -> Set.unionMany [ ofExpr c; freeCalls a; freeCalls b ]
            | SForIn(_, src, b) -> Set.union (ofExpr src) (freeCalls b)
            | SWhile(c, b) -> Set.union (ofExpr c) (freeCalls b)
            | SSwitch(subject, arms) ->
                Set.unionMany (
                    ofExpr subject
                    :: (arms |> List.map (fun (k, b) -> Set.union (k |> Option.map ofExpr |> Option.defaultValue Set.empty) (freeCalls b)))
                )
            | SReturn(Some e) -> ofExpr e
            | SReturn None -> Set.empty
            | SBreak
            | SContinue -> Set.empty
            | SThrow e -> ofExpr e
            | STry(b, c, f) ->
                Set.unionMany
                    [ freeCalls b
                      (match c with
                       | Some(_, _, cb) -> freeCalls cb
                       | None -> Set.empty)
                      freeCalls f ]
            | SFunc(_, _, b) -> freeCalls b)
        |> Set.unionMany

    /// Functions the script defines itself — these are NOT missing steps.
    let rec definedFunctions (stmts: Stmt list) : Set<string> =
        stmts
        |> List.map (fun s ->
            match s with
            | SFunc(n, _, b) -> Set.add n (definedFunctions b)
            | SIf(_, a, b) -> Set.union (definedFunctions a) (definedFunctions b)
            | SForIn(_, _, b)
            | SWhile(_, b) -> definedFunctions b
            | SSwitch(_, arms) -> arms |> List.map (snd >> definedFunctions) |> Set.unionMany
            | STry(b, c, f) ->
                Set.unionMany
                    [ definedFunctions b
                      (match c with
                       | Some(_, _, cb) -> definedFunctions cb
                       | None -> Set.empty)
                      definedFunctions f ]
            | SExpr _
            | SDef _
            | SAssign _
            | SReturn _
            | SBreak
            | SContinue
            | SThrow _ -> Set.empty)
        |> Set.unionMany
