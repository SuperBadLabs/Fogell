/// Shared prelude for the ported audit tools (FG-226).
///
/// WHY THIS FILE EXISTS AT ALL. `fflat` compiles ONE script, so eight ports would
/// otherwise carry eight copies of the same helpers and drift apart the way the
/// three copies of the timestamp rule did. `#load` is the one seam that keeps a
/// single definition, and it is verified to survive bflat's AOT path.
///
/// TWO RUNTIME HAZARDS THIS FILE EXISTS TO CONTAIN, both measured on 2026-08-27:
///
/// 1. `sprintf`/`printfn` COMPILE CLEAN AND THROW AT RUNTIME under AOT
///    (`NotSupportedException` out of FSharp.Core's reflection-based formatter).
///    Nothing here may use them; `String.Format` is the substitute and is proven
///    to work, alignment specifiers included.
///
/// 2. .NET REGEX AND CHARACTER PREDICATES ARE UNICODE-AWARE; JAVA'S ARE NOT.
///    babashka's regexes are `java.util.regex`, where `\s` is exactly
///    `[ \t\n\x0B\f\r]` and `\d` is exactly `[0-9]`. .NET's `\s` also matches
///    U+00A0 and friends. A port that keeps the pattern text unchanged therefore
///    changes the VERDICT on some inputs without changing a visible character.
///    `javaRx` performs that translation explicitly so every ported pattern
///    stays reviewable against its original.
///
///    THE EXPOSURE IS LATENT, NOT ACTIVE, and saying otherwise was an overclaim
///    this file carried until the pre-push verifier counted: the tracked tree
///    holds ZERO of these code points outside sealed evidence, so no ported
///    audit changes its verdict on today's inputs. The translation is here
///    because the divergence is SILENT when it does arrive, not because it has.
///    An earlier version of this comment asserted the execution board "contains"
///    them; it contains none.
module Prelude

open System
open System.IO
open System.Text
open System.Diagnostics
open System.Text.RegularExpressions

// ---------------------------------------------------------------- output

let out (s: string) = Console.Out.WriteLine s
let eout (s: string) = Console.Error.WriteLine s

/// Left-justify to width, the `%-Ns` of Clojure's `format`.
let padR (w: int) (s: string) = if s.Length >= w then s else s + String(' ', w - s.Length)

/// Defined HERE rather than at the end of the file because `runOrDie` below
/// depends on it and F# resolves in declaration order.
let exitWith (code: int) : 'a =
    Console.Out.Flush()
    Console.Error.Flush()
    exit code

// ---------------------------------------------------------------- java string semantics

/// `java.lang.Character.isWhitespace`, which is NOT `Char.IsWhiteSpace`. Java
/// and .NET disagree on four code points, each verified against babashka:
///
///   U+00A0, U+2007, U+202F — the NON-BREAKING spaces. Java excludes them on
///     the grounds that they are not breaks; .NET counts all three.
///   U+0085 NEL — .NET counts it; Java's list does not, since it is a control
///     character rather than a separator.
///
/// U+0085 was MISSED in the first version of this function and found by the
/// pre-push verifier. Every one of these flips a verdict without changing a
/// visible character, which is the whole reason this function exists — and none
/// of them occurs in the tracked tree today, so the exposure is latent.
let javaIsWhitespace (c: char) =
    if c = '\u00A0' || c = '\u2007' || c = '\u202F' || c = '\u0085' then false
    elif Char.IsWhiteSpace c then true
    else c >= '\u001C' && c <= '\u001F'

/// `clojure.string/blank?` — null, empty, or all `Character/isWhitespace`.
let blank (s: string) = isNull s || s |> Seq.forall javaIsWhitespace

/// `clojure.string/trim` trims by `Character/isWhitespace` — NOT by
/// `java.lang.String.trim`, which strips code points <= U+0020.
///
/// THIS COMMENT ASSERTED `String.trim` AND THE CODE FOLLOWED IT, so em space
/// (U+2003) and ideographic space (U+3000) survived a trim that Clojure
/// removes. Caught by the pre-push verifier, which measured both engines
/// instead of reading this sentence. The two pinned fixtures could not tell the
/// candidates apart, which is what made it survive: a fixture that passes under
/// either semantics is decoration.
///
/// Defined AFTER `javaIsWhitespace` because it now depends on it.
let javaTrim (s: string) =
    if isNull s then "" else
    let mutable i = 0
    let mutable j = s.Length
    while i < j && javaIsWhitespace s.[i] do i <- i + 1
    while j > i && javaIsWhitespace s.[j - 1] do j <- j - 1
    s.Substring(i, j - i)

/// Drop trailing empty strings, which is what both `split` and `split-lines`
/// do in Clojure and what neither `Regex.Split` nor `String.Split` does.
let private dropTrailingEmpties (xs: string list) =
    let rec go (rev: string list) =
        match rev with
        | "" :: rest -> go rest
        | _ -> List.rev rev
    go (List.rev xs)

/// `clojure.string/split-lines` — splits on `\r?\n`, trailing empties removed.
/// ONE KNOWN DIVERGENCE, left deliberately: on empty input this returns `[]`
/// where Clojure returns `[""]`. Every caller filters blanks, so it is inert,
/// and changing behaviour nothing depends on adds risk without removing any —
/// the opposite call from `cljSplit`, which had no callers at all and was
/// deleted rather than pinned.
let splitLines (s: string) =
    if isNull s || s = "" then []
    else Regex.Split(s, "\r?\n") |> Array.toList |> dropTrailingEmpties

// ---------------------------------------------------------------- regex

/// Rewrite a `java.util.regex` pattern into the .NET pattern with the SAME
/// meaning FOR THE SHORTHAND CLASSES. The rewrite is deliberately literal and
/// tracks character classes, where a shorthand contributes members rather than
/// a nested class.
///
/// WHAT IT DOES NOT TRANSLATE, stated because this docstring claimed the
/// shorthand classes were the only difference and that is measurably false for
/// three constructs in live use: `\b` (Java's word boundary is ASCII-`\w`
/// based, .NET's is Unicode), `.` (the two exclude different code points), and
/// `$` (multiline end-of-line around CRLF). All three are LATENT here — the
/// tree has no CRLF and no non-ASCII word character adjacent to a tracked
/// identifier in a comment.
///
/// THEY DO NOT ALL FAIL IN THE SAME DIRECTION, and this comment said they did.
/// `\b` and `$` fail OPEN — .NET matches less, so a divergence would MISS. `.`
/// fails CLOSED — .NET's any-char matches MORE than Java's, so `destructuredRx`
/// would over-extract and report a token Java never produced. Two of three miss,
/// one false-positives; the blanket claim was inverted for that one.
let javaPattern (p: string) =
    let sb = StringBuilder()
    let mutable i = 0
    let mutable inClass = false
    while i < p.Length do
        let c = p.[i]
        if c = '\\' && i + 1 < p.Length then
            let n = p.[i + 1]
            let repl =
                if inClass then
                    match n with
                    | 's' -> Some " \t\n\011\012\r"
                    | 'd' -> Some "0-9"
                    | 'w' -> Some "a-zA-Z0-9_"
                    | _ -> None
                else
                    match n with
                    | 's' -> Some "[ \t\n\011\012\r]"
                    | 'S' -> Some "[^ \t\n\011\012\r]"
                    | 'd' -> Some "[0-9]"
                    | 'D' -> Some "[^0-9]"
                    | 'w' -> Some "[a-zA-Z0-9_]"
                    | 'W' -> Some "[^a-zA-Z0-9_]"
                    | _ -> None
            match repl with
            | Some r -> sb.Append(r) |> ignore
            | None -> sb.Append(c).Append(n) |> ignore
            i <- i + 2
        else
            if c = '[' && not inClass then inClass <- true
            elif c = ']' && inClass then inClass <- false
            sb.Append(c) |> ignore
            i <- i + 1
    sb.ToString()

/// Build a regex carrying java.util.regex semantics.
let javaRx (p: string) = Regex(javaPattern p)

// `cljSplit` WAS HERE AND IS DELETED RATHER THAN FIXED. It claimed to be
// `clojure.string/split` and was not: `Regex.Split` interleaves captured groups
// where `Pattern.split` discards them, .NET emits a leading empty on a
// zero-width match at position 0 where Java suppresses it, and the two disagree
// on empty input. It had ZERO production callers — only its own definition and
// the two fixtures pinning it, both of which passed under either semantics.
// Deleting dead code beats pinning semantics nothing depends on. Found by the
// pre-push verifier, which was asked to assume a fourth divergence existed.

// ---------------------------------------------------------------- process

type Ran = { Rc: int; Out: string; Err: string }

/// Capture a child process EXACTLY, the way `babashka.process`'s `:out :string`
/// does.
///
/// Two properties are load-bearing and neither is the obvious spelling:
///
/// - stdout and stderr are drained CONCURRENTLY. Reading one to completion
///   before the other deadlocks the moment a child fills the other pipe's
///   buffer, and `rg` over this repository does exactly that.
/// - the streams are read WHOLE rather than line by line. The event-based
///   `OutputDataReceived` path hands back lines with their terminators
///   stripped, so reassembling it appends a trailing newline the child never
///   wrote. `sync-scm-cases` compares captured `git show` output against a
///   case body byte for byte to decide whether to push; under the line-based
///   spelling every body lacking a final newline compares unequal and the
///   sync pushes on every run, which is precisely the receipt churn that
///   tool exists to prevent.
/// A RELATIVE PROGRAM PATH RESOLVES AGAINST `dir`, NOT AGAINST THE CALLER'S CWD.
/// `babashka.process/shell` with `:dir` resolves the program relative to that
/// directory; .NET's `ProcessStartInfo.FileName` resolves it against the PARENT
/// process's working directory and treats `WorkingDirectory` as the child's cwd
/// only. Ported verbatim, that silently ran a DIFFERENT PROGRAM: the
/// scorecard receipt-mapping proof builds a scratch root with a stubbed
/// `scripts/verify-corpus.sh`, and the port reached past it to the real
/// repository's copy. On this machine the real one succeeds because the corpus
/// is mounted, so the proof passed; on a hosted runner it refuses and the
/// blocking proof failed. Found by CI (run 33196985961), not by any local run.
///
/// A BARE NAME IS LEFT ALONE so it still goes through PATH, which is what
/// `git`, `gh` and `dotnet` rely on here and what babashka does too. Only a
/// relative path containing a separator is resolved against `dir`.
let runIn (dir: string) (env: (string * string) list) (exe: string) (args: string list) =
    let exe =
        if dir <> ""
           && not (Path.IsPathRooted exe)
           && (exe.Contains(string Path.DirectorySeparatorChar) || exe.Contains "/")
        then Path.GetFullPath(Path.Combine(dir, exe))
        else exe
    let psi = ProcessStartInfo(exe)
    for a in args do psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    if dir <> "" then psi.WorkingDirectory <- dir
    for (k, v) in env do psi.Environment.[k] <- v
    use p = new Process()
    p.StartInfo <- psi
    p.Start() |> ignore
    let so = p.StandardOutput.ReadToEndAsync()
    let se = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { Rc = p.ExitCode; Out = so.Result; Err = se.Result }

let run (exe: string) (args: string list) = runIn "" [] exe args

/// Run a child process and ABORT if it fails.
///
/// THE DEFAULT DIRECTION OF `babashka.process/shell` IS TO THROW. A call without
/// `:continue true` aborts the script on a non-zero exit; `runIn` returns the
/// record instead, so every ported call site that dropped it converted an abort
/// into a SILENT SUCCESS. Measured on the originals: `sync-scm-cases.bb` made 9
/// shell calls of which only 2 were `:continue true`, and `review-rounds.bb`
/// made 2 with none — so a failed clone, push, or `gh api` stopped the babashka
/// script and did not stop the port. `review-rounds` printed "0 comments across
/// 0 review(s)", which a reader triaging a PR would take for "nothing to
/// triage"; `sync-scm-cases` printed "synced" for a branch it had not pushed.
///
/// Raised by Codex on PR #180 as two findings; it is one class, and the audit it
/// prompted found eight sites rather than two.
let runOrDie (what: string) (dir: string) (env: (string * string) list) (exe: string) (args: string list) =
    let r = runIn dir env exe args
    if r.Rc <> 0 then
        eout ("FAIL: " + what + " exited " + string r.Rc)
        let detail = javaTrim (r.Err + r.Out)
        if detail <> "" then eout detail
        exitWith 1
    r

let runOk (r: Ran) = r.Rc = 0

// ---------------------------------------------------------------- files

/// Deterministic, ordinal-sorted glob. `babashka.fs/glob` walks in filesystem
/// order; every caller here needs a stable order, so sorting once at the source
/// removes a class of ordering drift between the two engines.
let glob (dir: string) (pattern: string) =
    if not (Directory.Exists dir) then []
    else
        Directory.GetFiles(dir, pattern)
        |> Array.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

let slurp (path: string) = File.ReadAllText path
let spit (path: string) (contents: string) = File.WriteAllText(path, contents)

