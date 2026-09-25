namespace DevHarbor.Windows;

internal static class BoundaryPath
{
    internal static string Canonical(string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new BoundaryException(BoundaryError.UnsupportedPlatform, "Windows 11 x64 process required");
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.Contains('/') || path[3..].Contains(':'))
            throw new BoundaryException(BoundaryError.InvalidPath, "Only unambiguous absolute local drive paths are accepted");
        foreach (var part in path[3..].Split('\\')) ValidateLeaf(part);
        return Path.GetFullPath(path);
    }

    internal static void ValidateLeaf(string name)
    {
        string stem = name.Split('.')[0];
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or ".." || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new BoundaryException(BoundaryError.InvalidPath, "Invalid or ambiguous path component");
    }

    internal static bool Contains(string root, string path) => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    internal static string Root(string root)
    {
        root = Canonical(root);
        var blocked = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        if (root.Length <= 3 || blocked.Any(p => p.Length > 0 && (root.Equals(p, StringComparison.OrdinalIgnoreCase) || Contains(p, root)))
            || root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            throw new BoundaryException(BoundaryError.ProtectedPath, "System roots and entire user profiles are not scopes");
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
            throw new BoundaryException(BoundaryError.UnsupportedFileSystem, "Only fixed local volumes are supported");
        return root;
    }
}
