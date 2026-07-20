namespace Halfway.Core;

public sealed record ApplicationRun(
    Guid Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CleanShutdownAtUtc,
    string ApplicationVersion)
{
    public bool EndedCleanly => CleanShutdownAtUtc is not null;
}

public sealed record ApplicationRunStart(
    ApplicationRun CurrentRun,
    ApplicationRun? PreviousRun)
{
    public bool PreviousRunWasUnclean => PreviousRun is { EndedCleanly: false };
}
