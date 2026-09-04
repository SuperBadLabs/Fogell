namespace Fogell.Execution

open System
open System.IO

/// FG-070/071. Secret delivery and masking.
///
/// Two things are true and are treated as such rather than wished away:
///
/// 1. A secret placed in the child's ENVIRONMENT is readable from
///    /proc/<pid>/environ by any process running as the same user, for the whole
///    life of the step. Measured, not assumed. Jenkins' `withCredentials` does
///    exactly this, and lift-and-shift compatibility requires Fogell to bind the
///    value too. The 0600 file is an additive companion, not same-UID isolation;
///    `environmentForPathOnly` retains the incompatible path-only alternative.
///
/// 2. Masking cannot be a security boundary. A script that can read a secret can
///    transform it, and the measured Jenkins masker handles literal, base64 and
///    case-folded forms while losing to `rev`, hex, substring and char-split —
///    silently, with the build green. Fogell masks the same registered forms but
///    treats masking as defence-in-depth and, crucially, is NOT silent when a
///    transformation likely escaped it.
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
    { /// The variable carrying the VALUE, exactly as Jenkins binds it.
      ///
      /// MEASURED on the pinned Jenkins (FG-044): `withCredentials([string(...,
      /// variable: 'TOKEN')])` puts the real value in `TOKEN` —
      /// `env | grep -c '^TOKEN='` is 1 and `${#TOKEN}` is the secret's length — and
      /// unsets it after the block. FG-070's original design bound only a
      /// `TOKEN_FILE` path and NO value, which is incompatible with running any real
      /// pipeline: every credential user reads `$TOKEN`. Lift-and-shift outranks a
      /// hardening property that was in any case already proven weaker than claimed
      /// (a same-UID reader follows the path and opens the file it owns).
      /// Receipts: `credentials-string` (binding) and `credentials-userpass-masking`
      /// (masking on stdout). The first emits no output, so it cannot back a masking claim.
      ValueVariable: string
      /// Companion variable carrying a path to a 0600 file with the same value. Kept
      /// as an ADDITION, not a replacement: scripts that prefer a file can use it.
      PathVariable: string
      /// Absolute path of the 0600 file holding the value.
      FilePath: string
      /// Text value retained for Jenkins-compatible environment binding and
      /// literal leak checks. Binary file credentials leave this empty.
      Value: string
      /// Immutable text forms shared by every lexical binding of one resolved
      /// credential. They are run-scoped metadata, not zeroized memory.
      Forms: SecretForms
      /// FG-044. True for a `file()` credential, where Jenkins binds the requested
      /// variable to a PATH rather than to the content. The content is still what gets
      /// masked — the path is not a secret, the bytes are.
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
        // The Jenkinsfile controls the environment-variable name. Keep that
        // untrusted string out of the filesystem namespace entirely: a GUID-only
        // leaf cannot add separators, rooted paths, or platform-hostile names.
        let path = Path.Combine(directory, $".secret-{unique}")
        writeSecretFileAtPathWithObserver path bytes observe

    let private createSecretFile (directory: string) (bytes: byte[]) =
        createSecretFileWithObserver directory bytes (fun _ _ -> ())

    /// Encodings the masker recognises. Text credentials retain the set Jenkins
    /// was measured to handle; file credentials add exact byte-base64 only when
    /// the caller's documented length floor admits it. The detector below has no
    /// Jenkins equivalent.
    let private registeredForms (includeByteEncoding: bool) (value: string) (bytes: byte[]) =
        [ "literal", value
          "upper", value.ToUpperInvariant()
          "lower", value.ToLowerInvariant()
          if includeByteEncoding then
              "base64", Convert.ToBase64String bytes ]
        |> List.filter (fun (_, v) -> v <> "")
        |> List.distinctBy snd

    /// Transformations Jenkins is measured to LEAK on. Fogell does not mask them
    /// either — masking every possible encoding is impossible — but it detects
    /// them and says so, which is the difference between a known gap and a silent
    /// one.
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
        // Very short binary encodings are ordinary low-entropy words (`DE AD` ->
        // `dead`). Treating them as proof of disclosure makes unrelated output fail.
        // Text credentials retain the measured Jenkins forms at every length;
        // File-content base64 masking requires eight bytes. Terminal hex detection
        // additionally requires at least four distinct byte values because length
        // alone admits repeated-byte strings such as eight NULs in ordinary output.
        // Masking is non-terminal defence-in-depth, so retaining exact base64 for
        // every sufficiently long binary credential cannot falsely fail a build.
        // Hex detection is terminal and therefore also requires the diversity
        // floor that keeps ordinary repeated-byte output from becoming leak proof.
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
        // REVIEW FIX (Codex, PR #15): a FIXED `.secret-<variable>` path meant two
        // bindings of the same variable — nested `withCredentials`, or concurrent
        // parallel branches — shared one file. The inner one overwrote the outer, and
        // revoking the inner deleted the file the outer's variable still pointed at.
        // Jenkins allocates a fresh temporary path per binding; so do we.
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

    /// Environment entries for a set of bindings: the VALUE (Jenkins parity) and the
    /// file path (our addition). Masking on every output path is what actually
    /// protects the value — see FG-071 — not its absence from the environment.
    ///
    /// A requested value belongs to the current lexical scope and may shadow an outer
    /// name. A generated `_FILE` companion is only an additive Fogell convenience, so
    /// it must not shadow a name the enclosing environment already owns.
    let environmentForPreserving (preserved: Set<string>) (bindings: SecretBinding list) =
        // REVIEW FIX (Codex, PR #15 round 5): the overlay is last-wins, so a generated
        // `X_FILE` companion could overwrite a variable the Jenkinsfile had EXPLICITLY
        // requested as `X_FILE` — handing the body a path where its configured credential
        // should be. Requested names win; a colliding companion is dropped.
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

    /// Compatibility entry point for callers with no enclosing environment to protect.
    let environmentFor (bindings: SecretBinding list) =
        environmentForPreserving Set.empty bindings

    /// FG-070's original, hardened form: the path ONLY, no value. Available for a
    /// caller that accepts the incompatibility.
    let environmentForPathOnly (bindings: SecretBinding list) =
        bindings |> List.map (fun b -> b.PathVariable, b.FilePath)

    /// Every form which is safe to redact as one complete match. Kept in one
    /// definition so line-oriented emitters and FG-236's raw-stream matcher do
    /// not silently disagree about file paths or derived encodings.
    let maskingForms (bindings: SecretBinding list) =
        bindings
        |> List.collect (fun b ->
            // A file() credential's BOUND VALUE is the path — Jenkins masks it
            // (`+ test -r ****` on the trace), so the path is a maskable form here
            // too, alongside the content and its encodings.
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

    /// Scan output that already crossed the registered-form matcher. Literal
    /// detection here would mistake the canonical `****` replacement for a
    /// one-character `*` credential; only transformations outside the masking
    /// inventory remain meaningful at this boundary.
    let detectUnregisteredLeaks (bindings: SecretBinding list) (maskedText: string) : Leak list =
        [ for b in bindings do
              for name, form in b.Forms.LeakForms do
                  if maskedText.Contains form then
                      { Variable = b.ValueVariable; Encoding = name } ]

    /// Detect a registered or transformed form which exists only after an
    /// engine-authored prefix and a provenance-bearing value are composed.
    /// Scanning the two halves independently misses this case, while scanning
    /// the whole value as ordinary text would mistake canonical `****` masker
    /// tokens for a literal `*` credential.
    let detectBoundaryLeaks (bindings: SecretBinding list) (prefix: string) (value: string) : Leak list =
        let crossesBoundary (form: string) =
            if prefix = "" || value = "" || form = "" then
                false
            else
                let composed = prefix + value
                let boundary = prefix.Length
                let mutable at = composed.IndexOf(form, StringComparison.Ordinal)
                let mutable found = false

                while not found && at >= 0 do
                    found <- at < boundary && at + form.Length > boundary
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
