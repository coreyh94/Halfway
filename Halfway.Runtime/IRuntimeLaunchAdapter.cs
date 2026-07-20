using Halfway.Terminal;

namespace Halfway.Runtime;

public interface IRuntimeLaunchAdapter
{
    TerminalLaunchOptions CreateOptions(
        RuntimeLaunchContext context,
        CancellationToken cancellationToken = default);
}

public enum RuntimeLaunchProfile
{
    PowerShell,
    Codex,
}

public sealed record RuntimeLaunchContext(
    string WorkingDirectory,
    TerminalSize InitialSize);

public sealed class PowerShellRuntimeLaunchAdapter : IRuntimeLaunchAdapter
{
    public TerminalLaunchOptions CreateOptions(
        RuntimeLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(context.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Runtime working directory does not exist: {context.WorkingDirectory}");
        }

        return TerminalLaunchOptions.PowerShell(context.WorkingDirectory) with
        {
            InitialSize = context.InitialSize,
        };
    }
}

public sealed class CodexRuntimeLaunchAdapter : IRuntimeLaunchAdapter
{
    public TerminalLaunchOptions CreateOptions(
        RuntimeLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(context.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Runtime working directory does not exist: {context.WorkingDirectory}");
        }

        return ProcessCommandResolver.ResolveCodex(context.WorkingDirectory, context.InitialSize);
    }
}

public static class RuntimeLaunchAdapterSelection
{
    public static IRuntimeLaunchAdapter Create(string? configuredProfile)
    {
        var profile = configuredProfile?.Trim().ToLowerInvariant() switch
        {
            null or "" or "powershell" => RuntimeLaunchProfile.PowerShell,
            "codex" => RuntimeLaunchProfile.Codex,
            _ => throw new ArgumentException(
                $"Unsupported Runtime launch profile '{configuredProfile}'. Use 'powershell' or 'codex'.",
                nameof(configuredProfile)),
        };

        return Create(profile);
    }

    public static IRuntimeLaunchAdapter Create(RuntimeLaunchProfile profile) => profile switch
    {
        RuntimeLaunchProfile.PowerShell => new PowerShellRuntimeLaunchAdapter(),
        RuntimeLaunchProfile.Codex => new CodexRuntimeLaunchAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported Runtime launch profile."),
    };
}
