namespace Halfway.Core;

public sealed record CompletionAlert(
    Guid EventId,
    Guid ParentSessionId,
    IReadOnlyList<string> CompletedAgents)
{
    public string Message
    {
        get
        {
            var names = CompletedAgents.Count switch
            {
                0 => "A sub-agent",
                1 => CompletedAgents[0],
                2 => $"{CompletedAgents[0]} and {CompletedAgents[1]}",
                _ => $"{string.Join(", ", CompletedAgents.Take(CompletedAgents.Count - 1))} and {CompletedAgents[^1]}",
            };

            return $"[Halfway Alert!] {names} completed. Continue orchestration.";
        }
    }
}
