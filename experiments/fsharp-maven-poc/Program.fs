open System
open System.Diagnostics
open System.IO

type Context =
    { Project: string
      Target: string
      Classes: string
      TestClasses: string
      Artifact: string
      Version: string
      Release: string
      Dependencies: (string * string) list }

let tag (name: string) (xml: string) =
    let opening = "<" + name + ">"
    let closing = "</" + name + ">"
    let start = xml.IndexOf(opening, StringComparison.Ordinal)

    if start < 0 then
        None
    else
        let first = start + opening.Length
        let last = xml.IndexOf(closing, first, StringComparison.Ordinal)

        if last < 0 then
            None
        else
            let value = xml.Substring(first, last - first).Trim()
            if value.Contains('<') then None else Some value

let requiredTag name xml =
    tag name xml
    |> Option.defaultWith (fun () -> failwith ("missing <" + name + "> in pom.xml"))

let blocks (name: string) (xml: string) =
    let opening = "<" + name + ">"
    let closing = "</" + name + ">"

    let rec collect offset found =
        let start = xml.IndexOf(opening, offset, StringComparison.Ordinal)

        if start < 0 then
            List.rev found
        else
            let first = start + opening.Length
            let last = xml.IndexOf(closing, first, StringComparison.Ordinal)

            if last < 0 then
                List.rev found
            else
                collect (last + closing.Length) (xml.Substring(first, last - first) :: found)

    collect 0 []

let files root =
    if Directory.Exists root then
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.sort
        |> Seq.toList
    else
        []

let javaFiles root = files root |> List.filter (fun path -> Path.GetExtension(path) = ".java")

let copyResources source destination =
    for file in files source do
        let relative = Path.GetRelativePath(source, file)
        let output = Path.Combine(destination, relative)
        Directory.CreateDirectory(Path.GetDirectoryName(output)) |> ignore
        File.Copy(file, output, true)

let run program (arguments: string list) =
    let start = ProcessStartInfo(program)
    start.UseShellExecute <- false

    for argument in arguments do
        start.ArgumentList.Add(argument)

    use child = Process.Start(start)
    child.WaitForExit()

    if child.ExitCode <> 0 then
        failwith (program + " exited with status " + child.ExitCode.ToString())

let pathList (values: string list) = String.Join(Path.PathSeparator, values)

let load project repo =
    let pom = File.ReadAllText(Path.Combine(project, "pom.xml"))
    let artifact = requiredTag "artifactId" pom
    let version = requiredTag "version" pom
    let release = tag "maven.compiler.release" pom |> Option.defaultValue "17"

    let dependencies =
        blocks "dependency" pom
        |> List.map (fun block ->
            let group = requiredTag "groupId" block
            let name = requiredTag "artifactId" block
            let depVersion = requiredTag "version" block
            let scope = tag "scope" block |> Option.defaultValue "compile"

            let jar =
                Path.Combine(
                    repo,
                    group.Replace('.', Path.DirectorySeparatorChar),
                    name,
                    depVersion,
                    name + "-" + depVersion + ".jar"
                )

            if not (File.Exists jar) then
                failwith ("dependency absent from local repository: " + jar)

            scope, jar)

    let target = Path.Combine(project, "target")

    { Project = project
      Target = target
      Classes = Path.Combine(target, "classes")
      TestClasses = Path.Combine(target, "test-classes")
      Artifact = artifact
      Version = version
      Release = release
      Dependencies = dependencies }

let jars scope context =
    context.Dependencies
    |> List.choose (fun (depScope, jar) ->
        if depScope = "compile" || depScope = scope then Some jar else None)

let compile context =
    Directory.CreateDirectory(context.Classes) |> ignore
    let sources = javaFiles (Path.Combine(context.Project, "src/main/java"))

    if not sources.IsEmpty then
        let classpath = jars "compile" context |> pathList

        let classpathArgs =
            if classpath = "" then [] else [ "-classpath"; classpath ]

        run
            "javac"
            ([ "--release"
               context.Release
               "-g"
               "-encoding"
               "UTF-8"
               "-d"
               context.Classes ]
             @ classpathArgs
             @ sources)

    copyResources (Path.Combine(context.Project, "src/main/resources")) context.Classes
    Console.WriteLine("Compiled " + sources.Length.ToString() + " main sources")

let test context =
    compile context
    Directory.CreateDirectory(context.TestClasses) |> ignore
    let sourceRoot = Path.Combine(context.Project, "src/test/java")
    let sources = javaFiles sourceRoot
    let classpath = context.Classes :: jars "test" context |> pathList

    if not sources.IsEmpty then
        run
            "javac"
            ([ "--release"
               context.Release
               "-g"
               "-encoding"
               "UTF-8"
               "-d"
               context.TestClasses
               "-classpath"
               classpath ]
             @ sources)

    copyResources (Path.Combine(context.Project, "src/test/resources")) context.TestClasses

    let tests =
        sources
        |> List.filter (fun source -> Path.GetFileName(source).EndsWith("Test.java", StringComparison.Ordinal))
        |> List.map (fun source ->
            Path.GetRelativePath(sourceRoot, source)
                .Replace(Path.DirectorySeparatorChar, '.')
                .Replace(".java", ""))

    if not tests.IsEmpty then
        let testClasspath = pathList [ context.TestClasses; classpath ]
        run "java" ([ "-classpath"; testClasspath; "org.junit.runner.JUnitCore" ] @ tests)

    Console.WriteLine(
        "Compiled "
        + sources.Length.ToString()
        + " test sources; ran "
        + tests.Length.ToString()
        + " test classes"
    )

let package context =
    test context
    let output = Path.Combine(context.Target, context.Artifact + "-" + context.Version + ".jar")
    run "jar" [ "--create"; "--file"; output; "-C"; context.Classes; "." ]
    Console.WriteLine("Packaged " + output)

let parseArguments (arguments: string array) =
    let rec parse index project repo goals =
        if index >= arguments.Length then
            project, repo, List.rev goals
        else
            match arguments[index] with
            | "--project" when index + 1 < arguments.Length ->
                parse (index + 2) arguments[index + 1] repo goals
            | "--repo" when index + 1 < arguments.Length ->
                parse (index + 2) project arguments[index + 1] goals
            | ("clean" | "compile" | "test" | "package") as goal ->
                parse (index + 1) project repo (goal :: goals)
            | other -> failwith ("unknown argument: " + other)

    let defaultRepo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2/repository")
    let project, repo, goals = parse 0 (Directory.GetCurrentDirectory()) defaultRepo []
    project, repo, if goals.IsEmpty then [ "package" ] else goals

[<EntryPoint>]
let main arguments =
    try
        let project, repo, goals = parseArguments arguments
        let context = load project repo

        for goal in goals do
            match goal with
            | "clean" ->
                if Directory.Exists context.Target then
                    Directory.Delete(context.Target, true)

                Console.WriteLine("Cleaned target")
            | "compile" -> compile context
            | "test" -> test context
            | "package" -> package context
            | _ -> failwith ("unknown goal: " + goal)

        0
    with error ->
        Console.Error.WriteLine("mini_mvn_fsharp: " + error.Message)
        1
