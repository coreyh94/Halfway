namespace Halfway.Terminal.Readiness;

public static class ProcessReadinessAdapterCatalog
{
    public static IProcessReadinessAdapter Create(ProcessReadinessAdapterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity == ShellReadinessAdapter.AdapterIdentity) return new ShellReadinessAdapter();
        if (identity == CodexReadinessAdapter.AdapterIdentity) return new CodexReadinessAdapter();
        throw new NotSupportedException($"Readiness adapter '{identity}' is not supported.");
    }

    public static IProcessReadinessAdapter Create(string identifier, int version) =>
        Create(new ProcessReadinessAdapterIdentity(identifier, version));
}
