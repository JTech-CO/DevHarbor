using System.Diagnostics;
using System.Text;

namespace DevHarbor.Discovery;

public sealed record CommandRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null);
public sealed record CommandResult(int ExitCode, string Output, string Error);
public interface ICommandRunner { Task<CommandResult> RunAsync(CommandRequest request, CancellationToken token); }
public sealed class CommandFailure(string reason) : IOException(reason) { public string Reason { get; } = reason; }

public sealed class CommandRunner(TimeSpan? timeout = null, int outputLimit = 32768) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Executable) || !request.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(request.Executable)) throw new CommandFailure("ExecutableUnavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        var info = new ProcessStartInfo(request.Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = request.WorkingDirectory, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (string argument in request.Arguments) info.ArgumentList.Add(argument);
        if (request.Environment != null)
            foreach (var pair in request.Environment)
                if (pair.Value == null) info.Environment.Remove(pair.Key); else info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        bool started = false;
        int exceeded = 0;
        async Task<string> ReadBounded(StreamReader stream)
        {
            var result = new StringBuilder(); var buffer = new char[1024];
            while (true)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0) return result.ToString();
                if (result.Length + count > outputLimit)
                {
                    Interlocked.Exchange(ref exceeded, 1); deadline.Cancel();
                    throw new CommandFailure("OutputLimit");
                }
                result.Append(buffer, 0, count);
            }
        }
        try
        {
            started = process.Start();
            if (!started) throw new CommandFailure("StartFailed");
            process.StandardInput.Close();
            var stdout = ReadBounded(process.StandardOutput); var stderr = ReadBounded(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception e) when (e is OperationCanceledException or CommandFailure or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            token.ThrowIfCancellationRequested();
            if (exceeded != 0) throw new CommandFailure("OutputLimit");
            if (deadline.IsCancellationRequested) throw new CommandFailure("Timeout");
            throw new CommandFailure(e is CommandFailure failure ? failure.Reason : "StartFailed");
        }
    }
}
