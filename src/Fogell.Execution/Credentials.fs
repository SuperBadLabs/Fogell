namespace Fogell.Execution

open System
open System.Text.RegularExpressions

/// FG-044. What a credential id resolves to.
///
/// Only the forms the corpus actually uses are modelled — `string` (19 files),
/// `usernamePassword` (12) and `file` (3). A plugin-specific binding such as
/// `vaultString` (1 file) is NOT modelled and is rejected by name rather than
/// silently bound to nothing, because a step that appears to succeed with an empty
/// credential is the worst outcome available: the build goes green and the deploy
/// authenticates as nobody.
type Credential =
    | SecretText of string
    | UsernamePassword of user: string * password: string
    /// Bytes, not a string. REVIEW FIX (Codex, PR #15 round 4): decoding to UTF-8 text
    /// corrupts any real file credential — a keystore, a DER certificate, a gzip — and
    /// corrupting a credential silently is worse than refusing it.
    | SecretFile of PreparedFileCredential

/// A binding requested by `withCredentials`, before resolution.
type CredentialRequest =
    | BindText of id: string * variable: string
    | BindUserPass of id: string * userVariable: string * passVariable: string
    | BindFile of id: string * variable: string
    /// A binding kind this engine does not model, kept so it can be REFUSED by name.
    | BindUnmodelled of kind: string * source: string

module Credentials =

    /// Resolve one file credential's log-protection forms once. Every lexical
    /// binding can then share the immutable strings instead of retaining another
    /// full base64/hex set for the remainder of the run.
    let secretFile (fileName: string) (content: byte[]) =
        SecretFile(Secrets.prepareFileCredential fileName content)

    /// Parse the list literal `withCredentials` is given. The parser hands it over as
    /// raw source (ADR 0002), so the shape is decided here, where it is needed.
    let parseRequests (raw: string) : CredentialRequest list =
        let named (args: string) (key: string) =
            // A key is a Groovy/Java identifier token, not any matching suffix.
            // `\b` is insufficient: `$credentialsId` has a word boundary before
            // `credentialsId`, and Unicode identifier parts extend beyond `\w`.
            // Java admits spacing/nonspacing marks, decimal digits, letter numbers,
            // connector punctuation, currency, and identifier-ignorable format chars.
            // Other-number and enclosing-mark categories are deliberately excluded.
            let tokenStart = @"(?<![\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Nl}\p{Pc}\p{Sc}\p{Cf}])"

            let m =
                Regex.Match(
                    args,
                    tokenStart
                    + Regex.Escape key
                    + @"\s*:\s*'([^']*)'|"
                    + tokenStart
                    + Regex.Escape key
                    + @"\s*:\s*""([^""]*)"""
                )

            if not m.Success then None
            elif m.Groups[1].Success then Some m.Groups[1].Value
            else Some m.Groups[2].Value

        [ for m in Regex.Matches(raw, @"([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)") do
              let kind = m.Groups[1].Value
              let args = m.Groups[2].Value
              match kind with
              | "string" ->
                  match named args "credentialsId", named args "variable" with
                  | Some id, Some v -> BindText(id, v)
                  | _ -> BindUnmodelled(kind, args)
              | "usernamePassword" ->
                  match
                      named args "credentialsId",
                      named args "usernameVariable",
                      named args "passwordVariable"
                  with
                  | Some id, Some u, Some p -> BindUserPass(id, u, p)
                  | _ -> BindUnmodelled(kind, args)
              | "file" ->
                  match named args "credentialsId", named args "variable" with
                  | Some id, Some v -> BindFile(id, v)
                  | _ -> BindUnmodelled(kind, args)
              | other -> BindUnmodelled(other, args) ]

    /// The credential ids a request set needs, for a fail-closed check before running.
    let idsOf (requests: CredentialRequest list) =
        requests
        |> List.choose (function
            | BindText(id, _) -> Some id
            | BindUserPass(id, _, _) -> Some id
            | BindFile(id, _) -> Some id
            | BindUnmodelled _ -> None)
