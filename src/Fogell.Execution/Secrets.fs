namespace Fogell.Execution

open System
open System.IO

type SecretForms =
    private
        { TextValue: string
          MaskForms: string list
          LeakForms: (string * string) list }

/// One file credential's byte snapshot and the log-protection forms derived from
/// that exact snapshot. The representation is opaque outside this assembly so a
/// caller cannot pair forms for one value with bytes from another.
[<Sealed>]
type PreparedFileCredential internal (
    fileName: string,
    content: byte[],
    containsTextLineBreak: Lazy<bool>,
    forms: Lazy<SecretForms>
) =
    member internal _.FileName = fileName
    member internal _.Content = content
    member internal _.ContainsTextLineBreak = containsTextLineBreak.Value
    member internal _.Forms = forms.Value
    member internal _.FormsCreated = forms.IsValueCreated

type SecretBinding =
    { /// The variable carrying the secret value.
      ValueVariable: string
      /// Companion variable carrying a path to a 0600 file with the same value. Kept
      /// as an ADDITION, not a replacement: scripts that prefer a file can use it.
      PathVariable: string
      /// Absolute path of the 0600 file holding the value.
      FilePath: string
      Value: string
      /// Immutable text forms shared by every lexical binding of one resolved
      /// credential. They are run-scoped metadata, not zeroized memory.
      Forms: SecretForms
      ValueVariableCarriesPath: bool }

type Leak =
    { Variable: string
      /// How the value appeared: which transformation defeated the mask.
      Encoding: string }

module Secrets =

    [<Literal>]
    let UnsupportedMultilineCredentialCode = "unsupported_multiline_credential"

    [<Literal>]
    let private MinimumBinaryEncodingBytes = 8

    [<Literal>]
    let private MinimumBinaryDistinctBytes = 4

    /// FG-235. FG-236's raw matcher protects single-line registered forms when
    /// output inserts one CR/LF/CRLF separator between their characters. A
    /// credential which owns a line ending is not such a form, so keep refusing
    /// it before binding rather than silently widening that grammar.
    let containsPhysicalLineBreak (value: string) =
        not (isNull value)
        && value.IndexOfAny([| '\r'; '\n' |]) >= 0

    let private validateProgressiveText parameterName value =
        if containsPhysicalLineBreak value then
            invalidArg
                parameterName
                $"{UnsupportedMultilineCredentialCode}: raw-output redaction accepts only single-line credential text"

    type internal SecretFilePhase =
        | Opened
        | ReadyToWrite

    let internal writeSecretFileAtPathWithObserver
        (path: string)
        (bytes: byte[])
        (observe: SecretFilePhase -> string -> unit)
        =
        let ownerOnly = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        let options =
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = ownerOnly)

        // FG-073 review: WriteAllText/WriteAllBytes created under the process
        // umask and chmodded afterwards. A traversable parent plus a permissive
        // umask therefore exposed a different-UID read window. CreateNew with
        // the final mode makes both non-overwrite and confidentiality properties
        // true at the opening syscall, before any secret byte is written.
        let secret = File.Open(path, options)

        try
            use stream = secret
            observe Opened path

            // open(2) applies the process umask even to an explicit create mode.
            // Tighten through the already-open descriptor: this cannot redirect
            // through a path race, it restores owner readability under a hardened
            // umask, and no secret byte exists yet.
            File.SetUnixFileMode(stream.SafeFileHandle, ownerOnly)
            observe ReadyToWrite path
            stream.Write bytes
            stream.Flush()
            path
        with _ ->
            try File.Delete path with _ -> ()
            reraise()

    let internal createSecretFileWithObserver
        (directory: string)
        (bytes: byte[])
        (observe: SecretFilePhase -> string -> unit)
        =
        Directory.CreateDirectory directory |> ignore
        // The full 128-bit identifier keeps stale files and high-concurrency
        // bindings from turning CreateNew's fail-closed collision into an
        // avoidable build failure.
        let unique = Guid.NewGuid().ToString("N")
        let path = Path.Combine(directory, $".secret-{unique}")
        writeSecretFileAtPathWithObserver path bytes observe

    let private createSecretFile (directory: string) (bytes: byte[]) =
        createSecretFileWithObserver directory bytes (fun _ _ -> ())

    let private registeredForms (includeByteEncoding: bool) (value: string) (bytes: byte[]) =
        [ "literal", value
          "upper", value.ToUpperInvariant()
          "lower", value.ToLowerInvariant()
          if includeByteEncoding then
              "base64", Convert.ToBase64String bytes ]
        |> List.filter (fun (_, v) -> v <> "")
        |> List.distinctBy snd

    let private detectableForms (includeByteEncoding: bool) (value: string) (bytes: byte[]) =
        // REVIEW FIX (Copilot, PR #11): only the lowercase hex form was generated,
        // so a secret hex-encoded by anything using .NET's default casing — which
        // is UPPERCASE — went undetected while the report claimed hex was covered.
        // A detector with a hole it does not admit to is worse than no detector.
        [ "reversed", String(value.ToCharArray() |> Array.rev)
          "char-split", String.Join("_", value.ToCharArray())
          if includeByteEncoding then
              "hex", Convert.ToHexString(bytes).ToLowerInvariant()
              "hex-upper", Convert.ToHexString bytes ]
        |> List.filter (fun (_, v) -> v.Length > 3)
        |> List.distinctBy snd

    let private textValueOfBytes (bytes: byte[]) =
        try
            let text = Text.Encoding.UTF8.GetString bytes
            if Text.Encoding.UTF8.GetBytes text = bytes then text else ""
        with _ ->
            ""

    let private hasMinimumBinaryDiversity (bytes: byte[]) =
        let distinct = Collections.Generic.HashSet<byte>()
        let mutable index = 0

        while distinct.Count < MinimumBinaryDistinctBytes && index < bytes.Length do
            distinct.Add bytes.[index] |> ignore
            index <- index + 1

        distinct.Count >= MinimumBinaryDistinctBytes

    let private prepareForms (isFileCredential: bool) (value: string) (bytes: byte[]) =
        let includeBase64 =
            not isFileCredential || bytes.Length >= MinimumBinaryEncodingBytes

        let includeHexDetection =
            not isFileCredential
            || (bytes.Length >= MinimumBinaryEncodingBytes
                && hasMinimumBinaryDiversity bytes)

        { TextValue = value
          MaskForms = registeredForms includeBase64 value bytes |> List.map snd
          LeakForms = detectableForms includeHexDetection value bytes }

    let internal prepareBinaryForms (bytes: byte[]) =
        prepareForms true (textValueOfBytes bytes) bytes

    let internal prepareFileCredential (fileName: string) (content: byte[]) =
        // The credential store owns one defensive snapshot. Forms and every
        // materialized file are derived from this same otherwise-inaccessible array.
        // Derivation is lazy: resolving one store entry must not retain encodings for
        // every other file credential in the store.
        let snapshot = Array.copy content
        let textValue = lazy (textValueOfBytes snapshot)

        PreparedFileCredential(
            fileName,
            snapshot,
            lazy (containsPhysicalLineBreak textValue.Value),
            lazy (prepareForms true textValue.Value snapshot))

    let preparedFileContainsPhysicalLineBreak (credential: PreparedFileCredential) =
        credential.ContainsTextLineBreak

    let private validateVariableName (variableName: string) =
        // System.Diagnostics.Process environment keys cannot be empty or contain
        // NUL/'='. Reject them before materializing a file so a partial-construction
        // failure has deterministic cleanup semantics rather than path side effects.
        if String.IsNullOrEmpty variableName
           || variableName.IndexOf('\000') >= 0
           || variableName.Contains '=' then
            invalidArg
                (nameof variableName)
                "credential variable name must be nonempty and contain neither NUL nor '='"

    let internal inMemoryTextBinding (variableName: string) (value: string) =
        validateVariableName variableName
        validateProgressiveText (nameof value) value
        let bytes = Text.Encoding.UTF8.GetBytes value

        { ValueVariable = variableName
          PathVariable = variableName + "_FILE"
          FilePath = ""
          Value = value
          Forms = prepareForms false value bytes
          ValueVariableCarriesPath = false }

    /// Write the secret to a file only the running user can read, and return the
    /// binding. The caller owns lexical revocation and recovery cleanup for its
    /// controller-side secret directory; abrupt process death can bypass both the
    /// lexical scope and this module's best-effort deletion.
    /// Bind raw BYTES, for a file credential whose content is not text.
    let internal bindBytesPrepared
        (directory: string)
        (variableName: string)
        (bytes: byte[])
        (forms: SecretForms)
        : SecretBinding =
        validateVariableName variableName
        validateProgressiveText "bytes" forms.TextValue
        let path = createSecretFile directory bytes

        { ValueVariable = variableName
          PathVariable = variableName + "_FILE"
          FilePath = path
          Value = forms.TextValue
          Forms = forms
          ValueVariableCarriesPath = true }

    let bindBytes (directory: string) (variableName: string) (bytes: byte[]) : SecretBinding =
        bindBytesPrepared directory variableName bytes (prepareBinaryForms bytes)

    /// Materialize an opaque prepared file credential. Consumers can carry this
    /// value and bind it, but cannot separate or mutate its bytes and forms.
    let bindPreparedFile
        (directory: string)
        (variableName: string)
        (credential: PreparedFileCredential)
        : SecretBinding =
        // Validate before forcing the lazy forms: a refused environment key must
        // neither touch disk nor retain derived strings for an otherwise-unused ID.
        validateVariableName variableName
        if credential.ContainsTextLineBreak then
            invalidArg
                (nameof credential)
                $"{UnsupportedMultilineCredentialCode}: raw-output redaction accepts only single-line credential text"
        bindBytesPrepared directory variableName credential.Content credential.Forms

    let bind (directory: string) (variableName: string) (value: string) : SecretBinding =
        validateVariableName variableName
        validateProgressiveText (nameof value) value
        let bytes = Text.Encoding.UTF8.GetBytes value
        let forms = prepareForms false value bytes
        let path = createSecretFile directory bytes

        { ValueVariable = variableName
          PathVariable = variableName + "_FILE"
          FilePath = path
          Value = value
          Forms = forms
          ValueVariableCarriesPath = false }

    let environmentForPreserving (preserved: Set<string>) (bindings: SecretBinding list) =
        let requested = bindings |> List.map (fun b -> b.ValueVariable) |> Set.ofList

        let values =
            bindings
            |> List.map (fun b ->
                b.ValueVariable, (if b.ValueVariableCarriesPath then b.FilePath else b.Value))

        let companions =
            bindings
            |> List.filter (fun b ->
                not (requested.Contains b.PathVariable)
                && not (preserved.Contains b.PathVariable))
            |> List.map (fun b -> b.PathVariable, b.FilePath)

        values @ companions

    let environmentFor (bindings: SecretBinding list) =
        environmentForPreserving Set.empty bindings

    let environmentForPathOnly (bindings: SecretBinding list) =
        bindings |> List.map (fun b -> b.PathVariable, b.FilePath)

    /// Every form which is safe to redact as one complete match. Kept in one
    /// definition so line-oriented emitters and FG-236's raw-stream matcher do
    /// not silently disagree about file paths or derived encodings.
    let maskingForms (bindings: SecretBinding list) =
        bindings
        |> List.collect (fun b ->
            let pathForms =
                if b.ValueVariableCarriesPath && b.FilePath <> "" then [ b.FilePath ] else []
            b.Forms.MaskForms @ pathForms)
        |> List.distinct
        |> List.sortByDescending String.length

    /// Build one immutable policy; ProcessGroup derives independent mutable
    /// matchers from it for stdout and stderr.
    let outputRedaction (bindings: SecretBinding list) =
        let policy = OutputRedactionPolicy(maskingForms bindings)
        if policy.IsEmpty then None else Some policy

    /// A monotonic run-scoped inventory. Each stream matcher enrolls newly
    /// registered forms before processing its next decoded chunk.
    let outputRedactionLive (bindings: unit -> SecretBinding list) synchronizationRoot =
        OutputRedactionPolicy((fun () -> bindings () |> maskingForms), synchronizationRoot)

    /// Replace every registered form with `****`, retaining exact provenance
    /// so a not-yet-published line can be rechecked after later registration.
    let maskRedacted (bindings: SecretBinding list) (text: string) =
        let policy = OutputRedactionPolicy(maskingForms bindings)
        policy.MaskRedacted text

    let mask (bindings: SecretBinding list) (text: string) =
        (maskRedacted bindings text).Text

    /// Recheck output which already crossed the raw matcher against the latest
    /// run-wide inventory. A binding may race the matcher's earlier snapshot but
    /// cannot race WalkerCtx's locked publication boundary. Short all-star
    /// credentials would otherwise expand an existing canonical `****` token.
    /// Only spans carrying exact raw-matcher provenance are opaque. Literal
    /// four-star runs remain raw and can match a credential learned before the
    /// locked publication boundary.
    let maskAlreadyRedacted (bindings: SecretBinding list) (value: RedactedText) =
        let forms = maskingForms bindings |> List.toArray

        let apply (raw: RedactedText) =
            if Array.isEmpty forms then
                raw
            else
                let matcher = SeparatorTolerantMasker(fun () -> forms)
                RedactedTextOps.append (matcher.PushValue raw) (matcher.CompleteRedacted())

        RedactedTextOps.mapRawFragments apply value

    /// Recheck a sequence of framed lines as one stream and return each output
    /// line with the index of the last input line whose bytes contributed to it.
    /// A cross-line token therefore inherits the timestamp/order of its final
    /// credential fragment, even when several matches collapse independently.
    let maskAlreadyRedactedLines (bindings: SecretBinding list) (values: RedactedText array) =
        values
        |> Array.mapi (fun source value -> source, value)
        |> RedactedText.JoinSourcedLines
        |> maskAlreadyRedacted bindings
        |> _.SplitLinesWithSources()

    /// Reframe a retained stream history for late bindings while projecting
    /// the result onto sources which are still mutable. Committed characters
    /// participate as matcher left-context, but cannot be replayed as part of a
    /// later pending line. A token spanning committed and pending sources is
    /// attributed to its final (pending) source and is therefore retained.
    let maskAlreadyRedactedPendingLines
        (bindings: SecretBinding list)
        (pendingSources: Set<int>)
        (values: RedactedText array)
        =
        maskAlreadyRedactedLines bindings values
        |> Array.map (fun (source, value) -> source, RedactedTextOps.retainSources pendingSources value)

    /// FG-071. After masking, look for forms the mask does not cover. A hit means
    /// a secret reached the log in a shape masking cannot catch — reported, never
    /// swallowed.
    let detectLeaks (bindings: SecretBinding list) (maskedText: string) : Leak list =
        [ for b in bindings do
              // REVIEW FIX (Codex, PR #15 round 5): `bindBytes` deliberately stores an
              // empty Value for a binary credential, and EVERY string contains the empty
              // string — so this reported a literal credential leak on every line of
              // output inside the block. A security warning that fires always is worse
              // than none: it trains the reader to ignore the channel.
              if b.Value <> "" && maskedText.Contains b.Value then
                  { Variable = b.ValueVariable; Encoding = "literal" }

              for name, form in b.Forms.LeakForms do
                  if maskedText.Contains form then
                      { Variable = b.ValueVariable; Encoding = name } ]

    /// Screen engine-authored text which did not pass through the masker. This
    /// includes every form the matcher would redact, not only the literal and
    /// transformed forms retained by the post-mask leak detector.
    let detectRegisteredLeaks (bindings: SecretBinding list) (text: string) : Leak list =
        [ for b in bindings do
              let pathForms =
                  if b.ValueVariableCarriesPath && b.FilePath <> "" then [ b.FilePath ] else []

              for form in b.Forms.MaskForms @ pathForms do
                  if form <> "" && text.Contains form then
                      { Variable = b.ValueVariable; Encoding = "registered" } ]
        |> List.distinct

    /// Scan output that already crossed the registered-form matcher. Literal
    /// detection here would mistake the canonical `****` replacement for a
    /// one-character `*` credential; only transformations outside the masking
    /// inventory remain meaningful at this boundary.
    let detectUnregisteredLeaks (bindings: SecretBinding list) (maskedText: string) : Leak list =
        [ for b in bindings do
              for name, form in b.Forms.LeakForms do
                  if maskedText.Contains form then
                      { Variable = b.ValueVariable; Encoding = name } ]

    /// The provenance-aware form of unregistered leak detection. A transformed
    /// form may use only raw characters; canonical masker-token characters are
    /// opaque even when their rendered `****` bytes happen to equal the form.
    let detectUnregisteredLeaksRedacted (bindings: SecretBinding list) (value: RedactedText) : Leak list =
        let containsRaw (form: string) =
            let mutable at = value.Text.IndexOf(form, StringComparison.Ordinal)
            let mutable found = false

            while not found && at >= 0 do
                let mutable raw = true
                let mutable index = at

                while raw && index < at + form.Length do
                    raw <- not value.TokenCharacters[index]
                    index <- index + 1

                found <- raw
                at <- value.Text.IndexOf(form, at + 1, StringComparison.Ordinal)

            found

        [ for b in bindings do
              for name, form in b.Forms.LeakForms do
                  if containsRaw form then
                      { Variable = b.ValueVariable; Encoding = name } ]

    /// Detect a registered or transformed form which exists only after an
    /// engine-authored prefix and a provenance-bearing value are composed.
    /// Scanning the two halves independently misses this case, while scanning
    /// the whole value as ordinary text would mistake canonical `****` masker
    /// tokens for a literal `*` credential.
    let detectBoundaryLeaks (bindings: SecretBinding list) (prefix: string) (value: RedactedText) : Leak list =
        let crossesBoundary (form: string) =
            if prefix = "" || value.Text = "" || form = "" then
                false
            else
                let composed = prefix + value.Text
                let boundary = prefix.Length
                let mutable at = composed.IndexOf(form, StringComparison.Ordinal)
                let mutable found = false

                while not found && at >= 0 do
                    if at < boundary && at + form.Length > boundary then
                        let mutable raw = true
                        let mutable index = boundary
                        let finish = at + form.Length

                        while raw && index < finish do
                            raw <- not value.TokenCharacters[index - boundary]
                            index <- index + 1

                        found <- raw

                    at <- composed.IndexOf(form, at + 1, StringComparison.Ordinal)

                found

        [ for b in bindings do
              let pathForms =
                  if b.ValueVariableCarriesPath && b.FilePath <> "" then [ b.FilePath ] else []

              for form in b.Forms.MaskForms @ pathForms do
                  if crossesBoundary form then
                      { Variable = b.ValueVariable; Encoding = "registered-boundary" }

              for name, form in b.Forms.LeakForms do
                  if crossesBoundary form then
                      { Variable = b.ValueVariable; Encoding = name + "-boundary" } ]
        |> List.distinct

    /// Remove secret files. Called even on failure, because a leftover secret
    /// file outlives the reason it existed.
    let revoke (bindings: SecretBinding list) =
        for b in bindings do
            try
                if File.Exists b.FilePath then File.Delete b.FilePath
            with _ ->
                ()
