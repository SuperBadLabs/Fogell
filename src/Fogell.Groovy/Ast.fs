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
            | EProp(t, _)
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
