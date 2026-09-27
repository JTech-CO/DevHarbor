using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevHarbor.Discovery;
using DevHarbor.Execution;
using DevHarbor.Ledger;
using DevHarbor.Windows;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static readonly List<object> Results=[];
    private static int failures;
    private static string run="";
    private static string mappedObservation="";
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==4&&args[0]=="--crash")return CrashWorker(args[1],args[2],args[3]);
        run=Path.GetFullPath(Path.Combine("artifacts","p3",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(run);
        Check("approved-sample-hold-and-approved-restore",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();string original=File.ReadAllText(plan.Data.Snapshot.Path);
            var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));Require(result.State=="Held",$"Hold: {result}");
            Require(!File.Exists(plan.Data.Snapshot.Path),"Source still present");
            var restore=service.CreateRestorePlan(plan.Id);Require(service.Execute(restore,service.Broker.IssueFromLocalConfirmation(restore)).State=="Restored","Restore failed");
            Require(File.ReadAllText(plan.Data.Snapshot.Path)==original,"Bytes changed");
            Require(service.ReadHistory().All(o=>o.State=="Restored"),"Parent restore not recorded");
        });
        Check("forged-replayed-and-cross-broker-approvals-rejected",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();var grant=service.Broker.IssueFromLocalConfirmation(plan);
            Require(service.Execute(plan,new ApprovalGrant(grant.Id)).State=="Blocked","Forged object accepted");
            Require(File.Exists(plan.Data.Snapshot.Path),"Forgery moved file");
            using var other=new ApprovalBroker();var different=new CleanupPlan(plan.Data with{BrokerSession=other.Session});
            Require(service.Execute(plan,other.IssueFromLocalConfirmation(different)).State=="Blocked","Cross broker accepted");
            grant=service.Broker.IssueFromLocalConfirmation(plan);Require(service.Execute(plan,grant).State=="Held","Control failed");
            Require(service.Execute(plan,grant).State=="Blocked","Replay accepted");
        });
        Check("expired-plan-ticket-and-user-session-rejected",()=>
        {
            var clock=new FakeTime();using var service=Service(clock);var plan=service.CreateSamplePlan();var grant=service.Broker.IssueFromLocalConfirmation(plan);
            clock.Advance(TimeSpan.FromSeconds(31));Require(service.Execute(plan,grant).Reason=="Expired","Ticket did not expire");
            grant=service.Broker.IssueFromLocalConfirmation(plan);clock.Advance(TimeSpan.FromMinutes(3));Require(service.Execute(plan,grant).Reason=="Expired","Plan did not expire");
            var altered=new CleanupPlan(plan.Data with{UserSid="S-1-0-0"});Reject(()=>service.Broker.IssueFromLocalConfirmation(altered),"SessionMismatch");
        });
        Check("altered-plan-and-path-escape-rejected",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();var grant=service.Broker.IssueFromLocalConfirmation(plan);
            var changed=new CleanupPlan(plan.Data with{Store=Path.GetTempPath()});Require(service.Execute(changed,grant).Reason=="OutsideManagedSample","Arbitrary path accepted");
            changed=new CleanupPlan(plan.Data with{ExpiresAt=plan.Data.ExpiresAt.AddHours(2)});Require(service.Execute(changed,grant).Reason=="PlanUnavailable","Changed payload accepted");
            Require(File.Exists(plan.Data.Snapshot.Path),"Plan change moved file");
        });
        Check("target-change-after-approval-is-not-applied",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();var grant=service.Broker.IssueFromLocalConfirmation(plan);File.AppendAllText(plan.Data.Snapshot.Path,"changed");
            var result=service.Execute(plan,grant);Require(result.State=="NotApplied"&&result.Reason=="TargetChanged","Changed bytes accepted");Require(File.Exists(plan.Data.Snapshot.Path),"Changed file moved");
        });
        Check("journal-failure-before-intent-never-moves",()=>
        {
            using var service=Service(checkpoint:phase=>{if(phase=="before-intent")throw new IOException("Injected journal failure");});
            var plan=service.CreateSamplePlan();var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));
            Require(result.State=="Blocked"&&File.Exists(plan.Data.Snapshot.Path)&&!Directory.EnumerateFiles(plan.Data.Store).Any(),"Journal failure moved bytes");
        });
        Check("result-write-failure-leaves-recoverable-intent",()=>
        {
            using var service=Service(checkpoint:phase=>{if(phase=="before-result")throw new IOException("Injected result failure");});
            var plan=service.CreateSamplePlan();var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));
            Require(result.State=="RecoveryRequired"&&!File.Exists(plan.Data.Snapshot.Path),"Commit outcome misreported");
            Require(service.Reconcile().Single().State=="Held","Intent did not reconcile");
        });
        Check("cancel-before-and-after-intent",()=>
        {
            using var cts=new CancellationTokenSource();using var service=Service(checkpoint:phase=>{if(phase=="intent")cts.Cancel();});var plan=service.CreateSamplePlan();
            var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan),cts.Token);
            Require(File.Exists(plan.Data.Snapshot.Path)&&service.Reconcile().Single().State=="NotApplied","Intent cancellation moved bytes");
            plan=service.CreateSamplePlan();result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan),cts.Token);Require(result.Reason=="Cancelled"&&File.Exists(plan.Data.Snapshot.Path),"Pre-cancel ignored");
        });
        Check("cancel-after-rename-still-reports-committed",()=>
        {
            using var cts=new CancellationTokenSource();using var service=Service(checkpoint:p=>{if(p=="moved")cts.Cancel();});var plan=service.CreateSamplePlan();
            Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan),cts.Token).State=="Held","Committed move reported cancelled");
        });
        Check("restore-collision-and-explicit-alternate-approval",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan)).State=="Held","Control failed");
            File.WriteAllText(plan.Data.Snapshot.Path,"NEW ORIGINAL");var restore=service.CreateRestorePlan(plan.Id);
            var result=service.Execute(restore,service.Broker.IssueFromLocalConfirmation(restore));Require(result.Reason=="DestinationExists"&&File.ReadAllText(plan.Data.Snapshot.Path)=="NEW ORIGINAL","Collision overwritten");
            restore=service.CreateRestorePlan(plan.Id,"restored-sample.txt");Require(service.Execute(restore,service.Broker.IssueFromLocalConfirmation(restore)).State=="Restored","Alternate restore failed");
            Require(File.Exists(Path.Combine(plan.Data.Snapshot.Root,"restored-sample.txt"))&&File.ReadAllText(plan.Data.Snapshot.Path)=="NEW ORIGINAL","Alternate affected original");
        });
        Check("concurrent-operation-and-recovery-exclusion",()=>
        {
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            using var service=Service(checkpoint:p=>{if(p=="intent"){entered.Set();if(!release.Wait(10000))throw new TimeoutException();}});
            var plan=service.CreateSamplePlan();var grant=service.Broker.IssueFromLocalConfirmation(plan);var task=Task.Run(()=>service.Execute(plan,grant));
            Require(entered.Wait(10000),"Execution did not reach intent");
            try {Require(service.Execute(plan,grant).Reason=="ConcurrentOperation","Concurrent execution accepted");Reject(()=>service.Reconcile(),"ConcurrentOperation");}
            finally {release.Set();}Require(task.GetAwaiter().GetResult().State=="Held","First operation failed");
        });
        Check("database-corruption-does-not-recreate-or-move",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();byte[] garbage=System.Text.Encoding.UTF8.GetBytes("CORRUPTED DATABASE");File.WriteAllBytes(service.DatabasePath,garbage);
            Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan)).Reason=="LedgerUnavailable","Corrupt DB accepted");
            Require(File.Exists(plan.Data.Snapshot.Path)&&File.ReadAllBytes(service.DatabasePath).SequenceEqual(garbage),"Corruption repaired destructively");
        });
        Check("changed-held-payload-needs-review-no-auto-restore",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan)).State=="Held","Control failed");
            File.AppendAllText(Directory.GetFiles(plan.Data.Store).Single(),"change");
            Require(service.Reconcile().Single().State=="NeedsReview"&&!File.Exists(plan.Data.Snapshot.Path),"Ambiguous payload restored");
        });
        Check("writable-mapping-probe-does-not-open-live-cache-gate",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();
            using var mapping=MemoryMappedFile.CreateFromFile(plan.Data.Snapshot.Path,FileMode.Open,null,0,MemoryMappedFileAccess.ReadWrite);
            using var view=mapping.CreateViewAccessor();var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));
            mappedObservation=result.State+":"+result.Reason;
            if(result.State=="Held") {view.Write(0,(byte)'X');view.Flush();Require(service.Reconcile().Single().State=="NeedsReview","Mapped write was trusted");}
            else Require(File.Exists(plan.Data.Snapshot.Path),"Mapping probe lost original");
            Require(!WindowsCapabilities.CanRecycle&&!WindowsCapabilities.CanPermanentlyDelete,"Live cleanup enabled");
        });
        Check("pip-uv-and-other-stores-remain-blocked-with-reasons",()=>
        {
            foreach(var tool in Enum.GetValues<ToolKind>())
            {
                var item=new StoreMeasurement(new("id",tool,@"C:\fixture","verified-version","fixture"),ScanStatus.Complete,1,1,0,1,0,[],DateTimeOffset.UtcNow);
                var decision=CleanupEligibility.Assess(item);Require(!decision.CanExecute&&decision.Reasons.Count>=3,"Measurement granted cleanup");
            }
        });
        Check("sample-parent-junction-rejected-before-file-creation",()=>
        {
            using var service=Service();string outside=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outside);
            string junction=Path.Combine(service.Root,"samples");
            CreateJunction(junction,outside);
            bool denied=false;try{service.CreateSamplePlan();}catch(BoundaryException e)when(e.Reason==BoundaryError.ReparsePoint){denied=true;}
            Require(denied&&!Directory.EnumerateFileSystemEntries(outside).Any(),"Sample creation escaped through junction");
        });
        Check("state-parent-junction-rejected-before-creating-root",()=>
        {
            string outside=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outside);
            string junction=Path.Combine(run,Guid.NewGuid().ToString("N"));CreateJunction(junction,outside);
            bool denied=false;try{using var service=new WorkflowService(Path.Combine(junction,"workflow"));}catch(BoundaryException e)when(e.Reason==BoundaryError.ReparsePoint){denied=true;}
            Require(denied&&!Directory.EnumerateFileSystemEntries(outside).Any(),"Root creation escaped through junction");
        });
        Check("missing-ledger-with-samples-never-resets-history",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();
            // Same-directory fixture rename preserves the database for inspection; no files are removed.
            File.Move(service.DatabasePath,Path.Combine(service.Root,"preserved.db"));
            Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan)).Reason=="LedgerMissing"&&!File.Exists(service.DatabasePath)&&File.Exists(plan.Source),"Lost ledger recreated");
            Reject(()=>{using var restarted=new WorkflowService(service.Root);},"LedgerMissing");
        });
        Check("approval-expires-after-durable-intent",()=>
        {
            var clock=new FakeTime();using var service=Service(clock,p=>{if(p=="intent")clock.Advance(TimeSpan.FromSeconds(31));});
            var plan=service.CreateSamplePlan();var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));
            Require(result.State=="RecoveryRequired"&&result.Reason=="Expired"&&File.Exists(plan.Source),"Late expiry moved file");
            Require(service.Reconcile().Single().State=="NotApplied","Expired intent not reconciled");
        });
        Check("closed-session-revokes-approval",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();var grant=service.Broker.IssueFromLocalConfirmation(plan);service.Broker.Dispose();
            Require(service.Execute(plan,grant).Reason=="SessionMismatch"&&File.Exists(plan.Source),"Closed broker remained usable");
        });
        Check("unresolved-intent-blocks-new-plan-and-approval",()=>
        {
            using var service=Service(checkpoint:p=>{if(p=="before-result")throw new IOException();});var first=service.CreateSamplePlan();
            Require(service.Execute(first,service.Broker.IssueFromLocalConfirmation(first)).State=="RecoveryRequired","Intent fixture failed");
            var next=service.CreateSamplePlan();Require(service.Execute(next,service.Broker.IssueFromLocalConfirmation(next)).Reason=="RecoveryRequired"&&File.Exists(next.Source),"Pending recovery was bypassed");
        });
        Check("sqlite-write-lock-failure-never-moves",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=service.DatabasePath,Pooling=false}.ToString());connection.Open();
            using var transaction=connection.BeginTransaction();
            var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));
            Require(result.Reason=="LedgerUnavailable"&&File.Exists(plan.Source)&&!Directory.EnumerateFiles(plan.Data.Store).Any(),"SQLite write failure moved file");
        });
        Check("corrupt-receipt-never-traverses-outside-sample",()=>
        {
            using var service=Service();var plan=service.CreateSamplePlan();Require(service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan)).State=="Held","Fixture failed");
            using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=service.DatabasePath,Pooling=false}.ToString()))
            { connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE operations SET receipt='null';";command.ExecuteNonQuery(); }
            Require(service.Reconcile().Single().State=="NeedsReview"&&Directory.GetFiles(plan.Data.Store).Length==1,"Corrupt receipt accepted");
        });
        foreach(string action in new[]{"hold","restore"})foreach(string phase in new[]{"approved","before-intent","intent","moved","before-result","recorded"})
            Check($"crash-{action}-{phase}-fresh-process-reconcile",()=>CrashCase(action,phase));
        RunUiChecks();
        var report=new{timeUtc=DateTimeOffset.UtcNow,os=Environment.OSVersion.VersionString,failures,testResults=Results,mappedObservation,productionCleanupEnabled=false,humanApprovalManuallyVerified=false};
        File.WriteAllText(Path.Combine(run,"results.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText("artifacts/p3/latest.json",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{Results.Count-failures}/{Results.Count} P3 checks passed; mapped probe={mappedObservation}");return failures==0?0:1;
    }
    private static void CreateJunction(string junction,string outside)
    {
        var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '"+junction.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' -ErrorAction Stop | Out-Null");
        using var process=Process.Start(start)!;
        if(!process.WaitForExit(10000)){process.Kill();throw new TimeoutException("Junction fixture setup");}
        Require(process.ExitCode==0,"Junction fixture failed");
    }
    private static WorkflowService Service(TimeProvider? time=null,Action<string>? checkpoint=null)=>new(Path.Combine(run,Guid.NewGuid().ToString("N")),time,checkpoint);
    private static void Check(string name,Action test){try{test();Results.Add(new{name,status="passed"});Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Results.Add(new{name,status="failed",error=e.GetType().Name});Console.Error.WriteLine($"FAIL {name}: {e}");}}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Reject(Action action,string reason){try{action();}catch(WorkflowException e)when(e.Reason==reason){return;}throw new InvalidOperationException("Expected "+reason);}
    private sealed class FakeTime:TimeProvider{private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;internal void Advance(TimeSpan value)=>now+=value;}
    private static int CrashWorker(string root,string action,string phase)
    {
        string allowed=Path.GetFullPath("artifacts/p3")+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(root).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))return 2;
        bool armed=false;using var service=new WorkflowService(root,testCheckpoint:p=>{if(armed&&p==phase){File.WriteAllText(Path.Combine(root,"checkpoint.txt"),p);Environment.Exit(73);}});
        var plan=service.CreateSamplePlan();
        if(action=="restore"){var result=service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));if(result.State!="Held")return 3;plan=service.CreateRestorePlan(plan.Id);}
        File.WriteAllText(Path.Combine(root,"case.json"),JsonSerializer.Serialize(plan.Data));armed=true;
        service.Execute(plan,service.Broker.IssueFromLocalConfirmation(plan));return 4;
    }
    private static void CrashCase(string action,string phase)
    {
        string root=Path.Combine(run,Guid.NewGuid().ToString("N"));var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
        if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach(string value in new[]{"--crash",root,action,phase})psi.ArgumentList.Add(value);
        using(var process=Process.Start(psi)!){if(!process.WaitForExit(30000)){process.Kill();throw new TimeoutException();}Require(process.ExitCode==73,"Worker did not terminate at checkpoint");}
        Require(File.ReadAllText(Path.Combine(root,"checkpoint.txt"))==phase,"Wrong crash phase");
        var plan=JsonSerializer.Deserialize<PlanData>(File.ReadAllText(Path.Combine(root,"case.json")))!;
        using var restarted=new WorkflowService(root);var rows=restarted.Reconcile();var current=rows.SingleOrDefault(o=>o.Id==plan.Id);
        bool committed=phase is "moved" or "before-result" or "recorded";
        if(phase is "approved" or "before-intent")Require(current==null,"Intent existed too early");
        else Require(current?.State==(committed?(action=="hold"?"Held":"Restored"):"NotApplied"),"Reconciliation state wrong");
        bool atOriginal=action=="hold"?!committed:committed;
        Require(File.Exists(plan.Snapshot.Path)==atOriginal,"Recovery automatically moved or lost file");
        var locations=Directory.GetFiles(plan.Store).Concat(File.Exists(plan.Snapshot.Path)?new[]{plan.Snapshot.Path}:[]).ToArray();
        Require(locations.Length==1,"Payload not preserved exactly once");
        Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(locations[0])))==plan.Snapshot.Stamp.Sha256,"Crash changed payload");
    }

}
