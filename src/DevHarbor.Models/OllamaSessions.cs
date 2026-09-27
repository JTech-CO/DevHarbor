using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace DevHarbor.Models;

public sealed record LoadedModel(string Name, string Digest, long ModelBytes, long? VramBytes, DateTimeOffset ExpiresAt);
public sealed record ModelObservation(string Status, string Version, IReadOnlyList<LoadedModel> Models, DateTimeOffset ObservedAt, long? WorkingSetBytes, long? RamBytes = null);
internal sealed record UnloadPlan(string Id, LoadedModel Model, string Version, DateTimeOffset ExpiresAt);
internal sealed class UnloadGrant(UnloadPlan plan) { internal UnloadPlan Plan { get; } = plan; }
internal sealed record UnloadResult(string Status, string? Reason = null);
public sealed class ModelSessionException(string code) : IOException(code) { public string Code { get; } = code; }

public sealed class OllamaSessions : IDisposable
{
    public const string Endpoint = "http://127.0.0.1:11434/";
    public const string VerifiedVersion = "0.34.4";
    private readonly HttpClient http;
    private readonly TimeProvider time;
    private readonly bool endpointAllowed;
    private readonly object gate = new();
    private readonly Dictionary<string, UnloadPlan> plans = [];
    private readonly HashSet<UnloadGrant> grants = [];
    private readonly SemaphoreSlim execution = new(1);
    private bool disposed;
    public OllamaSessions() : this(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) }, Environment.GetEnvironmentVariable("OLLAMA_HOST"), TimeProvider.System) { }
    internal OllamaSessions(HttpMessageHandler handler, string? configuredHost = null, TimeProvider? clock = null)
    {
        http = new(handler) { BaseAddress = new(Endpoint), Timeout = Timeout.InfiniteTimeSpan };
        time = clock ?? TimeProvider.System;
        endpointAllowed = string.IsNullOrWhiteSpace(configuredHost) || configuredHost.Trim().TrimEnd('/') is "127.0.0.1:11434" or "localhost:11434" or "http://127.0.0.1:11434" or "http://localhost:11434";
    }
    public async Task<ModelObservation> ObserveAsync(CancellationToken token = default)
    {
        if (!endpointAllowed) return new("RemoteOrCustomEndpointExcluded", "unknown", [], time.GetUtcNow(), null);
        try { return await ObserveCore(token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("Timeout", "unknown", [], time.GetUtcNow(), null); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { return new(e is ModelSessionException m ? m.Code : "Unavailable", "unknown", [], time.GetUtcNow(), null); }
    }
    private async Task<ModelObservation> ObserveCore(CancellationToken token)
    {
        using var version = await Send(HttpMethod.Get, "api/version", null, token);
        string number = String(version.RootElement, "version", 64);
        using var response = await Send(HttpMethod.Get, "api/ps", null, token);
        if (!response.RootElement.TryGetProperty("models", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 128) throw new ModelSessionException("InvalidResponse");
        var models = new List<LoadedModel>(); var names = new HashSet<string>();
        foreach (var item in entries.EnumerateArray())
        {
            string name = String(item, "name", 256), digest = String(item, "digest", 64);
            if (!Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,255}$") || !Regex.IsMatch(digest, "^[a-fA-F0-9]{64}$") || !names.Add(name)) throw new ModelSessionException("InvalidResponse");
            long size = Number(item, "size"); long? vram = item.TryGetProperty("size_vram", out var value) && value.ValueKind != JsonValueKind.Null ? Number(item, "size_vram") : null;
            if (!DateTimeOffset.TryParse(String(item, "expires_at", 64), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var expires)) throw new ModelSessionException("InvalidResponse");
            models.Add(new(name, digest, size, vram, expires));
        }
        return new(number == VerifiedVersion ? "Ready" : "ReadOnlyVersion", number, models.AsReadOnly(), time.GetUtcNow(), WorkingSet());
    }
    internal async Task<UnloadPlan> PrepareUnloadAsync(LoadedModel selected, CancellationToken token = default)
    {
        if (selected.Name.EndsWith(":local", StringComparison.OrdinalIgnoreCase) || selected.Name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || selected.Name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)) throw new ModelSessionException("AmbiguousModelSource");
        var current = await ObserveAsync(token);
        if (current.Status != "Ready") throw new ModelSessionException("UnsupportedOrUnavailable");
        var found = current.Models.SingleOrDefault(m => m.Name == selected.Name && m.Digest == selected.Digest) ?? throw new ModelSessionException("ModelChanged");
        var plan = new UnloadPlan(Guid.NewGuid().ToString("N"), found, current.Version, time.GetUtcNow().AddSeconds(30));
        lock (gate)
        {
            if (disposed) throw new ModelSessionException("SessionClosed");
            foreach (var old in plans.Where(p => p.Value.ExpiresAt <= time.GetUtcNow()).Select(p => p.Key).ToArray()) plans.Remove(old);
            grants.RemoveWhere(g => g.Plan.ExpiresAt <= time.GetUtcNow());
            if (plans.Count >= 32) throw new ModelSessionException("PlanLimit"); plans.Add(plan.Id, plan);
        }
        return plan;
    }
    internal UnloadGrant ConfirmFromDesktop(UnloadPlan plan)
    {
        lock (gate) { Validate(plan); var grant = new UnloadGrant(plan); grants.Add(grant); return grant; }
    }
    internal void Validate(UnloadPlan plan)
    {
        lock (gate)
        {
            if (disposed || !plans.TryGetValue(plan.Id, out var known) || !ReferenceEquals(known, plan)) throw new ModelSessionException("InvalidPlan");
            if (time.GetUtcNow() >= plan.ExpiresAt || time.GetUtcNow() < plan.ExpiresAt.AddSeconds(-30)) throw new ModelSessionException("Expired");
        }
    }
    internal async Task<UnloadResult> UnloadAsync(UnloadPlan plan, UnloadGrant grant, CancellationToken token = default)
    {
        bool sent = false;
        if (!await execution.WaitAsync(0, token)) return new("Blocked", "Busy");
        try
        {
            lock (gate) { Validate(plan); if (!grants.Remove(grant) || !ReferenceEquals(grant.Plan, plan)) throw new ModelSessionException("InvalidApproval"); plans.Remove(plan.Id); }
            var current = await ObserveAsync(token);
            if (current.Status != "Ready" || current.Version != plan.Version) return new("Blocked", "ServerChanged");
            var model = current.Models.SingleOrDefault(m => m.Name == plan.Model.Name);
            if (model == null) return new("AlreadyAbsent");
            if (model.Digest != plan.Model.Digest) return new("Blocked", "ModelChanged");
            if (time.GetUtcNow() >= plan.ExpiresAt) return new("Blocked", "Expired");
            token.ThrowIfCancellationRequested();
            lock (gate) { if (disposed) throw new ModelSessionException("SessionClosed"); }
            sent = true;
            using var reply = await Send(HttpMethod.Post, "api/generate", new { model = plan.Model.Name + ":local", keep_alive = 0, stream = false }, token);
            if (!reply.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True || String(reply.RootElement, "done_reason", 32) != "unload") return new("OutcomeUnknown", "UnexpectedAcknowledgement");
            var after = await ObserveAsync(token);
            if (after.Status != "Ready") return new("OutcomeUnknown", "VerificationUnavailable");
            return after.Models.Any(m => m.Name == plan.Model.Name) ? new("StillLoaded", "ReloadedOrBusy") : new("Unloaded");
        }
        catch (Exception e) when (e is ModelSessionException or HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        { return new(sent ? "OutcomeUnknown" : "Blocked", e is ModelSessionException m ? m.Code : e is OperationCanceledException ? "CancelledOrTimeout" : "Unavailable"); }
        finally { execution.Release(); }
    }
    private async Task<JsonDocument> Send(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK) throw new ModelSessionException("Http" + (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > 65536) throw new ModelSessionException("ResponseTooLarge");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); using var memory = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        { if (memory.Length + count > 65536) throw new ModelSessionException("ResponseTooLarge"); memory.Write(buffer, 0, count); }
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
    private static string String(JsonElement obj, string name, int limit)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > limit || text.Any(char.IsControl)) throw new ModelSessionException("InvalidResponse");
        return text;
    }
    private static long Number(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < 0) throw new ModelSessionException("InvalidResponse");
        return number;
    }
    private static long? WorkingSet()
    {
        long total = 0; var processes = Process.GetProcessesByName("ollama");
        try { if (processes.Length == 0) return null; foreach (var process in processes) total = checked(total + process.WorkingSet64); return total; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OverflowException) { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public void Dispose() { lock (gate) { disposed = true; plans.Clear(); grants.Clear(); } http.Dispose(); }
}
