using Halfway.Terminal.Readiness;

namespace Halfway.Terminal;

public sealed class AlertInputCoordinator
{
    public const string DemonstrationAlert =
        "[Halfway Alert!] Runtime completed. Continue orchestration.";

    private readonly IProcessReadinessAdapter _readiness;
    private readonly object _gate = new();
    private AlertInputReservation? _queuedAlert;
    private bool _alertInFlight;
    private bool _hasPartialUserInput;
    private readonly HashSet<Guid> _delivered = [];

    public AlertInputCoordinator(IProcessReadinessAdapter readiness)
    {
        _readiness = readiness;
    }

    public bool HasPartialUserInput { get { lock (_gate) return _hasPartialUserInput; } }

    public bool HasQueuedAlert { get { lock (_gate) return _queuedAlert is not null; } }

    public bool AlertDelivered { get { lock (_gate) return _delivered.Count > 0; } }

    public void SetUserInput(string input)
    {
        lock (_gate) _hasPartialUserInput = !string.IsNullOrEmpty(input);
    }

    public void RequestDemonstrationAlert()
    {
        RequestAlert(Guid.Empty, Guid.Empty, DemonstrationAlert);
    }

    public void RequestAlert(string alert)
    {
        RequestAlert(Guid.Empty, Guid.Empty, alert);
    }

    public void RequestAlert(Guid eventId, string alert)
    {
        RequestAlert(eventId, Guid.Empty, alert);
    }

    public void RequestAlert(Guid eventId, Guid parentSessionId, string alert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alert);
        lock (_gate)
        {
            if (!_delivered.Contains(eventId))
            {
                if (_queuedAlert is null)
                {
                    _queuedAlert = new AlertInputReservation(eventId, parentSessionId, alert);
                }
                else if (!_alertInFlight && _queuedAlert.EventId == eventId)
                {
                    _queuedAlert = new AlertInputReservation(eventId, parentSessionId, alert);
                }
            }
        }
    }

    public string? TakeReadyAlert()
    {
        return TakeReadyAlertReservation()?.Message;
    }

    public AlertInputReservation? TakeReadyAlertReservation()
    {
        lock (_gate)
        {
            if (_queuedAlert is null || _alertInFlight || _hasPartialUserInput || !_readiness.IsReadyForInput)
                return null;

            _alertInFlight = true;
            return _queuedAlert;
        }
    }

    public bool CanWrite(AlertInputReservation reservation)
    {
        lock (_gate)
        {
            return _alertInFlight &&
                !_hasPartialUserInput &&
                _readiness.IsReadyForInput &&
                _queuedAlert == reservation;
        }
    }

    public void CommitAlertDelivery()
    {
        lock (_gate)
        {
            if (!_alertInFlight)
                throw new InvalidOperationException("There is no alert delivery in progress.");

            _alertInFlight = false;
            _delivered.Add(_queuedAlert!.EventId);
            _queuedAlert = null;
        }
    }

    public void ReleaseAlertDelivery()
    {
        lock (_gate) _alertInFlight = false;
    }
}

public sealed record AlertInputReservation(Guid EventId, Guid ParentSessionId, string Message);
