namespace Halfway.Terminal;

public interface ITerminalSession : IAsyncDisposable
{
    event EventHandler<string>? OutputReceived;

    event EventHandler<TerminalExit>? Exited;

    int ProcessId { get; }

    Task Completion { get; }

    ValueTask WriteAsync(string input, CancellationToken cancellationToken = default);

    void Resize(TerminalSize size);

    Task StopAsync(CancellationToken cancellationToken = default);
}
