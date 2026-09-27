using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHarbor.Discovery;

public sealed record DiscoveryContext(string Profile, IReadOnlyDictionary<string, string> Variables,
    Func<string, string?> Resolve, Func<string, string?> FileVersion)
{
    public string? Get(string key) => Variables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    public static DiscoveryContext Current()
    {
        var variables = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(x => (string)x.Key, x => (string)x.Value!, StringComparer.OrdinalIgnoreCase);
        string? Resolve(string name)
        {
            foreach (string directory in (variables.GetValueOrDefault("PATH") ?? "").Split(';'))
            {
                string dir = directory.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(dir) || dir.Length < 3 || !char.IsAsciiLetter(dir[0]) || dir[1] != ':' || dir[2] != '\\') continue;
                string candidate = Path.Combine(dir, name == "npm-cli" ? "node_modules/npm/bin/npm-cli.js" : name + ".exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }
        return new(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), variables, Resolve,
            path => FileVersionInfo.GetVersionInfo(path).ProductVersion);
    }
}

public sealed class ToolDiscovery(ICommandRunner runner, DiscoveryContext context)
{
    public async Task<IReadOnlyList<StoreDescriptor>> DiscoverAsync(CancellationToken token = default)
    {
        var stores = new List<StoreDescriptor>();
        foreach (var tool in Enum.GetValues<ToolKind>().Where(t => t != ToolKind.Custom))
        {
            if (token.IsCancellationRequested) { stores.Add(Unavailable(tool, "조회 취소됨")); continue; }
            try { stores.Add(await DiscoverOne(tool, token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { stores.Add(Unavailable(tool, "조회 취소됨")); }
            catch (Exception e) when (e is CommandFailure or JsonException or KeyNotFoundException or IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { stores.Add(Unavailable(tool, e is CommandFailure failure ? failure.Reason : "설정 또는 도구 응답을 확인할 수 없음")); }
        }
        return stores;
    }

    private async Task<StoreDescriptor> DiscoverOne(ToolKind tool, CancellationToken token)
    {
        string? executable = context.Resolve(tool switch { ToolKind.Pip or ToolKind.HuggingFace => "python", ToolKind.Npm => "node", _ => tool.ToString().ToLowerInvariant() });
        if (executable == null) return Unavailable(tool, "실행 파일을 찾을 수 없음", DiscoveryStatus.NotInstalled);
        string version;
        if (tool == ToolKind.Ollama)
        {
            version = Version(context.FileVersion(executable) ?? "");
            return Native(tool, context.Get("OLLAMA_MODELS") ?? Path.Combine(context.Profile, ".ollama", "models"), version,
                context.Get("OLLAMA_MODELS") != null ? "OLLAMA_MODELS · 현재 프로세스 설정" : "Windows 기본 모델 경로 · 서버 설정 일치 미검증");
        }
        if (tool == ToolKind.HuggingFace)
        {
            var result = await Run(executable, ["-B", "-c", "import importlib.metadata; print(importlib.metadata.version('huggingface-hub'))"], token).ConfigureAwait(false);
            if (result.ExitCode != 0) return Unavailable(tool, "현재 Python의 huggingface-hub를 확인할 수 없음", DiscoveryStatus.NotInstalled);
            version = Version(result.Output);
            string home = context.Get("HF_HOME") ?? Path.Combine(context.Get("XDG_CACHE_HOME") ?? Path.Combine(context.Profile, ".cache"), "huggingface");
            return Native(tool, context.Get("HF_HUB_CACHE") ?? context.Get("HUGGINGFACE_HUB_CACHE") ?? Path.Combine(home, "hub"), version,
                "HF_HUB_CACHE → HUGGINGFACE_HUB_CACHE → HF_HOME/XDG → 기본 hub · 토큰 파일 제외");
        }
        if (tool == ToolKind.Docker) return await Docker(executable, token).ConfigureAwait(false);
        string[] prefix = tool == ToolKind.Pip ? ["-B", "-m", "pip", "--disable-pip-version-check"] : [];
        if (tool == ToolKind.Npm)
        {
            string? script = context.Resolve("npm-cli");
            if (script == null) return Unavailable(tool, "npm CLI를 찾을 수 없음", DiscoveryStatus.NotInstalled);
            prefix = [script, "--logs-max=0", "--update-notifier=false"];
        }
        var ver = await Run(executable, [.. prefix, "--version"], token).ConfigureAwait(false);
        if (ver.ExitCode != 0) return Unavailable(tool, "도구 버전을 확인할 수 없음");
        version = Version(ver.Output);
        string[] query = tool == ToolKind.Npm ? ["config", "get", "cache"] : ["cache", "dir"];
        var location = await Run(executable, [.. prefix, .. query], token).ConfigureAwait(false);
        if (location.ExitCode != 0) return Unavailable(tool, "캐시 경로 조회 실패");
        return Native(tool, location.Output.Trim(), version, tool == ToolKind.Npm ? "npm config get cache · 사용자 설정" : $"{tool.ToString().ToLowerInvariant()} cache dir · 현재 사용자/선택된 실행 파일");
    }

    private Task<CommandResult> Run(string executable, string[] arguments, CancellationToken token, IReadOnlyDictionary<string, string?>? environment = null)
        => runner.RunAsync(new(executable, arguments, context.Profile, environment), token);

    private async Task<StoreDescriptor> Docker(string executable, CancellationToken token)
    {
        var ver = await Run(executable, ["--version"], token).ConfigureAwait(false);
        if (ver.ExitCode != 0) return Unavailable(ToolKind.Docker, "Docker CLI 버전 확인 실패");
        string version = Version(ver.Output);
        string? selected = context.Get("DOCKER_CONTEXT"), endpoint = selected == null ? context.Get("DOCKER_HOST") : null;
        if (endpoint == null)
        {
            if (selected == null)
            {
                var show = await Run(executable, ["context", "show"], token).ConfigureAwait(false);
                if (show.ExitCode != 0) return Unavailable(ToolKind.Docker, "Docker context 확인 실패");
                selected = show.Output.Trim();
            }
            if (!Regex.IsMatch(selected, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")) return Unavailable(ToolKind.Docker, "Docker context 이름 미지원", DiscoveryStatus.InvalidConfiguration);
            var inspect = await Run(executable, ["context", "inspect", selected, "--format", "{{json .Endpoints.docker.Host}}"], token).ConfigureAwait(false);
            if (inspect.ExitCode != 0) return Unavailable(ToolKind.Docker, "Docker endpoint 확인 실패");
            endpoint = JsonSerializer.Deserialize<string>(inspect.Output.Trim());
        }
        // Pin the actual local named pipe, not a mutable context name. Never contact remote engines.
        if (endpoint == null || !Regex.IsMatch(endpoint, @"^npipe:////\./pipe/[A-Za-z0-9_.-]+$", RegexOptions.IgnoreCase))
            return new("docker", ToolKind.Docker, null, version, "원격 또는 미지원 endpoint · 연결하지 않음", DiscoveryStatus.RemoteExcluded, StorageEnvironment.Remote);
        var env = new Dictionary<string, string?> { ["DOCKER_CONTEXT"] = null, ["DOCKER_HOST"] = null, ["DOCKER_TLS_VERIFY"] = null, ["DOCKER_TLS"] = null };
        var df = await Run(executable, ["--host", endpoint, "system", "df", "--format", "{{json .}}"], token, env).ConfigureAwait(false);
        if (df.ExitCode != 0) return new("docker", ToolKind.Docker, null, version, "로컬 엔진 사용량 조회 실패 · 엔진 실행 상태 확인", DiscoveryStatus.Unavailable, StorageEnvironment.LocalEngine);
        var usage = new List<string>();
        foreach (string line in df.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var json = JsonDocument.Parse(line);
            string? type = json.RootElement.GetProperty("Type").GetString();
            string? size = json.RootElement.GetProperty("Size").GetString();
            if (type is not ("Images" or "Containers" or "Local Volumes" or "Build Cache") || size == null
                || !Regex.IsMatch(size, @"^\d+(?:\.\d+)?\s*(?:B|kB|KB|MB|GB|TB|KiB|MiB|GiB|TiB)$"))
                throw new CommandFailure("UnsupportedDockerOutput");
            usage.Add(type + ": " + size);
        }
        if (usage.Count != 4 || usage.Select(s => s.Split(':')[0]).Distinct().Count() != 4) throw new CommandFailure("IncompleteDockerOutput");
        return new("docker", ToolKind.Docker, null, version, "명시적 로컬 named pipe · docker system df · 호스트 파일 합계에서 제외",
            DiscoveryStatus.Ready, StorageEnvironment.LocalEngine, string.Join(" · ", usage));
    }

    private static string Version(string output)
    {
        var match = Regex.Match(output, @"\b\d+\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9.]+)?\b");
        return match.Success ? match.Value : "미확인";
    }
    private static StoreDescriptor Native(ToolKind tool, string path, string version, string evidence)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32700 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('/') || path[3..].Split('\\').Any(p => p is "." or ".."))
            return Unavailable(tool, "명확한 로컬 절대 경로가 아님", DiscoveryStatus.InvalidConfiguration);
        return new(tool.ToString().ToLowerInvariant(), tool, path.TrimEnd('\\'), version, evidence);
    }
    private static StoreDescriptor Unavailable(ToolKind tool, string evidence, DiscoveryStatus status = DiscoveryStatus.Unavailable)
        => new(tool.ToString().ToLowerInvariant(), tool, null, "미확인", evidence, status);
}
