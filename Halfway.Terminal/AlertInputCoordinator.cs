using Halfway.Terminal.Readiness;

namespace Halfway.Terminal;

public sealed class AlertInputCoordinator
{
    public const string DemonstrationAlert =
        "[Halfway Alert!] Runtime completed. Continue orchestration.";

    private readonly IProcessReadinessAdapter _readiness;
    private bool _demonstrationRequested;
    private bool _demonstrationDelivered;

    public AlertInputCoordinator(IProcessReadinessAdapter readiness)
    {
        _readiness = readiness;
    }

    public bool HasPartialUserInput { get; private set; }

    public bool HasQueuedAlert => _demonstrationRequested && !_demonstrationDelivered;

    public bool AlertDelivered => _demonstrationDelivered;

    public void SetUserInput(string input) => HasPartialUserInput = !string.IsNullOrEmpty(input);

    public void RequestDemonstrationAlert()
    {
        if (!_demonstrationDelivered)
        {
            _demonstrationRequested = true;
        }
    }

    public string? TakeReadyAlert()
    {
        if (!_demonstrationRequested || _demonstrationDelivered || HasPartialUserInput || !_readiness.IsReadyForInput)
        {
            return null;
        }

        _demonstrationDelivered = true;
        return DemonstrationAlert;
    }
}
