using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
namespace DevHarbor.AgentBridge;

public static class AgentPipe
{
    public static string Name => "DevHarbor.P4." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value + ":" + Process.GetCurrentProcess().SessionId)))[..24];
    public static async Task<JsonElement> CallAsync(AgentRequest request, CancellationToken token = default)
    {
        AgentContract.Validate(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            await WriteAsync(pipe, AgentContract.Value(request), deadline.Token);
            return await ReadAsync(pipe, 2 * 1024 * 1024, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return AgentContract.Error("AppUnavailableOrTimeout"); }
        catch (AgentRequestException e) { return AgentContract.Error(e.Code); }
        catch (JsonException) { return AgentContract.Error("InvalidBridgeResponse"); }
        catch (IOException) { return AgentContract.Error("AppUnavailable"); }
        catch (UnauthorizedAccessException) { return AgentContract.Error("AppUnavailable"); }
    }
    internal static async Task<JsonElement> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token); int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 2 || size > maximum) throw new AgentRequestException("MessageTooLarge");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); return json.RootElement.Clone();
    }
    internal static async Task WriteAsync(Stream stream, JsonElement value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value); if (bytes.Length > 2 * 1024 * 1024) throw new AgentRequestException("MessageTooLarge");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}
public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly AgentHub hub;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource stop = new();
    public Task Completion { get; }
    public AgentPipeServer(AgentHub hub)
    {
        this.hub = hub;
        pipe = new(AgentPipe.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        Completion = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                JsonElement answer;
                try
                {
                    var payload = await AgentPipe.ReadAsync(pipe, 16384, deadline.Token);
                    var request = payload.Deserialize<AgentRequest>(AgentContract.Json) ?? throw new AgentRequestException("InvalidRequest");
                    answer = hub.Handle(request, deadline.Token);
                }
                catch (AgentRequestException e) { answer = AgentContract.Error(e.Code); }
                catch (JsonException) { answer = AgentContract.Error("InvalidRequest"); }
                await AgentPipe.WriteAsync(pipe, answer, deadline.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        hub.Disable(); await stop.CancelAsync();
        try { await Completion; } finally { pipe.Dispose(); stop.Dispose(); }
    }
}
