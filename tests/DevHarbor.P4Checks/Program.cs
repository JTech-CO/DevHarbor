using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DevHarbor.AgentBridge;
using DevHarbor.Discovery;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static readonly List<string> Protocols = [];
    private static int failures;
    private static readonly List<Process> Clients=[];
    private static string root = "";
    private static string Sdk => File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : "dotnet";
    private static string Server => Path.GetFullPath("src/DevHarbor.Mcp/bin/Release/net10.0-windows/DevHarbor.Mcp.dll");
    [STAThread]
    private static int Main(string[] args)
    {
        root=Path.GetFullPath("artifacts/p4");Directory.CreateDirectory(root);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/DevHarbor.Desktop;component/Theme.xaml",UriKind.Relative)});
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Dispatcher.BeginInvoke(new Action(async()=>
        {
            try { await Run();await RunModels();RunUi();
                if(args.Contains("--observe-local"))
                {
                    using var local=new DevHarbor.Models.OllamaSessions();var state=await local.ObserveAsync();
                    File.WriteAllText(Path.Combine(root,"ollama-local.json"),JsonSerializer.Serialize(new{timeUtc=DateTimeOffset.UtcNow,state.Status,state.Version,loadedCount=state.Models.Count,workingSetMeasured=state.WorkingSetBytes!=null,ramMeasured=state.RamBytes!=null,unloadAttempted=false},new JsonSerializerOptions{WriteIndented=true}));
                    Console.WriteLine($"Local Ollama: {state.Status}, version {state.Version}, {state.Models.Count} loaded; no unload");
                } }
            catch(Exception e){failures++;Console.Error.WriteLine(e);}
            finally
            {
                foreach(var child in Clients){try{child.StandardInput.Close();if(!child.WaitForExit(3000))child.Kill();}finally{child.Dispose();}}
                File.WriteAllText(Path.Combine(root,"latest.json"),JsonSerializer.Serialize(new{timeUtc=DateTimeOffset.UtcNow,os=Environment.OSVersion.VersionString,failures,testResults=Results,protocols=Protocols,liveUnloadPerformed=false,humanUiVerified=false},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"{Results.Count-failures}/{Results.Count} P4 checks passed");app.Shutdown();
            }
        }));
        app.Run();return failures==0?0:1;
    }
    private static ScanReport Snapshot(int count=3)=>new(Enumerable.Range(0,count).Select(i=>new StoreMeasurement(new("store-"+i,ToolKind.Pip,@"C:\Fixture\cache-"+i,"test","fixture"),ScanStatus.Complete,100,200,0,1,0,[],DateTimeOffset.UtcNow)).ToArray(),count*100,count*200,0,false,DateTimeOffset.UtcNow);
    private static AgentRequest Request(string tool,object? arguments=null)=>new(tool,AgentContract.Value(arguments??new{}));
    private static Task Check(string name,Action action)=>Check(name,()=>{action();return Task.CompletedTask;});
    private static async Task Check(string name,Func<Task> action)
    {try{await action();Results.Add(new{name,status="passed"});Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Results.Add(new{name,status="failed",error=e.GetType().Name});Console.Error.WriteLine("FAIL "+name+": "+e);}}
    private static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static void Reject(Action action,string code)
    {try{action();}catch(AgentRequestException e)when(e.Code==code){return;}throw new InvalidOperationException("Expected "+code);}
    private sealed class FakeTime:TimeProvider
    {private DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;internal void Advance(TimeSpan value)=>now+=value;}
    private static async Task<McpClient> Client(string? version=null)
    {
        // SDK 2.2.0's Windows StdioClientTransport invokes cmd /c and breaks spaced executable paths.
        // Launch the server directly; retain the official SDK stream protocol implementation.
        var start=new ProcessStartInfo(Sdk){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(Server);
        var process=Process.Start(start)!;Clients.Add(process);process.BeginErrorReadLine();
        var transport=new StreamClientTransport(process.StandardInput.BaseStream,process.StandardOutput.BaseStream);
        return await McpClient.CreateAsync(transport,new(){ClientInfo=new(){Name="DevHarbor.P4Checks",Version="0.4.0"},ProtocolVersion=version},cancellationToken:new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
    }
    private static async Task<JsonElement> Call(McpClient client,string name,Dictionary<string,object?>? args=null,CancellationToken token=default)
    {
        var result=await client.CallToolAsync(name,args,cancellationToken:token);
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();
    }
    private static async Task Run()
    {
        await Check("sharing-default-off-and-scan-required",()=>
        {
            var hub=new AgentHub();Reject(()=>hub.Handle(Request("devharbor_overview")),"SharingDisabled");hub.Enable(null);
            Reject(()=>hub.Handle(Request("devharbor_overview")),"ScanRequired");hub.Publish(Snapshot());Require(hub.Handle(Request("devharbor_overview")).GetProperty("itemCount").GetInt32()==3,"Snapshot unavailable");hub.Disable();Require(hub.Plans().Count==0,"Plans survived disable");
        });
        await Check("pagination-snapshot-expiry-and-stale-item",()=>
        {
            var clock=new FakeTime();clock.Advance(TimeSpan.FromSeconds(1));var hub=new AgentHub(clock);hub.Enable(Snapshot());
            var page=hub.Handle(Request("devharbor_items",new{limit=1}));string cursor=page.GetProperty("nextCursor").GetString()!;string id=page.GetProperty("items")[0].GetProperty("id").GetString()!;
            Require(hub.Handle(Request("devharbor_items",new{cursor,limit=2})).GetProperty("items").GetArrayLength()==2,"Pagination failed");hub.Publish(Snapshot());
            Reject(()=>hub.Handle(Request("devharbor_items",new{cursor})),"StaleCursor");Reject(()=>hub.Handle(Request("devharbor_plan",new{itemIds=new[]{id},requestId=Guid.NewGuid().ToString("N")})),"StaleItem");
            clock.Advance(TimeSpan.FromMinutes(11));Reject(()=>hub.Handle(Request("devharbor_overview")),"SnapshotExpired");
        });
        await Check("schema-rejects-approval-path-shell-scope-and-invalid-types",()=>
        {
            var hub=new AgentHub();hub.Enable(Snapshot());
            foreach(var extra in new[]{"approved","command","path","scope","level","unload"})
                Reject(()=>hub.Handle(Request("devharbor_plan",new Dictionary<string,object?>{{extra,true}})),"UnexpectedArgument");
            Reject(()=>hub.Handle(Request("devharbor_items",new{limit="50"})),"InvalidLimit");
            Reject(()=>hub.Handle(Request("devharbor_items",new{limit=51})),"InvalidLimit");
            Reject(()=>hub.Handle(Request("devharbor_items",new{cursor="../../"})),"InvalidCursor");
            Reject(()=>hub.Handle(Request("devharbor_approve")),"UnknownTool");
            using var duplicate=JsonDocument.Parse("{\"limit\":1,\"limit\":2}");Reject(()=>hub.Handle(new("devharbor_items",duplicate.RootElement)),"UnexpectedArgument");
        });
        await Check("plan-dedup-conflict-review-expiry-and-no-execution",()=>
        {
            var clock=new FakeTime();clock.Advance(TimeSpan.FromSeconds(1));var hub=new AgentHub(clock);hub.Enable(Snapshot());
            var items=hub.Handle(Request("devharbor_items")).GetProperty("items");string id=items[0].GetProperty("id").GetString()!;string requestId=Guid.NewGuid().ToString("N");
            var request=Request("devharbor_plan",new{itemIds=new[]{id},requestId});var plan=hub.Handle(request);string planId=plan.GetProperty("id").GetString()!;
            Require(plan.GetProperty("status").GetString()=="Blocked"&&hub.Handle(request).GetProperty("id").GetString()==planId&&hub.Plans().Count==1,"Plan was not blocked/idempotent");
            Reject(()=>hub.Handle(Request("devharbor_plan",new{itemIds=new[]{items[1].GetProperty("id").GetString()},requestId})),"RequestConflict");
            hub.MarkReviewed(planId);Require(hub.Handle(Request("devharbor_plan_status",new{planId})).GetProperty("reviewed").GetBoolean(),"Review not visible");
            clock.Advance(TimeSpan.FromMinutes(3));Require(hub.Handle(Request("devharbor_plan_status",new{planId})).GetProperty("status").GetString()=="Expired","Plan not expired");
            hub.Disable();hub.Enable(Snapshot());Reject(()=>hub.Handle(Request("devharbor_plan_status",new{planId})),"PlanNotFound");
        });
        await Check("cancelled-plan-creates-no-record",()=>
        {
            var hub=new AgentHub();hub.Enable(Snapshot());using var cts=new CancellationTokenSource();cts.Cancel();
            try{hub.Handle(Request("devharbor_overview"),cts.Token);throw new InvalidOperationException("Cancellation ignored");}catch(OperationCanceledException){}
            Require(hub.Plans().Count==0,"Cancellation persisted plan");
        });
        foreach(string version in new[]{"2024-11-05","2025-03-26","2025-06-18","2025-11-25","2026-07-28"})
            await Check("sdk-stdio-negotiation-"+version,async()=>
            {
                await using var client=await Client(version);var tools=await client.ListToolsAsync();
                Require(tools.Select(t=>t.Name).Order().SequenceEqual(AgentContract.Names.Order()),"Incorrect tool surface");
                Require(client.NegotiatedProtocolVersion==version,"Wrong negotiated protocol");Protocols.Add(client.NegotiatedProtocolVersion!);
            });
        await Check("stdio-app-absent-and-cancellation-no-auto-start",async()=>
        {
            await using var client=await Client();var result=await Call(client,"devharbor_overview");Require(result.GetProperty("error").GetString()=="AppUnavailableOrTimeout","App absence not explicit");
            using var cts=new CancellationTokenSource(100);
            try{await Call(client,"devharbor_overview",token:cts.Token);throw new InvalidOperationException("Request not cancelled");}catch(OperationCanceledException){}
        });
        await Check("stdio-to-current-user-pipe-overview-items-plan-status",async()=>
        {
            var hub=new AgentHub();hub.Enable(Snapshot());await using var pipe=new AgentPipeServer(hub);await using var client=await Client();
            Require((await Call(client,"devharbor_overview")).GetProperty("cleanupEnabled").GetBoolean()==false,"Execution advertised");
            var page=await Call(client,"devharbor_items",new(){{"limit",1}});var cursor=page.GetProperty("nextCursor").GetString();
            Require((await Call(client,"devharbor_items",new(){{"cursor",cursor},{"limit",2}})).GetProperty("items").GetArrayLength()==2,"Wire pagination failed");
            string id=page.GetProperty("items")[0].GetProperty("id").GetString()!;
            var plan=await Call(client,"devharbor_plan",new(){{"itemIds",new[]{id}},{"requestId",Guid.NewGuid().ToString("N")}});
            Require(plan.GetProperty("status").GetString()=="Blocked","Wire plan executable");string planId=plan.GetProperty("id").GetString()!;
            hub.MarkReviewed(planId);Require((await Call(client,"devharbor_plan_status",new(){{"planId",planId}})).GetProperty("reviewed").GetBoolean(),"Wire status failed");
            try{await Call(client,"devharbor_overview",new(){{"approved",true}});throw new InvalidOperationException("Extra property accepted");}catch(McpProtocolException){}
            hub.Disable();Require((await Call(client,"devharbor_overview")).GetProperty("error").GetString()=="SharingDisabled","Disabled snapshot leaked");
        });
        await Check("raw-pipe-schema-and-frame-limit-enforced",async()=>
        {
            var hub=new AgentHub();hub.Enable(Snapshot());await using var listener=new AgentPipeServer(hub);
            using(var pipe=new System.IO.Pipes.NamedPipeClientStream(".",AgentPipe.Name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly))
            {
                using var timeout=new CancellationTokenSource(5000);await pipe.ConnectAsync(timeout.Token);
                await AgentPipe.WriteAsync(pipe,AgentContract.Value(new{tool="devharbor_overview",arguments=new{},approved=true}),timeout.Token);
                Require((await AgentPipe.ReadAsync(pipe,65536,timeout.Token)).GetProperty("error").GetString()=="InvalidRequest","IPC accepted extra approval");
            }
            using(var pipe=new System.IO.Pipes.NamedPipeClientStream(".",AgentPipe.Name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly))
            {
                using var timeout=new CancellationTokenSource(5000);await pipe.ConnectAsync(timeout.Token);await pipe.WriteAsync(BitConverter.GetBytes(20000),timeout.Token);
                Require((await AgentPipe.ReadAsync(pipe,65536,timeout.Token)).GetProperty("error").GetString()=="MessageTooLarge","IPC frame limit ignored");
            }
            Require(!(await AgentPipe.CallAsync(Request("devharbor_overview"))).TryGetProperty("error",out _),"Invalid input killed listener");Require(hub.Plans().Count==0,"Invalid request created plan");
        });
        await Check("plan-storage-bounded-and-no-silent-scope-expansion",()=>
        {
            var hub=new AgentHub();hub.Enable(Snapshot());string id=hub.Handle(Request("devharbor_items")).GetProperty("items")[0].GetProperty("id").GetString()!;
            for(int i=0;i<100;i++)hub.Handle(Request("devharbor_plan",new{itemIds=new[]{id},requestId=Guid.NewGuid().ToString("N")}));
            Reject(()=>hub.Handle(Request("devharbor_plan",new{itemIds=new[]{id},requestId=Guid.NewGuid().ToString("N")})),"PlanLimit");Require(hub.Plans().Count==100,"Plan bound ignored");
            hub.Disable();hub.Enable(Snapshot());Require(hub.Plans().Count==0,"Sharing reset retained plans");
        });
        await Check("second-app-cannot-replace-current-user-pipe",async()=>
        {
            var hub=new AgentHub();hub.Enable(Snapshot());await using var pipe=new AgentPipeServer(hub);bool rejected=false;
            try{await using var other=new AgentPipeServer(new AgentHub());}catch(IOException){rejected=true;}
            Require(rejected,"Second listener replaced owner");
        });
        await Check("oversized-stdio-input-is-bounded-and-exits",async()=>
        {
            var start=new ProcessStartInfo(Sdk){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(Server);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            try{await process.StandardInput.WriteLineAsync(new string('x',70000));process.StandardInput.Close();}catch(IOException){}
            using var timeout=new CancellationTokenSource(10000);
            try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill();throw;}
            string text=await output;Require(string.IsNullOrWhiteSpace(text)||text.Split('\n',StringSplitOptions.RemoveEmptyEntries).All(line=>IsJson(line)),"Nonprotocol stdout");await error;
        });
    }
    private static bool IsJson(string text){try{using var value=JsonDocument.Parse(text);return value.RootElement.ValueKind==JsonValueKind.Object;}catch(JsonException){return false;}}
}
