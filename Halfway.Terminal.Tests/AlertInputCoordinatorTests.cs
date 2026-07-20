using Halfway.Terminal.Readiness;
using Xunit;

namespace Halfway.Terminal.Tests;

public sealed class AlertInputCoordinatorTests
{
    [Fact]
    public void Alert_waits_until_partial_user_input_is_submitted()
    {
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("PS> ");
        var coordinator = new AlertInputCoordinator(readiness);

        coordinator.SetUserInput("git status");
        coordinator.RequestDemonstrationAlert();

        Assert.Null(coordinator.TakeReadyAlert());
        Assert.True(coordinator.HasQueuedAlert);

        coordinator.SetUserInput(string.Empty);

        Assert.Equal(AlertInputCoordinator.DemonstrationAlert, coordinator.TakeReadyAlert());
        coordinator.CommitAlertDelivery();
        Assert.False(coordinator.HasQueuedAlert);
    }

    [Fact]
    public void Demonstration_alert_can_only_be_taken_once()
    {
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("ready");
        var coordinator = new AlertInputCoordinator(readiness);

        coordinator.RequestDemonstrationAlert();
        var first = coordinator.TakeReadyAlert();
        coordinator.CommitAlertDelivery();
        coordinator.RequestDemonstrationAlert();

        Assert.Equal(AlertInputCoordinator.DemonstrationAlert, first);
        Assert.Null(coordinator.TakeReadyAlert());
        Assert.True(coordinator.AlertDelivered);
    }

    [Fact]
    public void Alert_waits_for_process_readiness()
    {
        var readiness = new ShellReadinessAdapter();
        var coordinator = new AlertInputCoordinator(readiness);
        coordinator.RequestDemonstrationAlert();

        Assert.Null(coordinator.TakeReadyAlert());

        readiness.ObserveOutput("ready");

        Assert.Equal(AlertInputCoordinator.DemonstrationAlert, coordinator.TakeReadyAlert());
    }

    [Fact]
    public void Failed_delivery_releases_the_alert_for_retry()
    {
        var readiness = new ShellReadinessAdapter();
        readiness.ObserveOutput("ready");
        var coordinator = new AlertInputCoordinator(readiness);
        coordinator.RequestDemonstrationAlert();

        Assert.Equal(AlertInputCoordinator.DemonstrationAlert, coordinator.TakeReadyAlert());
        Assert.Null(coordinator.TakeReadyAlert());

        coordinator.ReleaseAlertDelivery();

        Assert.Equal(AlertInputCoordinator.DemonstrationAlert, coordinator.TakeReadyAlert());
        coordinator.CommitAlertDelivery();
        Assert.Null(coordinator.TakeReadyAlert());
    }

    [Fact]
    public void Delivery_cannot_be_committed_without_a_reserved_alert()
    {
        var coordinator = new AlertInputCoordinator(new ShellReadinessAdapter());

        Assert.Throws<InvalidOperationException>(() => coordinator.CommitAlertDelivery());
    }

    [Fact]
    public void DistinctDurableEventsWithTheSameMessageCanEachBeDelivered()
    {
        var readiness = new ShellReadinessAdapter(); readiness.ObserveOutput("ready"); var coordinator = new AlertInputCoordinator(readiness);
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        coordinator.RequestAlert(firstId, AlertInputCoordinator.DemonstrationAlert);
        Assert.Equal(firstId, coordinator.TakeReadyAlertReservation()!.EventId); coordinator.CommitAlertDelivery();
        coordinator.RequestAlert(secondId, AlertInputCoordinator.DemonstrationAlert);
        Assert.Equal(secondId, coordinator.TakeReadyAlertReservation()!.EventId); coordinator.CommitAlertDelivery();
        coordinator.RequestAlert(firstId, AlertInputCoordinator.DemonstrationAlert);
        Assert.Null(coordinator.TakeReadyAlertReservation());
    }

    [Fact]
    public void QueuedDurableAlertCanExpandBeforeReservationButNotWhileInFlight()
    {
        var readiness = new ShellReadinessAdapter(); var coordinator = new AlertInputCoordinator(readiness); var eventId = Guid.NewGuid();
        coordinator.RequestAlert(eventId, "single"); coordinator.RequestAlert(eventId, "batched"); readiness.ObserveOutput("ready");
        Assert.Equal("batched", coordinator.TakeReadyAlertReservation()!.Message);
        coordinator.RequestAlert(eventId, "too late"); coordinator.ReleaseAlertDelivery();
        Assert.Equal("batched", coordinator.TakeReadyAlertReservation()!.Message);
    }
}
