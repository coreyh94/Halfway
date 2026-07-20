using Halfway.Terminal.Readiness;

namespace Halfway.Runtime;

public static class RuntimeReadinessAdapterSelection
{
    public static IProcessReadinessAdapter Create(RuntimeLaunchProfile profile) => profile switch
    {
        RuntimeLaunchProfile.PowerShell => ProcessReadinessAdapterCatalog.Create(ShellReadinessAdapter.AdapterIdentity),
        RuntimeLaunchProfile.Codex => ProcessReadinessAdapterCatalog.Create(CodexReadinessAdapter.AdapterIdentity),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported Runtime launch profile."),
    };
}
