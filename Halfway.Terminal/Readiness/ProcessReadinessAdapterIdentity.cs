namespace Halfway.Terminal.Readiness;

public sealed record ProcessReadinessAdapterIdentity
{
    public ProcessReadinessAdapterIdentity(string identifier, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        Identifier = identifier;
        Version = version;
    }

    public string Identifier { get; }
    public int Version { get; }
    public override string ToString() => $"{Identifier}/v{Version}";
}
