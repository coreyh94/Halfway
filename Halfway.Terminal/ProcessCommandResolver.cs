namespace Halfway.Terminal;

public static class ProcessCommandResolver
{
    public static TerminalLaunchOptions ResolveCodex(string workingDirectory, TerminalSize size)
    {
        var resolved = ResolveFromPath("codex");
        if (resolved is null)
        {
            throw new FileNotFoundException(
                "Codex CLI was not found on PATH. Install Codex, then restart Halfway.");
        }

        var extension = Path.GetExtension(resolved);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new TerminalLaunchOptions(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/s", "/c", resolved],
                workingDirectory,
                size);
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new TerminalLaunchOptions(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-ExecutionPolicy", "Bypass", "-File", resolved],
                workingDirectory,
                size);
        }

        return new TerminalLaunchOptions(resolved, [], workingDirectory, size);
    }

    internal static string? ResolveFromPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = new[] { ".exe", ".cmd", ".bat", ".ps1" };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }
}
