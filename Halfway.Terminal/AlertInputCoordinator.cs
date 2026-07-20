using Halfway.Terminal.Readiness;

namespace Halfway.Terminal;

public sealed class AlertInputCoordinator
{
    public const string DemonstrationAlert =
        "[Halfway Alert!] Runtime completed. Continue orchestration.";

    private readonly IProcessReadinessAdapter _readiness;
    private string? _queuedAlert;
    private bool _alertDelivered;

    public AlertInputCoordinator(IProcessReadinessAdapter readiness)
    {
        _readiness = readiness;
    }

    public bool HasPartialUserInput { get; private set; }

    public bool HasQueuedAlert => _queuedAlert is not null && !_alertDelivered;

    public bool AlertDelivered => _alertDelivered;

    public void SetUserInput(string input) => HasPartialUserInput = !string.IsNullOrEmpty(input);

    public void RequestDemonstrationAlert()
    {
        RequestAlert(DemonstrationAlert);
    }

    public void RequestAlert(string alert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alert);
        if (!_alertDelivered)
        {
            _queuedAlert ??= alert;
        }
    }

    public string? TakeReadyAlert()
    {
        if (_queuedAlert is null || _alertDelivered || HasPartialUserInput || !_readiness.IsReadyForInput)
        {
            return null;
        }

        _alertDelivered = true;
        return _queuedAlert;
    }
}
