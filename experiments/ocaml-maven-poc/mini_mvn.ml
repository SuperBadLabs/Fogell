(* A deliberately narrow Maven lifecycle experiment. See README.md for its contract. *)
open Unix

let fail message = prerr_endline ("mini_mvn: " ^ message); exit 1
let join = Filename.concat
let exists = Sys.file_exists

let read_file path =
  let channel = open_in_bin path in
  Fun.protect ~finally:(fun () -> close_in channel) (fun () ->
    really_input_string channel (in_channel_length channel))

let tag name xml =
  let pattern = Str.regexp ("<" ^ name ^ ">[ \t\r\n]*\\([^<]*\\)</" ^ name ^ ">") in
  try
    ignore (Str.search_forward pattern xml 0);
    String.trim (Str.matched_group 1 xml)
  with Not_found -> fail ("missing <" ^ name ^ "> in pom.xml")

let optional_tag name xml fallback =
  let pattern = Str.regexp ("<" ^ name ^ ">[ \t\r\n]*\\([^<]*\\)</" ^ name ^ ">") in
  try
    ignore (Str.search_forward pattern xml 0);
    String.trim (Str.matched_group 1 xml)
  with Not_found -> fallback

let blocks name xml =
  let opening = Str.regexp ("<" ^ name ^ ">") in
  let closing = Str.regexp ("</" ^ name ^ ">") in
  let rec loop offset acc =
    try
      ignore (Str.search_forward opening xml offset);
      let start = Str.match_end () in
      ignore (Str.search_forward closing xml start);
      let stop = Str.match_beginning () in
      let next = Str.match_end () in
      loop next (String.sub xml start (stop - start) :: acc)
    with Not_found -> List.rev acc
  in
  loop 0 []

let rec mkdir path =
  if not (exists path) then (mkdir (Filename.dirname path); Unix.mkdir path 0o755)

let rec remove path =
  if exists path then
    match (Unix.lstat path).st_kind with
    | S_DIR -> Array.iter (fun name -> remove (join path name)) (Sys.readdir path); Unix.rmdir path
    | _ -> Unix.unlink path

let rec files root =
  if not (exists root) then []
  else if Sys.is_directory root then
    Sys.readdir root |> Array.to_list |> List.sort String.compare
    |> List.concat_map (fun name -> files (join root name))
  else [root]

let copy_file source destination =
  mkdir (Filename.dirname destination);
  let input_channel = open_in_bin source in
  Fun.protect ~finally:(fun () -> close_in input_channel) (fun () ->
    let output_channel = open_out_bin destination in
    Fun.protect ~finally:(fun () -> close_out output_channel) (fun () ->
      let buffer = Bytes.create 65536 in
      let rec loop () =
        let count = input input_channel buffer 0 (Bytes.length buffer) in
        if count > 0 then (output output_channel buffer 0 count; loop ())
      in loop ()))

let run program arguments =
  let argv = Array.of_list (program :: arguments) in
  let pid = Unix.create_process program argv Unix.stdin Unix.stdout Unix.stderr in
  match snd (Unix.waitpid [] pid) with
  | WEXITED 0 -> ()
  | WEXITED code -> fail (Printf.sprintf "%s exited with status %d" program code)
  | WSIGNALED signal | WSTOPPED signal ->
      fail (Printf.sprintf "%s stopped with signal %d" program signal)

let path_from root path = join root path
let is_java path = Filename.check_suffix path ".java"
let class_name source_root source =
  let relative = String.sub source (String.length source_root + 1)
      (String.length source - String.length source_root - 1) in
  let no_suffix = Filename.chop_suffix relative ".java" in
  String.map (fun c -> if c = '/' then '.' else c) no_suffix

let () =
  let project = ref (Sys.getcwd ()) in
  let repo = ref (join (Sys.getenv "HOME") ".m2/repository") in
  let goals = ref [] in
  let rec parse = function
    | [] -> ()
    | "--project" :: value :: rest -> project := value; parse rest
    | "--repo" :: value :: rest -> repo := value; parse rest
    | (("clean" | "compile" | "test" | "package") as goal) :: rest ->
        goals := !goals @ [goal]; parse rest
    | option :: _ -> fail ("unknown argument: " ^ option)
  in
  parse (List.tl (Array.to_list Sys.argv));
  if !goals = [] then goals := ["package"];
  let project = Filename.concat !project "" in
  let pom_path = path_from project "pom.xml" in
  if not (exists pom_path) then fail ("no pom.xml in " ^ project);
  let pom = read_file pom_path in
  let artifact = tag "artifactId" pom in
  let version = tag "version" pom in
  let release = optional_tag "maven.compiler.release" pom "17" in
  let target = path_from project "target" in
  let classes = join target "classes" in
  let test_classes = join target "test-classes" in
  let main_sources = path_from project "src/main/java" in
  let test_sources = path_from project "src/test/java" in
  let dependencies = blocks "dependency" pom |> List.map (fun block ->
    let group = tag "groupId" block in
    let artifact = tag "artifactId" block in
    let version = tag "version" block in
    let scope = optional_tag "scope" block "compile" in
    let group_path = String.map (fun c -> if c = '.' then '/' else c) group in
    let jar = join !repo (group_path ^ "/" ^ artifact ^ "/" ^ version ^ "/" ^ artifact ^ "-" ^ version ^ ".jar") in
    if not (exists jar) then fail ("dependency absent from local repository: " ^ jar);
    (scope, jar)) in
  let jars scope = dependencies |> List.filter_map (fun (s, jar) ->
    if s = "compile" || s = scope then Some jar else None) in
  let copy_resources source destination =
    files source |> List.iter (fun file ->
      let relative = String.sub file (String.length source + 1)
        (String.length file - String.length source - 1) in
      copy_file file (join destination relative)) in
  let compile () =
    mkdir classes;
    let sources = List.filter is_java (files main_sources) in
    if sources <> [] then (
      let cp = jars "compile" in
      let cp_args = if cp = [] then [] else ["-classpath"; String.concat ":" cp] in
      run "javac" (["--release"; release; "-g"; "-encoding"; "UTF-8"; "-d"; classes] @ cp_args @ sources));
    copy_resources (path_from project "src/main/resources") classes;
    Printf.printf "Compiled %d main sources\n%!" (List.length sources)
  in
  let test () =
    compile ();
    mkdir test_classes;
    let sources = List.filter is_java (files test_sources) in
    let cp = String.concat ":" (classes :: jars "test") in
    if sources <> [] then
      run "javac" (["--release"; release; "-g"; "-encoding"; "UTF-8"; "-d"; test_classes;
                    "-classpath"; cp] @ sources);
    copy_resources (path_from project "src/test/resources") test_classes;
    let tests = sources |> List.filter (fun file -> Filename.check_suffix file "Test.java")
      |> List.map (class_name test_sources) in
    if tests <> [] then (
      let junit_classpath = String.concat ":" (test_classes :: cp :: []) in
      run "java" (["-classpath"; junit_classpath; "org.junit.runner.JUnitCore"] @ tests));
    Printf.printf "Compiled %d test sources; ran %d test classes\n%!"
      (List.length sources) (List.length tests)
  in
  let package () =
    test ();
    let output = join target (artifact ^ "-" ^ version ^ ".jar") in
    run "jar" ["--create"; "--file"; output; "-C"; classes; "."];
    Printf.printf "Packaged %s\n%!" output
  in
  List.iter (function
    | "clean" -> remove target; print_endline "Cleaned target"
    | "compile" -> compile ()
    | "test" -> test ()
    | "package" -> package ()
    | _ -> assert false) !goals
