namespace Halfway.Terminal.Windows;

public sealed class ConPtyTerminalSessionFactory : ITerminalSessionFactory
{
    public Task<ITerminalSession> StartAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ITerminalSession>(ConPtyTerminalSession.Start(options));
    }
}
