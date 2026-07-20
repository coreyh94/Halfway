using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class ConnectionPresentationTests
{
    [Theory]
    [MemberData(nameof(ConnectionCases))]
    public void ConnectionIsDerivedFromCurrentLiveStates(AgentStatus[] statuses, bool expected)
    {
        Assert.Equal(expected, ConnectionPresentation.IsConnected(statuses));
    }

    public static TheoryData<AgentStatus[], bool> ConnectionCases => new()
    {
        { [AgentStatus.Running], true },
        { [AgentStatus.Waiting], true },
        { [AgentStatus.Running, AgentStatus.Waiting], true },
        { [AgentStatus.Queued], false },
        { [AgentStatus.Completed], false },
        { [AgentStatus.Failed], false },
        { [AgentStatus.Disconnected], false },
    };
}
