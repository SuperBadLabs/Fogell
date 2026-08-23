namespace Fogell.Groovy.Interpreter

/// The interpreter's capability boundary.
///
/// ADR 0002 accepted interpretation to reach coverage a static IR cannot, and
/// explicitly accepted the consequence: interpreting untrusted Groovy inherits
/// Jenkins' script-security problem. Jenkins answered with a script-approval
/// plugin and a whitelist. Fogell answers with a *closed* value type (no case
/// wraps a .NET object) plus this deny-by-default gate on every call.
///
/// The rule: a free call is either a registered pipeline step, a function the
/// script defined itself, or a rejection. There is no reflective escape,
/// because there is nothing to reflect over.
type Capability =
    /// A pipeline step the host implements (`sh`, `echo`, `archiveArtifacts`).
    | Step of string
    /// A pure helper the interpreter itself provides.
    | Builtin of string

type Denial =
    { Attempted: string
      Reason: string }

module Sandbox =

    /// FG-213. Public zero-argument accessors on the nominal JUnit summary.
    /// Admission is by name only; Interpreter dispatch confines every name to
    /// VJUnitSummary and rejects free calls, other receivers, and every argument
    /// or trailing-closure shape.
    let junitSummaryCountGetters =
        set [ "getTotalCount"; "getFailCount"; "getSkipCount"; "getPassCount" ]

    /// Pure/interpreter-managed method names that cannot touch the outside world.
    /// Most are string/collection helpers; nominal getter names dispatch only on
    /// their closed [Value] receiver and fail closed for every other use.
    let builtins: Set<string> =
        Set.union
            junitSummaryCountGetters
            (set
                [ "size"; "length"; "toString"; "toInteger"; "trim"; "toUpperCase"; "toLowerCase"
                  "split"; "join"; "contains"; "startsWith"; "endsWith"; "replace"; "substring"
                  // FG-177: get/containsKey are admitted as NAMES only; the
                  // interpreter implements their narrow signatures solely for the
                  // nominal SCM return map and rejects every other receiver/shape.
                  "get"; "containsKey"; "keySet"; "values"; "isEmpty"; "collect"; "each"; "find"; "findAll"; "any"; "every"
                  "sort"; "reverse"; "first"; "last"; "tokenize"; "readLines"; "minus"; "plus"
                  // FG-189/FG-195: `f.call(x)` is the explicit closure-invocation spelling;
                  // the interpreter dispatches it only on a VClosure receiver, so admitting
                  // the NAME opens nothing else
                  "call" ])

    /// Names that are unambiguously an escape attempt. Kept as an explicit
    /// deny-list purely so the *diagnostic* names what was tried; the closed
    /// Value type is what actually makes them impossible.
    let knownEscapes: Set<string> =
        set
            [ "File"; "FileInputStream"; "FileOutputStream"; "RandomAccessFile"
              "ProcessBuilder"; "Runtime"; "System"; "Class"; "ClassLoader"
              "GroovyShell"; "GroovyClassLoader"; "Eval"; "evaluate"
              "URL"; "URLConnection"; "Socket"; "ServerSocket"; "HttpURLConnection"
              "Thread"; "Unsafe"; "MethodHandles"; "getClass"; "forName"; "newInstance"
              "getDeclaredMethod"; "getDeclaredField"; "setAccessible"; "invoke"
              "execute"; "exec" ]

    /// Decide whether a free call is admissible.
    let admitCall (registeredSteps: Set<string>) (definedFunctions: Set<string>) (name: string) =
        // a constructor expression is parsed as `new X`; never admissible
        let bare = if name.StartsWith "new " then name.Substring 4 else name

        if definedFunctions.Contains name then Ok(Builtin name)
        elif registeredSteps.Contains name then Ok(Step name)
        elif builtins.Contains name then Ok(Builtin name)
        elif knownEscapes.Contains bare || name.StartsWith "new " then
            Error
                { Attempted = name
                  Reason =
                    "reflection, file, process and network access are not reachable from a pipeline script; \
                     the interpreter's value type cannot represent a host object" }
        else
            Error
                { Attempted = name
                  Reason = "not a registered step, a script-defined function, or a pure builtin" }

    /// Method dispatch on a value. Only pure builtins are reachable — there is
    /// no path to an arbitrary member, by construction.
    let admitMethod (name: string) =
        if builtins.Contains name then
            Ok(Builtin name)
        elif knownEscapes.Contains name then
            Error
                { Attempted = name
                  Reason = "host reflection is not reachable from a pipeline script" }
        else
            Error
                { Attempted = name
                  Reason = "not a pure builtin; host methods are not reachable" }
