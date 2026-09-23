module Fogell.Retention.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open Fogell.Retention

let mutable database=""
let private sql statement (parameters: (string*obj) list) =
    use connection=new NpgsqlConnection(database)
    connection.Open()
    use cmd=connection.CreateCommand()
    cmd.CommandText<-statement
    cmd.CommandTimeout<-5
    for key,value in parameters do cmd.Parameters.AddWithValue(key,value) |> ignore
    cmd.ExecuteScalar()

let private policy={KeepBuilds=0;MaxBytes=Int64.MaxValue;MaxAgeSeconds=Int64.MaxValue
                    MaxCandidates=100;MaxEntries=100;MaxOperations=100;BudgetSeconds=10}

type Lab() =
    let root=Path.Combine(Path.GetTempPath(),"fogell-retention-test-"+Guid.NewGuid().ToString("N"))
    let org,project=Guid.NewGuid(),Guid.NewGuid()
    let mutable number=0
    do
        Directory.CreateDirectory root |> ignore
        sql "INSERT INTO organizations(id,slug) VALUES(@org,@slug); INSERT INTO projects(id,organization_id,slug) VALUES(@project,@org,'p')" ["org",box org;"project",box project;"slug",box(string org)] |> ignore
    member _.Root=root
    member _.Org=org
    member _.Seed(state: string) =
        number<-number+1
        let build,node,attempt=Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()
        let source=Text.Encoding.UTF8.GetBytes("pipeline { agent any; stages { stage('x') { steps { echo 'retention' } } } }")
        let digest=SHA256.HashData source
        sql "INSERT INTO builds(id,organization_id,project_id,number,idempotency_key,status) VALUES(@b,@o,@p,@num,@key,@status);
          INSERT INTO nodes(id,organization_id,build_id,name,ordinal,required_trust_pool,status) VALUES(@n,@o,@b,'n',0,'trusted-linux',@status);
          INSERT INTO attempts(id,organization_id,node_id,ordinal,state,result) VALUES(@a,@o,@n,0,@state,@result);
          INSERT INTO build_definitions(build_id,organization_id,source_bytes,source_digest,admission_fingerprint) VALUES(@b,@o,@source,@digest,@digest);
          INSERT INTO log_chunks(organization_id,build_id,attempt_id,sequence,build_sequence,body) VALUES(@o,@b,@a,0,0,'first'),(@o,@b,@a,1,1,'second')" ["b",box build;"o",box org;"p",box project;"n",box node;"a",box attempt;"num",box number;
             "key",box(string build);"status",box(if state="terminal" then "success" else state);
             "state",box state;"result",(if state="terminal" then box "success" else box DBNull.Value);
             "source",box source;"digest",box digest] |> ignore
        let home=Text.Encoding.UTF8.GetBytes(build.ToString("N")+"\u0000"+string number) |> SHA256.HashData |> Convert.ToHexStringLower
        for relative in [$"workspaces/{org:N}/{build:N}";$"workspaces/{org:N}/_artifact-snapshots/{attempt:N}"
                         $"workspaces/{org:N}/_agent_home/{home}";$"workspaces/{org:N}/_artifacts/_stash/{build:N}#build-{number}"] do
            let path=Path.Combine(root,relative)
            Directory.CreateDirectory path |> ignore
            File.WriteAllText(Path.Combine(path,"evidence.txt"),String('x',1024))
        let definition=Path.Combine(root,$"definitions/{org:N}/{build:N}")
        Directory.CreateDirectory definition |> ignore
        File.WriteAllBytes(Path.Combine(definition,"Jenkinsfile"),source)
        build,node,attempt
    member _.Sweep(p,cut)=Retention.sweep database root org p cut
    member _.Count(table)=
        sql ($"SELECT count(*) FROM {table} WHERE organization_id=@org") ["org",box org] |> Convert.ToInt32
    interface IDisposable with
        member _.Dispose()=if Directory.Exists root then Directory.Delete(root,true)

let private stopAt target point = if target=point then raise(OperationCanceledException("proof_cutpoint"))
let private cut (lab: Lab) point = Expect.throwsT<OperationCanceledException> (fun ()->lab.Sweep(policy,stopAt point) |> ignore) point

let private complete (lab: Lab) p =
    let mutable result=lab.Sweep(p,ignore)
    let mutable sweeps=1
    while result.Pending>0 && sweeps<40 do
        result<-lab.Sweep(p,ignore)
        sweeps<-sweeps+1
    Expect.equal result.Held 0 "no unexplained holds"
    Expect.equal result.Pending 0 "declared 40-sweep recovery bound"
    result,sweeps

let tests = testSequenced <| testList "retention" [
    test "bounded count policy restores target and expires source/log payload" {
        use lab=new Lab()
        for _ in 1..5 do lab.Seed "terminal" |> ignore
        let result,sweeps=complete lab {policy with KeepBuilds=1;MaxOperations=2}
        Expect.equal (lab.Count "build_definitions") 1 "one source envelope remains"
        Expect.equal (lab.Count "log_chunks") 2 "one build's output remains"
        Expect.equal (lab.Count "build_retention") 4 "identities have durable tombstones"
        Expect.equal (Directory.GetDirectories(Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/_agent_home")).Length) 1 "one retained build HOME"
        Expect.equal (Directory.GetDirectories(Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/_artifacts/_stash")).Length) 1 "one retained build stash"
        Expect.isLessThanOrEqual result.Operations 2 "per-sweep operation bound"
        Expect.isLessThanOrEqual sweeps 40 "capacity recovers under declared bound"
    }
    test "age and size policies independently select terminal history" {
        for p in [{policy with KeepBuilds=100;MaxAgeSeconds=0L};{policy with KeepBuilds=100;MaxBytes=1L}] do
            use lab=new Lab()
            lab.Seed "terminal" |> ignore
            complete lab p |> ignore
            Expect.equal (lab.Count "build_definitions") 0 "payload expired"
    }
    test "active and reconciliation evidence survives" {
        use lab=new Lab()
        lab.Seed "queued" |> ignore
        lab.Seed "running" |> ignore
        lab.Seed "reconciliation_required" |> ignore
        lab.Seed "terminal" |> ignore
        complete lab policy |> ignore
        Expect.equal (lab.Count "build_retention") 1 "only safe terminal selected"
        Expect.equal (lab.Count "build_definitions") 3 "protected source evidence survives"
    }
    test "retry ancestry protects the entire build" {
        use lab=new Lab()
        let _,node,parent=lab.Seed "terminal"
        sql "INSERT INTO attempts(id,organization_id,node_id,ordinal,state,result,retry_of) VALUES(@id,@o,@n,1,'terminal','success',@parent)" ["id",box(Guid.NewGuid());"o",box lab.Org;"n",box node;"parent",box parent] |> ignore
        complete lab policy |> ignore
        Expect.equal (lab.Count "build_retention") 0 "retry lineage held"
        Expect.equal (lab.Count "build_definitions") 1 "parent payload retained"
    }
    for point in ["selected";"deleting";"intent";"quarantined";"unlinked";"filesystem";"logs"] do
        testCase ("resume interruption at "+point) <| fun () ->
            use lab=new Lab()
            lab.Seed "terminal" |> ignore
            cut lab point
            complete lab policy |> ignore
            Expect.equal (lab.Count "build_definitions") 0 "resume completed payload expiry"
            Expect.equal (lab.Count "log_chunks") 0 "logs drained"
    test "concurrent sweeper refuses while first owns tenant lock" {
        use lab=new Lab()
        lab.Seed "terminal" |> ignore
        use entered=new ManualResetEventSlim(false)
        use release=new ManualResetEventSlim(false)
        let first=Task.Run(fun ()->lab.Sweep(policy,fun point->
            if point="selected" then
                entered.Set()
                if not(release.Wait 10000) then failwith "test_release_deadline"))
        try
            Expect.isTrue (entered.Wait 10000) "first sweep entered"
            Expect.throwsC (fun ()->lab.Sweep(policy,ignore) |> ignore)
                (fun e->Expect.stringContains e.Message "retention_sweep_busy" "second refuses")
        finally release.Set()
        Expect.isTrue (first.Wait 10000) "first sweep bounded completion"
        Expect.equal first.Result.Held 0 "winner completed safely"
    }
    test "selected build refuses new log and retry writes" {
        use lab=new Lab()
        let build,node,attempt=lab.Seed "terminal"
        cut lab "selected"
        Expect.throws(fun ()->sql "INSERT INTO log_chunks(organization_id,build_id,attempt_id,sequence,build_sequence,body) VALUES(@o,@b,@a,2,2,'late')" ["o",box lab.Org;"b",box build;"a",box attempt] |> ignore) "output cannot revive selected evidence"
        Expect.throws(fun ()->sql "INSERT INTO attempts(id,organization_id,node_id,ordinal,state,retry_of) VALUES(@id,@o,@n,1,'queued',@a)" ["id",box(Guid.NewGuid());"o",box lab.Org;"n",box node;"a",box attempt] |> ignore) "retry cannot consume selected parent"
    }
    test "changed entry identity holds and preserves replacement" {
        use lab=new Lab()
        let build,_,_=lab.Seed "terminal"
        cut lab "selected"
        let path=Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/{build:N}/evidence.txt")
        File.Move(path,path+".saved")
        File.WriteAllText(path,"replacement must survive")
        let result=lab.Sweep(policy,ignore)
        Expect.equal result.Held 1 "identity mismatch holds"
        Expect.equal (File.ReadAllText path) "replacement must survive" "known-bad cleaner rejected"
        Expect.equal (lab.Count "log_chunks") 2 "DB payload retained on disagreement"
    }
    test "replaced ancestor holds and preserves new tree" {
        use lab=new Lab()
        lab.Seed "terminal" |> ignore
        cut lab "selected"
        let path=Path.Combine(lab.Root,$"workspaces/{lab.Org:N}")
        Directory.Move(path,path+"-saved")
        Directory.CreateDirectory path |> ignore
        File.WriteAllText(Path.Combine(path,"sentinel"),"keep")
        let result=lab.Sweep(policy,ignore)
        Expect.equal result.Held 1 "ancestor replacement holds"
        Expect.equal (File.ReadAllText(Path.Combine(path,"sentinel"))) "keep" "new tree survives"
    }
    test "unexpected missing entry holds before deletion intent" {
        use lab=new Lab()
        let build,_,_=lab.Seed "terminal"
        cut lab "selected"
        File.Delete(Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/{build:N}/evidence.txt"))
        Expect.equal (lab.Sweep(policy,ignore)).Held 1 "unexplained missing file is not a successful unlink"
    }
    test "database definition disagreement refuses selection" {
        use lab=new Lab()
        let build,_,_=lab.Seed "terminal"
        File.WriteAllText(Path.Combine(lab.Root,$"definitions/{lab.Org:N}/{build:N}/Jenkinsfile"),"substituted source")
        Expect.throws(fun ()->lab.Sweep(policy,ignore) |> ignore) "wrong paired filesystem rejected"
        Expect.equal (lab.Count "build_retention") 0 "no deletion authorized"
        Expect.equal (lab.Count "log_chunks") 2 "evidence retained"
    }
    test "symlink child is unlinked without following target" {
        use lab=new Lab()
        let build,_,_=lab.Seed "terminal"
        let outside=Path.Combine(lab.Root,"protected")
        File.WriteAllText(outside,"outside survives")
        File.CreateSymbolicLink(Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/{build:N}/link"),outside) |> ignore
        complete lab policy |> ignore
        Expect.equal (File.ReadAllText outside) "outside survives" "link never followed"
    }
    test "inventory bounds refuse before committing deletion" {
        use lab=new Lab()
        lab.Seed "terminal" |> ignore
        Expect.throws(fun ()->lab.Sweep({policy with MaxEntries=1},ignore) |> ignore) "oversized tree refused"
        Expect.equal (lab.Count "build_retention") 0 "no partial plan"
    }
    test "recreated deleted root holds before evidence expiry" {
        use lab=new Lab()
        let build,_,_=lab.Seed "terminal"
        let path=Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/{build:N}")
        let result=lab.Sweep(policy,fun point->
            if point="filesystem" then
                Directory.CreateDirectory path |> ignore
                File.WriteAllText(Path.Combine(path,"replacement"),"keep"))
        Expect.equal result.Held 1 "new root is database/filesystem disagreement"
        Expect.equal (lab.Count "log_chunks") 2 "payload delete rolls back"
        Expect.equal (File.ReadAllText(Path.Combine(path,"replacement"))) "keep" "new evidence preserved"
    }
    test "changed state root identity holds" {
        use lab=new Lab()
        lab.Seed "terminal" |> ignore
        cut lab "selected"
        let backup=lab.Root+"-original"
        Directory.Move(lab.Root,backup)
        try
            Directory.CreateDirectory lab.Root |> ignore
            File.WriteAllText(Path.Combine(lab.Root,"sentinel"),"keep")
            Expect.equal (lab.Sweep(policy,ignore)).Held 1 "root replacement held"
            Expect.equal (File.ReadAllText(Path.Combine(lab.Root,"sentinel"))) "keep" "replacement survives"
        finally Directory.Delete(backup,true)
    }
    test "invalid CLI switches cannot silently become destructive defaults" {
        use lab=new Lab()
        lab.Seed "terminal" |> ignore
        let previous=Environment.GetEnvironmentVariable "FOGELL_MAINTENANCE_DATABASE_URL"
        Environment.SetEnvironmentVariable("FOGELL_MAINTENANCE_DATABASE_URL",database)
        try
            let common=[|"--state-root";lab.Root;"--organization";string lab.Org;"--keep-builds";"0"|]
            for bad in [[|"--dry-run";"true"|];[|"--keep-builds";"10"|];[|"--max-operations";"4294967297"|]] do
                Expect.equal (Fogell.Retention.Program.main(Array.append common bad)) 1 "unsafe invocation refused"
            Expect.equal (lab.Count "build_retention") 0 "no silent deletion"
            Expect.equal (lab.Count "build_definitions") 1 "source preserved"
        finally Environment.SetEnvironmentVariable("FOGELL_MAINTENANCE_DATABASE_URL",previous)
    }
    test "known-bad cleaner is rejected by the preservation checker" {
        use lab=new Lab()
        let build,_,_=lab.Seed "queued"
        let protectedPath=Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/{build:N}/evidence.txt")
        let check()=
            let homes=Directory.GetFiles(Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/_agent_home"),"evidence.txt",SearchOption.AllDirectories)
            let stashes=Directory.GetFiles(Path.Combine(lab.Root,$"workspaces/{lab.Org:N}/_artifacts/_stash"),"evidence.txt",SearchOption.AllDirectories)
            if not(File.Exists protectedPath) || homes.Length<>1 || stashes.Length<>1 || lab.Count "log_chunks"<>2 then failwith "protected_evidence_lost"
        lab.Sweep(policy,ignore) |> ignore
        check()
        // Deliberately broken cleaner confined to this disposable test's data.
        File.Delete protectedPath
        Expect.throwsC check (fun error->Expect.equal error.Message "protected_evidence_lost" "false cleanup success refused")
    }
]

[<EntryPoint>]
let main args =
    let admin=Environment.GetEnvironmentVariable "FOGELL_TEST_DATABASE_URL"
    if String.IsNullOrWhiteSpace admin then
        eprintfn "FOGELL_TEST_DATABASE_URL is required for real retention tests"
        1
    else
        let name="fogell_retention_"+Guid.NewGuid().ToString("N")
        use connection=new NpgsqlConnection(admin)
        connection.Open()
        use create=connection.CreateCommand()
        create.CommandText<- $"CREATE DATABASE {name}"
        create.ExecuteNonQuery() |> ignore
        let builder=NpgsqlConnectionStringBuilder admin
        builder.Database<-name
        database<-builder.ConnectionString
        try
            match Fogell.Store.Migrations.run database with
            | Error error -> eprintfn "migration failed: %s" error;1
            | Ok _ -> runTestsWithCLIArgs [] args tests
        finally
            NpgsqlConnection.ClearAllPools()
            use drop=connection.CreateCommand()
            drop.CommandText<- $"DROP DATABASE {name} WITH(FORCE)"
            drop.CommandTimeout<-10
            drop.ExecuteNonQuery() |> ignore
