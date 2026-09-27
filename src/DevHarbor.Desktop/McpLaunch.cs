using System.IO;
namespace DevHarbor.Desktop;
internal sealed record McpLaunch(string Command, string[] Arguments)
{
    internal static McpLaunch Resolve()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "DevHarbor.slnx"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        if (root != null)
        {
            string sdk = Path.Combine(root, ".tools", "dotnet", "dotnet.exe");
            return new(File.Exists(sdk) ? sdk : "dotnet", [Path.Combine(root, "src", "DevHarbor.Mcp", "bin", "Release", "net10.0-windows", "DevHarbor.Mcp.dll")]);
        }
        return new("dotnet", [Path.Combine(AppContext.BaseDirectory, "DevHarbor.Mcp.dll")]);
    }
}
