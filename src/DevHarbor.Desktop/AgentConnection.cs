using DevHarbor.AgentBridge;
using DevHarbor.Discovery;
namespace DevHarbor.Desktop;
internal sealed class AgentConnection : IAsyncDisposable
{
    internal AgentHub Hub { get; } = new();
    private AgentPipeServer? server;
    internal void Enable(ScanReport? snapshot)
    {
        if (server != null) return;
        server = new(Hub); Hub.Enable(snapshot);
    }
    internal async Task Disable()
    {
        Hub.Disable(); var current = server; server = null;
        if (current != null) await current.DisposeAsync();
    }
    public async ValueTask DisposeAsync() => await Disable();
}
