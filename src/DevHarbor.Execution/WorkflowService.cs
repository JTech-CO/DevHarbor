using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevHarbor.Ledger;
using DevHarbor.Windows;

namespace DevHarbor.Execution;

// All mutation entry points are internal and restricted to application-created sample files.
internal sealed class WorkflowService : IDisposable
{
    internal ApprovalBroker Broker { get; }
    internal string Root { get; }
    internal string DatabasePath => Path.Combine(Root,"workflow.db");
    private readonly Action<string>? checkpoint;
    private readonly TimeProvider clock;
    internal static WorkflowService OpenDesktop() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DevHarbor","Workflow"));
    internal WorkflowService(string root,TimeProvider? time=null,Action<string>? testCheckpoint=null)
    {
        Root=Path.GetFullPath(root); clock=time??TimeProvider.System; checkpoint=testCheckpoint; Broker=new(clock);
        bool fresh=!Directory.Exists(Root);
        using var creation=CreateStateDirectory(Root);
        using var lease=BoundaryLease.Directory(Root,default);
        if(fresh)
        {
            var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false);
            var sid=WindowsIdentity.GetCurrent().User!;
            security.SetOwner(sid);
            security.AddAccessRule(new(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(Root).SetAccessControl(security);
        }
        using var guard=Lock(); using var db=OpenLedger(initialize:true);
    }
    internal CleanupPlan CreateSamplePlan()
    {
        using var guard=Lock(); using var lease=BoundaryLease.Directory(Root,default); using var db=OpenLedger();
        string samples=Path.Combine(Root,"samples");Directory.CreateDirectory(samples);
        using var samplesLease=BoundaryLease.Directory(samples,default);
        string basis=Path.Combine(samples,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(basis);
        using var basisLease=BoundaryLease.Directory(basis,default);
        string input=Path.Combine(basis,"input"),store=Path.Combine(basis,"held");
        Directory.CreateDirectory(input);Directory.CreateDirectory(store);
        using var inputLease=BoundaryLease.Directory(input,default);
        using var storeLease=BoundaryLease.Directory(store,default);
        string file=Path.Combine(input,"sample.txt");
        using(var writer=new StreamWriter(new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)))
            writer.Write("DevHarbor synthetic approval sample\n"+Guid.NewGuid().ToString("N"));
        var snapshot=WindowsBoundary.Inspect(input,file);
        var plan=MakePlan(WorkflowAction.HoldSample,snapshot,store,null,null,null);
        db.Prepare(plan.Id,plan.Digest,plan.Payload);return plan;
    }
    internal CleanupPlan CreateRestorePlan(string operationId,string? alternateLeaf=null)
    {
        using var guard=Lock(); using var lease=BoundaryLease.Directory(Root,default); using var db=OpenLedger();
        var operation=db.Operations().SingleOrDefault(o=>o.Id==operationId&&o.Action==nameof(WorkflowAction.HoldSample)&&o.State=="Held")
            ??throw new WorkflowException("RestoreUnavailable");
        if(alternateLeaf!=null) BoundaryPath.ValidateLeaf(alternateLeaf);
        var receipt=(JsonSerializer.Deserialize<QuarantineReceipt>(operation.Receipt)??throw new WorkflowException("ReceiptMissing"));ValidateReceipt(receipt);
        var plan=MakePlan(WorkflowAction.RestoreSample,receipt.Original,receipt.Store,receipt,operation.Id,alternateLeaf);
        db.Prepare(plan.Id,plan.Digest,plan.Payload);return plan;
    }
    private CleanupPlan MakePlan(WorkflowAction action,FileSnapshot snapshot,string store,QuarantineReceipt? receipt,string? parent,string? leaf)
    {
        var now=clock.GetUtcNow();return new(new(Guid.NewGuid().ToString("N"),action,Broker.UserSid,Broker.SessionId,Broker.Session,snapshot,store,receipt,parent,leaf,now,now.AddMinutes(2)));
    }
    internal WorkflowResult Execute(CleanupPlan plan,ApprovalGrant grant,CancellationToken token=default)
    {
        try
        {
            using var guard=Lock(); using var stateLease=BoundaryLease.Directory(Root,token);using var db=OpenLedger();
            token.ThrowIfCancellationRequested();
            ValidateScope(plan.Data.Snapshot,plan.Data.Store);
            if(plan.Data.Action==WorkflowAction.RestoreSample) ValidateReceipt(plan.Data.Receipt??throw new WorkflowException("ReceiptMissing"));
            if(db.Operations().Any(o=>o.State=="Intent")) return new("Blocked","RecoveryRequired");
            if(!db.IsPrepared(plan.Id,plan.Digest,plan.Payload)) return new("Blocked","PlanUnavailable");
            var ticket=Broker.Consume(plan,grant);checkpoint?.Invoke("approved");
            bool intent=false;
            void Persist(QuarantineReceipt receipt,string destination)
            {
                Broker.CheckTicket(ticket);token.ThrowIfCancellationRequested();ValidateReceipt(receipt);
                string key=$"{Path.GetPathRoot(receipt.Original.Path)!.ToUpperInvariant()}:{receipt.Original.Stamp.Identity.Volume:X8}:{receipt.Original.Stamp.Identity.FileId:X16}";
                checkpoint?.Invoke("before-intent");
                db.BeginIntent(plan.Id,plan.Digest,plan.Payload,plan.Data.Action.ToString(),JsonSerializer.Serialize(receipt),destination,plan.Data.ParentId,key,Now());
                intent=true;checkpoint?.Invoke("intent");
                Broker.CheckTicket(ticket);token.ThrowIfCancellationRequested();
            }
            try
            {
                MoveOutcome result;
                if(plan.Data.Action==WorkflowAction.HoldSample)
                    result=HandleQuarantine.Stage(plan.Data.Snapshot,plan.Data.Store,token,persistIntent:r=>Persist(r,r.StoredPath));
                else if(plan.Data.Action==WorkflowAction.RestoreSample)
                    result=HandleQuarantine.Restore(plan.Data.Receipt!,plan.Data.RestoreLeaf,token,persistIntent:destination=>Persist(plan.Data.Receipt!,destination));
                else throw new WorkflowException("UnsupportedAction");
                if(result.Completed) checkpoint?.Invoke("moved");
                string state=result.Completed?(plan.Data.Action==WorkflowAction.HoldSample?"Held":"Restored"):"NotApplied";
                if(intent)
                {
                    checkpoint?.Invoke("before-result");
                    db.Finish(plan.Id,state,result.Error?.ToString(),Now());checkpoint?.Invoke("recorded");
                }
                else db.Reject(plan.Id,result.Error?.ToString()??"NotApplied",Now());
                return new(state,result.Error?.ToString(),intent?plan.Id:null);
            }
            catch(Exception e) when(IsExpected(e))
            {
                // Never claim 'not applied' after a durable intent: recover from observed identities.
                return new(intent?"RecoveryRequired":"Blocked",Reason(e),intent?plan.Id:null);
            }
        }
        catch(Exception e) when(IsExpected(e)){return new("Blocked",Reason(e));}
    }
    internal IReadOnlyList<HistoryEntry> Reconcile()
    {
        using var guard=Lock();using var lease=BoundaryLease.Directory(Root,default);using var db=OpenLedger();
        foreach(var original in db.Operations())
        {
            var operation=db.Operations().Single(o=>o.Id==original.Id);
            if(operation.State is not ("Intent" or "Held"))continue;
            string state;
            try
            {
                var receipt=(JsonSerializer.Deserialize<QuarantineReceipt>(operation.Receipt)??throw new WorkflowException("ReceiptMissing"));ValidateReceipt(receipt);
                using var input=BoundaryLease.Directory(receipt.Original.Root,default);
                using var held=BoundaryLease.Directory(receipt.Store,default);
                if(input.DirectoryIdentity!=receipt.Original.AncestorIdentity||held.DirectoryIdentity!=receipt.StoreIdentity)throw new WorkflowException("DirectoryChanged");
                if(operation.Action is not (nameof(WorkflowAction.HoldSample) or nameof(WorkflowAction.RestoreSample)))throw new WorkflowException("UnsupportedAction");
                string source=operation.Action==nameof(WorkflowAction.HoldSample)?receipt.Original.Path:receipt.StoredPath;
                string sourceRoot=operation.Action==nameof(WorkflowAction.HoldSample)?receipt.Original.Root:receipt.Store;
                string destination=operation.Destination;
                string destRoot=operation.Action==nameof(WorkflowAction.HoldSample)?receipt.Store:receipt.Original.Root;
                if(!BoundaryPath.Contains(destRoot,destination)||Path.GetDirectoryName(destination)!=destRoot)throw new WorkflowException("DestinationOutsideSample");
                string a=Probe(sourceRoot,source,receipt.Original.Stamp),b=Probe(destRoot,destination,receipt.Original.Stamp);
                state=operation.State=="Held"?(b=="Exact"?"Held":"NeedsReview")
                    : a=="Absent"&&b=="Exact"?(operation.Action==nameof(WorkflowAction.HoldSample)?"Held":"Restored")
                    : a=="Exact"&&b=="Absent"?"NotApplied":"NeedsReview";
            }
            catch(Exception e) when(IsExpected(e)){state="NeedsReview";}
            if(state!=operation.State)db.Finish(operation.Id,state,state=="NeedsReview"?"AmbiguousFilesystemState":"Reconciled",Now());
        }
        return History(db);
    }
    internal IReadOnlyList<HistoryEntry> ReadHistory()
    {using var guard=Lock();using var lease=BoundaryLease.Directory(Root,default);using var db=OpenLedger();return History(db);}
    private static IReadOnlyList<HistoryEntry> History(SqliteLedger db)=>db.Operations().Select(o=>
    {
        string source="기록 확인 필요";
        try {var receipt=JsonSerializer.Deserialize<QuarantineReceipt>(o.Receipt);if(receipt?.Original!=null)source=o.Action==nameof(WorkflowAction.HoldSample)?receipt.Original.Path:receipt.StoredPath;}catch(JsonException){}
        return new HistoryEntry(o.Id,o.Action,o.State,source,o.Destination,o.Reason,DateTimeOffset.TryParse(o.UpdatedAt,System.Globalization.CultureInfo.InvariantCulture,out var stamp)?stamp:DateTimeOffset.MinValue);
    }).ToArray();
    private static string Probe(string root,string path,FileStamp expected)
    {
        try{return WindowsBoundary.Inspect(root,path).Stamp==expected?"Exact":"Different";}
        catch(BoundaryException e)when(e.NativeError is 2 or 3){return "Absent";}
    }
    private void ValidateReceipt(QuarantineReceipt receipt)
    {if(receipt?.Original?.Stamp?.Identity==null)throw new WorkflowException("InvalidReceipt");ValidateScope(receipt.Original,receipt.Store);if(!Regex.IsMatch(receipt.StoredName,@"^[a-f0-9]{32}\.quarantine$"))throw new WorkflowException("InvalidReceipt");}
    private void ValidateScope(FileSnapshot snapshot,string store)
    {
        string? sample=Path.GetDirectoryName(snapshot.Root);string? id=sample==null?null:Path.GetFileName(sample);
        if(id==null||!Regex.IsMatch(id,@"^[a-f0-9]{32}$"))throw new WorkflowException("OutsideManagedSample");
        string basis=Path.Combine(Root,"samples",id);
        if(!string.Equals(snapshot.Root,Path.Combine(basis,"input"),StringComparison.OrdinalIgnoreCase)
            ||!string.Equals(snapshot.Path,Path.Combine(basis,"input","sample.txt"),StringComparison.OrdinalIgnoreCase)
            ||!string.Equals(store,Path.Combine(basis,"held"),StringComparison.OrdinalIgnoreCase))throw new WorkflowException("OutsideManagedSample");
    }
    private sealed class CreationLeases : IDisposable
    {
        internal List<BoundaryLease> Items { get; } = [];
        public void Dispose() { for(int i=Items.Count-1;i>=0;i--)Items[i].Dispose(); }
    }
    private static CreationLeases CreateStateDirectory(string root)
    {
        var leases=new CreationLeases();
        try
        {
            var missing=new Stack<string>();string ancestor=root;
            while(!Directory.Exists(ancestor))
            { missing.Push(ancestor);ancestor=Path.GetDirectoryName(ancestor)??throw new WorkflowException("InvalidStateRoot"); }
            leases.Items.Add(BoundaryLease.Directory(ancestor,default));
            while(missing.TryPop(out string? path))
            { Directory.CreateDirectory(path);leases.Items.Add(BoundaryLease.Directory(path,default)); }
            return leases;
        }
        catch { leases.Dispose();throw; }
    }
    private SqliteLedger OpenLedger(bool initialize=false)
    {
        // Reject existing aliases before SQLite opens its files. This is not a hostile same-user sandbox.
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                var item = WindowsBoundary.ReadMetadata(Root, DatabasePath + suffix);
                if (item.IsDirectory || item.LinkCount != 1) throw new WorkflowException("InvalidLedgerFile");
            }
            catch (BoundaryException e) when (e.NativeError is 2 or 3) { }
        }
        // Missing journals with retained samples must never silently reset recovery history.
        if(!File.Exists(DatabasePath)&&(!initialize||Directory.Exists(Path.Combine(Root,"samples"))))
            throw new WorkflowException("LedgerMissing");
        return new SqliteLedger(DatabasePath,initialize);
    }
    private string Now()=>clock.GetUtcNow().ToString("O");
    private ProcessLock Lock()=>new(Root);
    private static bool IsExpected(Exception e)=>e is IOException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException;
    private static string Reason(Exception e)=>e switch{WorkflowException w=>w.Reason,BoundaryException b=>b.Reason.ToString(),OperationCanceledException=>"Cancelled",Microsoft.Data.Sqlite.SqliteException=>"LedgerUnavailable",_=>"StateUnavailable"};
    public void Dispose()=>Broker.Dispose();
    private sealed class ProcessLock:IDisposable
    {
        private readonly Mutex mutex;
        internal ProcessLock(string root)
        {
            string key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value+root.ToUpperInvariant())));
            mutex=new(false,@"Global\DevHarbor.Workflow."+key);
            bool acquired;try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired){mutex.Dispose();throw new WorkflowException("ConcurrentOperation");}
        }
        public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
    }
}
