namespace Halfway.Terminal;

public interface ITerminalSessionFactory
{
    Task<ITerminalSession> StartAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default);
}
