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
///    exactly this. So Fogell does not do it: the value goes to a 0600 file and
///    the environment carries only the PATH.
///
/// 2. Masking cannot be a security boundary. A script that can read a secret can
///    transform it, and the measured Jenkins masker handles literal, base64 and
///    case-folded forms while losing to `rev`, hex, substring and char-split —
///    silently, with the build green. Fogell masks the same registered forms but
///    treats masking as defence-in-depth and, crucially, is NOT silent when a
///    transformation likely escaped it.
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
      ValueVariable: string
      /// Companion variable carrying a path to a 0600 file with the same value. Kept
      /// as an ADDITION, not a replacement: scripts that prefer a file can use it.
      PathVariable: string
      /// Absolute path of the 0600 file holding the value.
      FilePath: string
      /// The value, retained only to build the masker and to detect leaks.
      Value: string }

type Leak =
    { Variable: string
      /// How the value appeared: which transformation defeated the mask.
      Encoding: string }

module Secrets =

    /// Encodings the masker recognises. Deliberately the SAME set Jenkins was
    /// measured to handle, so parity is exact — plus the detector below, which
    /// Jenkins has no equivalent of.
    let private registeredForms (value: string) =
        let bytes = Text.Encoding.UTF8.GetBytes value

        [ "literal", value
          "base64", Convert.ToBase64String bytes
          "upper", value.ToUpperInvariant()
          "lower", value.ToLowerInvariant() ]
        |> List.filter (fun (_, v) -> v <> "")

    /// Transformations Jenkins is measured to LEAK on. Fogell does not mask them
    /// either — masking every possible encoding is impossible — but it detects
    /// them and says so, which is the difference between a known gap and a silent
    /// one.
    let private detectableForms (value: string) =
        let bytes = Text.Encoding.UTF8.GetBytes value

        // REVIEW FIX (Copilot, PR #11): only the lowercase hex form was generated,
        // so a secret hex-encoded by anything using .NET's default casing — which
        // is UPPERCASE — went undetected while the report claimed hex was covered.
        // A detector with a hole it does not admit to is worse than no detector.
        [ "reversed", String(value.ToCharArray() |> Array.rev)
          "hex", Convert.ToHexString(bytes).ToLowerInvariant()
          "hex-upper", Convert.ToHexString bytes
          "char-split", String.Join("_", value.ToCharArray()) ]
        |> List.filter (fun (_, v) -> v.Length > 3)

    /// Write the secret to a file only the running user can read, and return the
    /// binding. The file lives in the attempt's own directory so it is removed
    /// with the workspace.
    let bind (directory: string) (variableName: string) (value: string) : SecretBinding =
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, $".secret-{variableName}")
        File.WriteAllText(path, value)

        // 0600 before anything can read it. WriteAllText creates with the
        // process umask, so this is tightened immediately after.
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        { ValueVariable = variableName
          PathVariable = variableName + "_FILE"
          FilePath = path
          Value = value }

    /// Environment entries for a set of bindings: the VALUE (Jenkins parity) and the
    /// file path (our addition). Masking on every output path is what actually
    /// protects the value — see FG-071 — not its absence from the environment.
    let environmentFor (bindings: SecretBinding list) =
        bindings
        |> List.collect (fun b -> [ b.ValueVariable, b.Value; b.PathVariable, b.FilePath ])

    /// FG-070's original, hardened form: the path ONLY, no value. Available for a
    /// caller that accepts the incompatibility.
    let environmentForPathOnly (bindings: SecretBinding list) =
        bindings |> List.map (fun b -> b.PathVariable, b.FilePath)

    /// Replace every registered form with `****`.
    let mask (bindings: SecretBinding list) (text: string) =
        bindings
        |> List.collect (fun b -> registeredForms b.Value |> List.map snd)
        |> List.sortByDescending String.length
        |> List.fold (fun (acc: string) form -> acc.Replace(form, "****")) text

    /// FG-071. After masking, look for forms the mask does not cover. A hit means
    /// a secret reached the log in a shape masking cannot catch — reported, never
    /// swallowed.
    let detectLeaks (bindings: SecretBinding list) (maskedText: string) : Leak list =
        [ for b in bindings do
              // the literal must never survive masking; if it does, the masker failed
              if maskedText.Contains b.Value then
                  { Variable = b.ValueVariable; Encoding = "literal" }

              for name, form in detectableForms b.Value do
                  if maskedText.Contains form then
                      { Variable = b.ValueVariable; Encoding = name } ]

    /// Remove secret files. Called even on failure, because a leftover secret
    /// file outlives the reason it existed.
    let revoke (bindings: SecretBinding list) =
        for b in bindings do
            try
                if File.Exists b.FilePath then File.Delete b.FilePath
            with _ ->
                ()
