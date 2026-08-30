namespace Fogell.Execution

open System

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

    // GroovyLexer WS is deliberately narrower than Char.IsWhiteSpace: only
    // space, tab, CR, LF, and form feed are token trivia. In particular NBSP
    // and the other Unicode separator characters are compile errors, not gaps
    // that may be erased before credential authority is consulted.
    let private isGroovyWhitespace c =
        c = ' ' || c = '\t' || c = '\r' || c = '\n' || c = '\u000C'

    let private trimGroovyWhitespace (text: string) =
        let mutable first = 0

        while first < text.Length && isGroovyWhitespace text.[first] do
            first <- first + 1

        let mutable last = text.Length - 1

        while last >= first && isGroovyWhitespace text.[last] do
            last <- last - 1

        text.Substring(first, last - first + 1)

    let private skipTrivia (text: string) start =
        let mutable i = start
        let mutable scanning = true
        let mutable valid = true

        while scanning && i < text.Length do
            if isGroovyWhitespace text.[i] then
                i <- i + 1
            elif i + 1 < text.Length && text.[i] = '/' && text.[i + 1] = '/' then
                i <- i + 2

                while i < text.Length && text.[i] <> '\r' && text.[i] <> '\n' do
                    i <- i + 1
            elif i + 1 < text.Length && text.[i] = '/' && text.[i + 1] = '*' then
                let close = text.IndexOf("*/", i + 2, StringComparison.Ordinal)

                if close < 0 then
                    valid <- false
                    scanning <- false
                else
                    i <- close + 2
            else
                scanning <- false

        if valid then Some i else None

    let private isTripleQuoteAt (text: string) i =
        i + 2 < text.Length
        && (text.[i] = '\'' || text.[i] = '"')
        && text.[i + 1] = text.[i]
        && text.[i + 2] = text.[i]

    let private isLineBreak c = c = '\r' || c = '\n'

    /// Split a credential-DSL list/call body only at top-level commas. Quotes,
    /// escapes, comments, and nested delimiters are structural: text that merely
    /// LOOKS like a binding or named key inside them is never re-scanned as code.
    let private splitTopLevel (text: string) =
        let parts = Collections.Generic.List<string>()
        let mutable start = 0
        let mutable i = 0
        let delimiters = Collections.Generic.List<char>()
        let mutable quote = ValueNone
        let mutable escaped = false
        let mutable lineComment = false
        let mutable blockComment = false
        let mutable valid = true

        while valid && i < text.Length do
            let c = text.[i]

            if lineComment then
                if c = '\r' || c = '\n' then lineComment <- false
                i <- i + 1
            elif blockComment then
                if c = '*' && i + 1 < text.Length && text.[i + 1] = '/' then
                    blockComment <- false
                    i <- i + 2
                else
                    i <- i + 1
            else
                match quote with
                | ValueSome q ->
                    if isLineBreak c then
                        // Groovy ordinary single/double literals cannot span a
                        // physical line. Continuing would accept source Jenkins
                        // rejects during compilation and could expose controller
                        // credential authority to an impossible request.
                        valid <- false
                    elif escaped then
                        escaped <- false
                    elif c = '\\' then
                        escaped <- true
                    elif c = q then
                        quote <- ValueNone

                    i <- i + 1
                | ValueNone ->
                    if isTripleQuoteAt text i then
                        // This boundary models only escape-free single/double quoted
                        // literals. Treating the first quote of a Groovy triple string
                        // as an ordinary opener would expose its commas/delimiters as
                        // structure, so reject the complete request instead.
                        valid <- false
                    elif c = '\'' || c = '"' then
                        quote <- ValueSome c
                        i <- i + 1
                    elif c = '/' && i + 1 < text.Length && text.[i + 1] = '/' then
                        lineComment <- true
                        i <- i + 2
                    elif c = '/' && i + 1 < text.Length && text.[i + 1] = '*' then
                        blockComment <- true
                        i <- i + 2
                    else
                        match c with
                        | '(' | '[' | '{' -> delimiters.Add c
                        | ')' | ']' | '}' ->
                            let expected =
                                match c with
                                | ')' -> '('
                                | ']' -> '['
                                | _ -> '{'

                            if delimiters.Count = 0 || delimiters.[delimiters.Count - 1] <> expected then
                                valid <- false
                            else
                                delimiters.RemoveAt(delimiters.Count - 1)
                        | ',' when delimiters.Count = 0 ->
                            parts.Add(text.Substring(start, i - start))
                            start <- i + 1
                        | _ -> ()

                        i <- i + 1

        if
            valid
            && quote = ValueNone
            && not escaped
            && not blockComment
            && delimiters.Count = 0
        then
            parts.Add(text.Substring start)
            Some(List.ofSeq parts)
        else
            None

    let private isKeyStart c = Char.IsAsciiLetter c || c = '_'
    let private isKeyContinue c = Char.IsAsciiLetterOrDigit c || c = '_'
    let private isNamedStart c = Char.IsLetter c || c = '_' || c = '$'
    let private isNamedContinue c = Char.IsLetterOrDigit c || c = '_' || c = '$'

    let private matchingDelimiter (text: string) opening =
        let mutable i = opening + 1
        let delimiters = Collections.Generic.List<char>()
        delimiters.Add text.[opening]
        let mutable quote = ValueNone
        let mutable escaped = false
        let mutable lineComment = false
        let mutable blockComment = false
        let mutable close = -1
        let mutable valid = true

        while valid && close < 0 && i < text.Length do
            let c = text.[i]

            if lineComment then
                if c = '\r' || c = '\n' then lineComment <- false
                i <- i + 1
            elif blockComment then
                if c = '*' && i + 1 < text.Length && text.[i + 1] = '/' then
                    blockComment <- false
                    i <- i + 2
                else
                    i <- i + 1
            else
                match quote with
                | ValueSome q ->
                    if isLineBreak c then
                        valid <- false
                    elif escaped then
                        escaped <- false
                    elif c = '\\' then
                        escaped <- true
                    elif c = q then
                        quote <- ValueNone

                    i <- i + 1
                | ValueNone ->
                    if isTripleQuoteAt text i then
                        valid <- false
                    elif c = '\'' || c = '"' then
                        quote <- ValueSome c
                        i <- i + 1
                    elif c = '/' && i + 1 < text.Length && text.[i + 1] = '/' then
                        lineComment <- true
                        i <- i + 2
                    elif c = '/' && i + 1 < text.Length && text.[i + 1] = '*' then
                        blockComment <- true
                        i <- i + 2
                    elif c = '(' || c = '[' || c = '{' then
                        delimiters.Add c
                        i <- i + 1
                    elif c = ')' || c = ']' || c = '}' then
                        let expected =
                            match c with
                            | ')' -> '('
                            | ']' -> '['
                            | _ -> '{'

                        if delimiters.Count = 0 || delimiters.[delimiters.Count - 1] <> expected then
                            valid <- false
                        else
                            delimiters.RemoveAt(delimiters.Count - 1)

                            if delimiters.Count = 0 then close <- i

                            i <- i + 1
                    else
                        i <- i + 1

        if valid && close >= 0 && quote = ValueNone && not blockComment then Some close else None

    /// Parse one actual top-level named argument. Every supported credential key
    /// requires one quoted literal. Unknown keys are rejected later by binding kind:
    /// Jenkins warns for inert unknown literals and evaluates non-literal values, but
    /// Fogell has neither behavior at this boundary, so silently ignoring either would
    /// be a false success. A malformed segment is an Error for the whole binding call.
    let private argumentSegment (segment: string) =
        match skipTrivia segment 0 with
        | None -> Error()
        | Some start when start >= segment.Length || not (isNamedStart segment.[start]) -> Error()
        | Some start ->
            let mutable i = start + 1

            while i < segment.Length && isNamedContinue segment.[i] do
                i <- i + 1

            let key = segment.Substring(start, i - start)

            match skipTrivia segment i with
            | Some colon when colon < segment.Length && segment.[colon] = ':' ->
                match skipTrivia segment (colon + 1) with
                | Some valueStart
                    when valueStart < segment.Length
                         && (segment.[valueStart] = '\'' || segment.[valueStart] = '"')
                         && not (isTripleQuoteAt segment valueStart) ->
                    let q = segment.[valueStart]
                    let mutable j = valueStart + 1
                    let mutable escaped = false
                    let mutable close = -1
                    let mutable valid = true

                    while valid && close < 0 && j < segment.Length do
                        let c = segment.[j]

                        if isLineBreak c then
                            valid <- false
                        elif escaped then
                            escaped <- false
                        elif c = '\\' then
                            escaped <- true
                        elif c = q then
                            close <- j

                        j <- j + 1

                    if not valid || close < 0 then
                        Error()
                    else
                        let value = segment.Substring(valueStart + 1, close - valueStart - 1)

                        match skipTrivia segment (close + 1) with
                        | Some finish
                            when finish = segment.Length
                                 && not (value.Contains '\\')
                                 && not (value.Contains '\r')
                                 && not (value.Contains '\n')
                                 && (q <> '"' || not (value.Contains '$')) ->
                            // Preserve the previous credential contract: quoted text is
                            // returned byte-for-byte between delimiters. Escapes are used
                            // only to locate the real close, not silently reinterpreted.
                            // A dollar in a double-quoted value is a possible Groovy
                            // interpolation, and a backslash in either quote form can
                            // decode to bytes other than this retained source. This
                            // boundary cannot evaluate either safely, so it refuses
                            // rather than binding an expression's raw spelling.
                            Ok(key, Some value)
                        | _ -> Error()
                | _ -> Error()
            | _ -> Error()

    let private parseCall (item: string) =
        match skipTrivia item 0 with
        | None -> None
        | Some start when start >= item.Length || not (isKeyStart item.[start]) -> None
        | Some start ->
            let mutable i = start + 1

            while i < item.Length && isKeyContinue item.[i] do
                i <- i + 1

            let kind = item.Substring(start, i - start)

            match skipTrivia item i with
            | Some opening when opening < item.Length && item.[opening] = '(' ->
                match matchingDelimiter item opening with
                | Some close ->
                    match skipTrivia item (close + 1) with
                    | Some finish when finish = item.Length ->
                        let args = item.Substring(opening + 1, close - opening - 1)
                        Some(kind, args)
                    | _ -> None
                | None -> None
            | _ -> None

    /// Parse the credential call sequence `withCredentials` is given. Depending on
    /// whether it came through declarative or scripted syntax, the retained source
    /// may include its outer brackets. In either form the complete source is consumed:
    /// quoted/comment decoys, trailing junk, malformed segments, and unknown keys
    /// cannot disappear beside an otherwise valid request.
    let parseRequests (raw: string) : CredentialRequest list =
        let malformed source = [ BindUnmodelled("malformed", source) ]
        let trimmed = if isNull raw then "" else trimGroovyWhitespace raw

        let body =
            if trimmed.StartsWith("[", StringComparison.Ordinal) then
                if trimmed.EndsWith("]", StringComparison.Ordinal) && trimmed.Length >= 2 then
                    Some(trimmed.Substring(1, trimmed.Length - 2))
                else
                    None
            elif trimmed.EndsWith("]", StringComparison.Ordinal) then
                None
            else
                Some trimmed

        match body with
        | None -> malformed raw
        | Some body ->
            match splitTopLevel body with
            | None -> malformed raw
            | Some items ->
                let isBlank item =
                    match skipTrivia item 0 with
                    | Some finish -> finish = item.Length
                    | None -> false

                if List.length items = 1 && isBlank items.Head then
                    []
                elif items |> List.exists isBlank then
                    // Leading, trailing, or doubled commas are items with no call.
                    // They cannot silently disappear beside otherwise valid requests.
                    malformed raw
                else
                    items
                    |> List.collect (fun item ->
                        match parseCall item with
                        | None -> malformed item
                        | Some(kind, args) ->
                            let parsedArgs =
                                match splitTopLevel args with
                                | None -> Error()
                                | Some segments ->
                                    let argumentIsBlank segment =
                                        match skipTrivia segment 0 with
                                        | Some finish -> finish = segment.Length
                                        | None -> false

                                    if List.length segments = 1 && argumentIsBlank segments.Head then
                                        Ok []
                                    elif segments |> List.exists argumentIsBlank then
                                        Error()
                                    else
                                        segments
                                        |> List.fold
                                            (fun state segment ->
                                                match state, argumentSegment segment with
                                                | Ok parsed, Ok arg -> Ok(arg :: parsed)
                                                | _ -> Error())
                                            (Ok [])

                            match parsedArgs with
                            | Error _ -> [ BindUnmodelled(kind, args) ]
                            | Ok reverseNamed ->
                                let named = List.rev reverseNamed

                                // Preserve the established first-key behavior, including
                                // a first exact key with an
                                // unsupported value refusing rather than falling through
                                // to a later quoted duplicate.
                                let get key =
                                    named
                                    |> List.tryFind (fun (candidate, _) -> candidate = key)
                                    |> Option.bind snd

                                let onlyKeys allowed =
                                    let keys = named |> List.map fst
                                    List.length keys = (keys |> List.distinct |> List.length)
                                    && (keys |> List.forall (fun candidate -> Set.contains candidate allowed))

                                match kind with
                                | "string" when onlyKeys (set [ "credentialsId"; "variable" ]) ->
                                    match get "credentialsId", get "variable" with
                                    | Some id, Some v -> [ BindText(id, v) ]
                                    | _ -> [ BindUnmodelled(kind, args) ]
                                | "usernamePassword"
                                    when onlyKeys (set [ "credentialsId"; "usernameVariable"; "passwordVariable" ]) ->
                                    match get "credentialsId", get "usernameVariable", get "passwordVariable" with
                                    | Some id, Some u, Some p -> [ BindUserPass(id, u, p) ]
                                    | _ -> [ BindUnmodelled(kind, args) ]
                                | "file" when onlyKeys (set [ "credentialsId"; "variable" ]) ->
                                    match get "credentialsId", get "variable" with
                                    | Some id, Some v -> [ BindFile(id, v) ]
                                    | _ -> [ BindUnmodelled(kind, args) ]
                                | other -> [ BindUnmodelled(other, args) ])

    /// Jenkins emits this warning before rejecting a known binding kind whose
    /// constructor map contains unknown literal keys. Keep warning derivation on
    /// the same structural parse as admission so key-shaped text in a quote or
    /// comment can never manufacture narration.
    let unknownParameterWarning request =
        let descriptor =
            match request with
            | BindUnmodelled("string", args) ->
                Some(
                    "org.jenkinsci.plugins.credentialsbinding.impl.StringBinding",
                    set [ "credentialsId"; "variable" ],
                    args)
            | BindUnmodelled("file", args) ->
                Some(
                    "org.jenkinsci.plugins.credentialsbinding.impl.FileBinding",
                    set [ "credentialsId"; "variable" ],
                    args)
            | BindUnmodelled("usernamePassword", args) ->
                Some(
                    "org.jenkinsci.plugins.credentialsbinding.impl.UsernamePasswordMultiBinding",
                    set [ "credentialsId"; "usernameVariable"; "passwordVariable" ],
                    args)
            | _ -> None

        match descriptor with
        | None -> None
        | Some(bindingClass, allowed, args) ->
            match splitTopLevel args with
            | None -> None
            | Some segments ->
                let parsed = segments |> List.map argumentSegment

                if parsed |> List.exists Result.isError then
                    None
                else
                    let unknown =
                        parsed
                        |> List.choose (function Ok(key, _) when not (Set.contains key allowed) -> Some key | _ -> None)
                        |> List.distinct

                    if List.isEmpty unknown then None else Some(bindingClass, unknown)

    /// The credential ids a request set needs, for a fail-closed check before running.
    let idsOf (requests: CredentialRequest list) =
        requests
        |> List.choose (function
            | BindText(id, _) -> Some id
            | BindUserPass(id, _, _) -> Some id
            | BindFile(id, _) -> Some id
            | BindUnmodelled _ -> None)
