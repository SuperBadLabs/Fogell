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
    | SReturn of Expr option
    | SBreak
    | SContinue
    | SThrow of Expr
    | STry of body: Stmt list * catch: (string option * Stmt list) option * finallyBlock: Stmt list
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
              | STry(b, c, f) ->
                  countStmts b
                  + (match c with
                     | Some(_, cb) -> countStmts cb
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
            | ECall(MethodCall(t, _), args, trailing) ->
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
            | EProp(t, _) -> ofExpr t
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
            | SReturn(Some e) -> ofExpr e
            | SReturn None -> Set.empty
            | SBreak
            | SContinue -> Set.empty
            | SThrow e -> ofExpr e
            | STry(b, c, f) ->
                Set.unionMany
                    [ freeCalls b
                      (match c with
                       | Some(_, cb) -> freeCalls cb
                       | None -> Set.empty)
                      freeCalls f ]
            | SFunc(_, _, b) -> freeCalls b)
        |> Set.unionMany

    /// Variable names READ but bound nowhere in the script — the names Groovy's
    /// property lookup would fail on. `bound` seeds the environment (a GString
    /// evaluation passes its known variables plus `env`). Scoping is sequential:
    /// a `def` binds for the statements after it, loop variables and closure
    /// parameters bind their bodies, and a closure without parameters binds `it`.
    let freeVars (bound: Set<string>) (stmts: Stmt list) : Set<string> =
        let rec ofExpr (b: Set<string>) e =
            match e with
            | EVar n -> if Set.contains n b then Set.empty else Set.singleton n
            | EProp(t, _) -> ofExpr b t
            | EIndex(t, i) -> Set.union (ofExpr b t) (ofExpr b i)
            | EUnary(_, x) -> ofExpr b x
            | EBinary(_, l, r) -> Set.union (ofExpr b l) (ofExpr b r)
            | ETernary(c, x, y) -> Set.unionMany [ ofExpr b c; ofExpr b x; ofExpr b y ]
            | EElvis(x, y) -> Set.union (ofExpr b x) (ofExpr b y)
            | EGString parts ->
                parts
                |> List.map (function
                    | GLit _ -> Set.empty
                    | GExpr x -> ofExpr b x)
                |> Set.unionMany
            | EList xs -> xs |> List.map (ofExpr b) |> Set.unionMany
            | EMap kvs -> kvs |> List.map (snd >> ofExpr b) |> Set.unionMany
            | ECall(target, args, trailing) ->
                let t =
                    match target with
                    | FreeCall _ -> Set.empty
                    | MethodCall(x, _) -> ofExpr b x

                let a =
                    args
                    |> List.map (function
                        | APos x -> ofExpr b x
                        | ANamed(_, x) -> ofExpr b x)
                    |> Set.unionMany

                let c =
                    match trailing with
                    | Some cl -> ofClosure b cl
                    | None -> Set.empty

                Set.unionMany [ t; a; c ]
            | EClosure c -> ofClosure b c
            | ENull
            | EBool _
            | EInt _
            | EStr _ -> Set.empty

        and ofClosure b (c: Closure) =
            let ps = if List.isEmpty c.Params then [ "it" ] else c.Params
            ofStmts (Set.union b (Set.ofList ps)) c.Body

        and ofStmts (b: Set<string>) (ss: Stmt list) : Set<string> =
            let free, _ =
                ss
                |> List.fold
                    (fun (acc: Set<string>, b: Set<string>) s ->
                        match s with
                        | SExpr e -> Set.union acc (ofExpr b e), b
                        | SDef(n, v) ->
                            let f =
                                match v with
                                | Some e -> ofExpr b e
                                | None -> Set.empty

                            Set.union acc f, Set.add n b
                        // Assigning to a bare name CREATES it in Groovy's binding —
                        // `${x = 'ok'; x}` is valid, so `x` must bind for the statements
                        // after it (the right-hand side is still scanned first, with the
                        // pre-assignment scope: `x = x` reads an unbound x). A dotted or
                        // indexed target is a write into something that must itself
                        // already exist, so it stays a read.
                        | SAssign(EVar n, v) -> Set.union acc (ofExpr b v), Set.add n b
                        | SAssign(t, v) -> Set.unionMany [ acc; ofExpr b t; ofExpr b v ], b
                        | SIf(c, x, y) -> Set.unionMany [ acc; ofExpr b c; ofStmts b x; ofStmts b y ], b
                        | SForIn(var, src, body) ->
                            Set.unionMany [ acc; ofExpr b src; ofStmts (Set.add var b) body ], b
                        | SWhile(c, body) -> Set.unionMany [ acc; ofExpr b c; ofStmts b body ], b
                        | SReturn(Some e) -> Set.union acc (ofExpr b e), b
                        | SReturn None -> acc, b
                        | SBreak
                        | SContinue -> acc, b
                        | SThrow e -> Set.union acc (ofExpr b e), b
                        | STry(body, catch, fin) ->
                            let c =
                                match catch with
                                | Some(v, cb) ->
                                    let cb' =
                                        match v with
                                        | Some n -> ofStmts (Set.add n b) cb
                                        | None -> ofStmts b cb

                                    cb'
                                | None -> Set.empty

                            Set.unionMany [ acc; ofStmts b body; c; ofStmts b fin ], b
                        | SFunc(n, ps, body) ->
                            let b' = Set.add n b
                            Set.union acc (ofStmts (Set.union b' (Set.ofList ps)) body), b')
                    (Set.empty, bound)

            free

        ofStmts bound stmts

    /// Functions the script defines itself — these are NOT missing steps.
    let rec definedFunctions (stmts: Stmt list) : Set<string> =
        stmts
        |> List.map (fun s ->
            match s with
            | SFunc(n, _, b) -> Set.add n (definedFunctions b)
            | SIf(_, a, b) -> Set.union (definedFunctions a) (definedFunctions b)
            | SForIn(_, _, b)
            | SWhile(_, b) -> definedFunctions b
            | STry(b, c, f) ->
                Set.unionMany
                    [ definedFunctions b
                      (match c with
                       | Some(_, cb) -> definedFunctions cb
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
