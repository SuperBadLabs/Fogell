namespace Fogell.Execution

open System
open System.IO
open Fogell.Admission

/// FG-030. Each attempt gets one fresh, normalized workspace beneath a
/// canonical root.
///
/// The rejection list is not hypothetical: a Jenkinsfile is untrusted input, and
/// `dir('../../etc')` or a symlinked path is how a step escapes its workspace.
/// ADR 0008 requires absolute paths, traversal and symlink components to be
/// refused rather than normalized away.
module Workspace =

    type Error =
        | AbsolutePath of string
        | Traversal of string
        | SymlinkComponent of string
        | NonDirectoryParent of string
        | AlreadyExists of string
        | OutsideRoot of string
        | MaterializationFailed of string * string

        member this.Describe =
            match this with
            | AbsolutePath p -> $"'{p}' is absolute; workspace paths must be relative"
            | Traversal p -> $"'{p}' contains a parent-directory traversal"
            | SymlinkComponent p -> $"'{p}' passes through a symlink"
            | NonDirectoryParent p -> $"'{p}' has a parent that is not a directory"
            | AlreadyExists p -> $"'{p}' already exists; each attempt gets a fresh workspace"
            | OutsideRoot p -> $"'{p}' resolves outside the canonical workspace root"
            | MaterializationFailed(p, why) -> $"'{p}' could not be materialized beneath the workspace: {why}"

    /// Validate a *relative* path a pipeline asked for (e.g. `dir('sub/deep')`).
    let validateRelative (relative: string) : Result<unit, Error> =
        if String.IsNullOrWhiteSpace relative then
            Result.Error(Traversal relative)
        elif Path.IsPathRooted relative then
            Result.Error(AbsolutePath relative)
        else
            let parts =
                relative.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

            if parts |> Array.exists (fun p -> p = ".." ) then
                Result.Error(Traversal relative)
            else
                Result.Ok()

    /// Resolve a relative path under `root`, refusing anything that escapes it
    /// or passes through a symlink. Symlinks are checked component by component
    /// because resolving the whole path first would hide the escape.
    let resolveUnder (root: string) (relative: string) : Result<string, Error> =
        match validateRelative relative with
        | Result.Error e -> Result.Error e
        | Result.Ok() ->
            let canonicalRoot = Path.GetFullPath root
            let candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relative))

            if
                not (
                    candidate = canonicalRoot
                    || candidate.StartsWith(canonicalRoot + string Path.DirectorySeparatorChar))
            then
                Result.Error(OutsideRoot relative)
            else
                // walk each existing component; a symlink anywhere on the path
                // means the final location is not where it appears to be
                let rec check (current: string) (remaining: string list) =
                    match remaining with
                    | [] -> Result.Ok candidate
                    | part :: rest ->
                        let next = Path.Combine(current, part)

                        if Directory.Exists next || File.Exists next then
                            let info = FileInfo next

                            if info.LinkTarget <> null then
                                Result.Error(SymlinkComponent relative)
                            elif File.Exists next && not (List.isEmpty rest) then
                                Result.Error(NonDirectoryParent relative)
                            else
                                check next rest
                        else
                            // does not exist yet: nothing further to inspect
                            Result.Ok candidate

                relative.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
                |> check canonicalRoot

    /// Materialize an already-resolved logical cwd immediately before an effect
    /// which Jenkins proves creates it (durable task or SCM launch). Re-resolving
    /// from the attempt root at the launch boundary narrows the check/create race
    /// after `dir` established its logical FilePath. Callers must not
    /// use this for context-only or read-only steps: absence is observable state.
    let materializeUnder (root: string) (target: string) : Result<unit, Error> =
        let canonicalRoot = Path.GetFullPath root
        let canonicalTarget = Path.GetFullPath target

        if canonicalTarget = canonicalRoot then
            if Directory.Exists canonicalRoot then
                let rootInfo = DirectoryInfo canonicalRoot

                if rootInfo.LinkTarget = null then
                    Result.Ok()
                else
                    Result.Error(SymlinkComponent target)
            else
                Result.Error(MaterializationFailed(target, "workspace root does not exist"))
        else
            let relative = Path.GetRelativePath(canonicalRoot, canonicalTarget)

            match resolveUnder canonicalRoot relative with
            | Result.Error e -> Result.Error e
            | Result.Ok resolved when resolved <> canonicalTarget ->
                Result.Error(OutsideRoot target)
            | Result.Ok resolved ->
                try
                    Directory.CreateDirectory resolved |> ignore
                    Result.Ok()
                with ex ->
                    Result.Error(MaterializationFailed(relative, $"{ex.GetType().Name}: {ex.Message}"))

    /// Create a fresh workspace for an attempt. Refuses to reuse a directory:
    /// a leftover workspace is how one build's output contaminates the next.
    let createFresh (root: string) (attemptKey: string) : Result<string, Error> =
        match resolveUnder root attemptKey with
        | Result.Error e -> Result.Error e
        | Result.Ok path ->
            if Directory.Exists path || File.Exists path then
                Result.Error(AlreadyExists attemptKey)
            else
                Directory.CreateDirectory path |> ignore
                Result.Ok path
