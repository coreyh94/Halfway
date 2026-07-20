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
}
