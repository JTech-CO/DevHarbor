using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DevHarbor.Models;
internal static partial class Program
{
    private static readonly string Digest=new('a',64);
    private sealed class OllamaFixture : HttpMessageHandler
    {
        internal string Version=OllamaSessions.VerifiedVersion, ModelDigest=Digest, Name="fixture:latest";
        internal bool Loaded=true, FailPost, KeepLoaded, Malformed, TooLarge, Slow, NullVram, Redirect;
        internal int Posts, Requests;
        internal string? Body;
        internal Func<Task>? OnPost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;Require(request.RequestUri!.Host=="127.0.0.1"&&request.RequestUri.Port==11434,"Remote request");
            if(Slow)await Task.Delay(Timeout.Infinite,token);
            if(Redirect)return new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://example.com/")},Content=new StringContent("{}")};
            if(request.Method==HttpMethod.Post)
            {
                Posts++;Body=await request.Content!.ReadAsStringAsync(token);if(OnPost!=null)await OnPost();
                if(FailPost)throw new HttpRequestException("fixture response lost");if(!KeepLoaded)Loaded=false;
                return Json(new{model="fixture:latest",done=true,done_reason="unload"});
            }
            if(request.RequestUri.AbsolutePath=="/api/version")return Json(new{version=Version});
            if(TooLarge)return new(HttpStatusCode.OK){Content=new StringContent(new string('x',70000))};
            if(Malformed)return new(HttpStatusCode.OK){Content=new StringContent("{not-json}")};
            return Json(new{models=Loaded?new[]{new{name=Name,digest=ModelDigest,size=2048,size_vram=NullVram?(long?)null:1024,expires_at=DateTimeOffset.UtcNow.AddMinutes(5)}}:[]});
        }
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    }
    private static async Task RunModels()
    {
        await Check("ollama-metrics-not-conflated-and-missing-vram-not-zero",async()=>
        {
            var fixture=new OllamaFixture{NullVram=true};using var session=new OllamaSessions(fixture);var state=await session.ObserveAsync();
            Require(state.Status=="Ready"&&state.Models.Single().VramBytes==null&&state.RamBytes==null&&state.Models.Single().ModelBytes==2048&&fixture.Posts==0,"Misrepresented metrics");
        });
        await Check("ollama-remote-custom-endpoint-never-contacted",async()=>
        {
            foreach(var host in new[]{"https://ollama.com","http://192.168.1.2:11434","http://localhost:1234","http://user:secret@127.0.0.1:11434"})
            {var fixture=new OllamaFixture();using var session=new OllamaSessions(fixture,host);Require((await session.ObserveAsync()).Status=="RemoteOrCustomEndpointExcluded"&&fixture.Requests==0,"Endpoint bypass");}
        });
        await Check("ollama-unknown-version-readonly-and-invalid-responses",async()=>
        {
            var fixture=new OllamaFixture{Version="99.0.0"};using var session=new OllamaSessions(fixture);var state=await session.ObserveAsync();Require(state.Status=="ReadOnlyVersion","Unknown version enabled");
            try{await session.PrepareUnloadAsync(state.Models[0]);throw new InvalidOperationException("Unknown version authorized");}catch(ModelSessionException){}
            fixture.Version=OllamaSessions.VerifiedVersion;fixture.Malformed=true;Require((await session.ObserveAsync()).Status=="Unavailable","Malformed response accepted");
            fixture.Malformed=false;fixture.TooLarge=true;Require((await session.ObserveAsync()).Status=="ResponseTooLarge","Unbounded response");
            fixture.TooLarge=false;fixture.Redirect=true;Require((await session.ObserveAsync()).Status=="Http302","Redirect accepted");Require(fixture.Posts==0,"Read produced mutation");
        });
        await Check("ollama-explicit-grant-exact-payload-and-replay-rejected",async()=>
        {
            var fixture=new OllamaFixture();using var session=new OllamaSessions(fixture);var state=await session.ObserveAsync();var plan=await session.PrepareUnloadAsync(state.Models[0]);
            var grant=session.ConfirmFromDesktop(plan);var result=await session.UnloadAsync(plan,grant);Require(result.Status=="Unloaded"&&fixture.Posts==1,"Unload not confirmed");
            using var body=JsonDocument.Parse(fixture.Body!);Require(body.RootElement.EnumerateObject().Count()==3&&body.RootElement.GetProperty("keep_alive").GetInt32()==0&&!body.RootElement.GetProperty("stream").GetBoolean()&&body.RootElement.GetProperty("model").GetString()=="fixture:latest:local","Arbitrary payload");
            Require((await session.UnloadAsync(plan,grant)).Status=="Blocked"&&fixture.Posts==1,"Replay sent POST");
        });
        await Check("ollama-source-qualified-names-never-post",async()=>
        {
            foreach(var name in new[]{"fixture:cloud","fixture:LOCAL","fixture:8b-cloud"})
            {
                var fixture=new OllamaFixture{Name=name};using var session=new OllamaSessions(fixture);
                var model=(await session.ObserveAsync()).Models[0];
                try{await session.PrepareUnloadAsync(model);throw new InvalidOperationException("Ambiguous source accepted");}
                catch(ModelSessionException e){Require(e.Code=="AmbiguousModelSource","Unexpected reason");}
                Require(fixture.Posts==0,"Ambiguous model posted");
            }
        });
        await Check("ollama-forged-grant-and-digest-change-never-post",async()=>
        {
            var fixture=new OllamaFixture();using var session=new OllamaSessions(fixture);var model=(await session.ObserveAsync()).Models[0];var plan=await session.PrepareUnloadAsync(model);
            Require((await session.UnloadAsync(plan,new UnloadGrant(plan))).Status=="Blocked"&&fixture.Posts==0,"Forged grant accepted");
            plan=await session.PrepareUnloadAsync(model);var grant=session.ConfirmFromDesktop(plan);fixture.ModelDigest=new string('b',64);
            Require((await session.UnloadAsync(plan,grant)).Reason=="ModelChanged"&&fixture.Posts==0,"Changed digest accepted");
        });
        await Check("ollama-expiry-cancel-and-closed-session-never-post",async()=>
        {
            var clock=new FakeTime();var fixture=new OllamaFixture();using var session=new OllamaSessions(fixture,clock:clock);var model=(await session.ObserveAsync()).Models[0];var plan=await session.PrepareUnloadAsync(model);var grant=session.ConfirmFromDesktop(plan);
            clock.Advance(TimeSpan.FromSeconds(31));Require((await session.UnloadAsync(plan,grant)).Reason=="Expired","Plan never expired");
            plan=await session.PrepareUnloadAsync(model);grant=session.ConfirmFromDesktop(plan);using var cts=new CancellationTokenSource();cts.Cancel();
            try{await session.UnloadAsync(plan,grant,cts.Token);throw new InvalidOperationException("Cancel ignored");}catch(OperationCanceledException){}
            session.Dispose();Require((await session.UnloadAsync(plan,grant)).Status=="Blocked"&&fixture.Posts==0,"Closed session posted");
        });
        await Check("ollama-lost-response-is-unknown-no-retry",async()=>
        {
            var fixture=new OllamaFixture{FailPost=true};using var session=new OllamaSessions(fixture);var plan=await session.PrepareUnloadAsync((await session.ObserveAsync()).Models[0]);var grant=session.ConfirmFromDesktop(plan);
            Require((await session.UnloadAsync(plan,grant)).Status=="OutcomeUnknown"&&fixture.Posts==1,"Lost response misreported");Require((await session.UnloadAsync(plan,grant)).Status=="Blocked"&&fixture.Posts==1,"Implicit retry");
        });
        await Check("ollama-already-absent-and-reloaded-outcome",async()=>
        {
            var fixture=new OllamaFixture();using var session=new OllamaSessions(fixture);var model=(await session.ObserveAsync()).Models[0];var plan=await session.PrepareUnloadAsync(model);fixture.Loaded=false;
            Require((await session.UnloadAsync(plan,session.ConfirmFromDesktop(plan))).Status=="AlreadyAbsent"&&fixture.Posts==0,"Absent model posted");
            fixture.Loaded=true;fixture.KeepLoaded=true;plan=await session.PrepareUnloadAsync(model);
            Require((await session.UnloadAsync(plan,session.ConfirmFromDesktop(plan))).Status=="StillLoaded","Reloaded model claimed absent");
        });
        await Check("ollama-concurrent-unload-is-blocked",async()=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fixture=new OllamaFixture{OnPost=()=>{entered.SetResult();return release.Task;}};using var session=new OllamaSessions(fixture);var model=(await session.ObserveAsync()).Models[0];var a=await session.PrepareUnloadAsync(model);var b=await session.PrepareUnloadAsync(model);
            var first=session.UnloadAsync(a,session.ConfirmFromDesktop(a));await entered.Task;
            try{Require((await session.UnloadAsync(b,session.ConfirmFromDesktop(b))).Reason=="Busy","Concurrent POST accepted");}finally{release.SetResult();}
            Require((await first).Status=="Unloaded"&&fixture.Posts==1,"Duplicate mutation");
        });
        await Check("ollama-query-cancellation-propagates",async()=>
        {
            var fixture=new OllamaFixture{Slow=true};using var session=new OllamaSessions(fixture);using var cts=new CancellationTokenSource(50);
            try{await session.ObserveAsync(cts.Token);throw new InvalidOperationException("Query not cancelled");}catch(OperationCanceledException){}Require(fixture.Posts==0,"Cancelled GET mutated");
        });
    }
}
