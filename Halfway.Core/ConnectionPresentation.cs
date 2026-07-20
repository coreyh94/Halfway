namespace Halfway.Core;

public static class ConnectionPresentation
{
    public static bool IsConnected(IEnumerable<AgentStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return statuses.Any(status => status is AgentStatus.Running or AgentStatus.Waiting);
    }
}
