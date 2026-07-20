namespace Halfway.Terminal;

public sealed record TerminalLaunchOptions(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TerminalSize InitialSize)
{
    public static TerminalLaunchOptions PowerShell(string workingDirectory) => new(
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoLogo"],
        workingDirectory,
        new TerminalSize(100, 30));
}
