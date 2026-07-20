using Halfway.Terminal;

namespace Halfway.Runtime;

public interface IRuntimeLaunchAdapter
{
    TerminalLaunchOptions CreateOptions(
        RuntimeLaunchContext context,
        CancellationToken cancellationToken = default);
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
