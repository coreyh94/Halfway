using Halfway.Terminal.Readiness;

namespace Halfway.Terminal;

public sealed class AlertInputCoordinator
{
    public const string DemonstrationAlert =
        "[Halfway Alert!] Runtime completed. Continue orchestration.";

    private readonly IProcessReadinessAdapter _readiness;
    private AlertInputReservation? _queuedAlert;
    private bool _alertInFlight;
    private readonly HashSet<Guid> _delivered = [];

    public AlertInputCoordinator(IProcessReadinessAdapter readiness)
    {
        _readiness = readiness;
    }

    public bool HasPartialUserInput { get; private set; }

    public bool HasQueuedAlert => _queuedAlert is not null;

    public bool AlertDelivered => _delivered.Count > 0;

    public void SetUserInput(string input) => HasPartialUserInput = !string.IsNullOrEmpty(input);

    public void RequestDemonstrationAlert()
    {
        RequestAlert(Guid.Empty, DemonstrationAlert);
    }

    public void RequestAlert(string alert)
    {
        RequestAlert(Guid.Empty, alert);
    }

    public void RequestAlert(Guid eventId, string alert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alert);
        if (!_delivered.Contains(eventId))
        {
            if (_queuedAlert is null)
            {
                _queuedAlert = new AlertInputReservation(eventId, alert);
            }
            else if (!_alertInFlight && _queuedAlert.EventId == eventId)
            {
                _queuedAlert = new AlertInputReservation(eventId, alert);
            }
        }
    }

    public string? TakeReadyAlert()
    {
        return TakeReadyAlertReservation()?.Message;
    }

    public AlertInputReservation? TakeReadyAlertReservation()
    {
        if (_queuedAlert is null || _alertInFlight || HasPartialUserInput || !_readiness.IsReadyForInput)
        {
            return null;
        }

        _alertInFlight = true;
        return _queuedAlert;
    }

    public void CommitAlertDelivery()
    {
        if (!_alertInFlight)
        {
            throw new InvalidOperationException("There is no alert delivery in progress.");
        }

        _alertInFlight = false;
        _delivered.Add(_queuedAlert!.EventId);
        _queuedAlert = null;
    }

    public void ReleaseAlertDelivery()
    {
        _alertInFlight = false;
    }
}

public sealed record AlertInputReservation(Guid EventId, string Message);
