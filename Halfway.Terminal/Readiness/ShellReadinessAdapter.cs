namespace Halfway.Terminal.Readiness;

public sealed class ShellReadinessAdapter : IProcessReadinessAdapter
{
    public static ProcessReadinessAdapterIdentity AdapterIdentity { get; } = new("shell", 1);

    public ProcessReadinessAdapterIdentity Identity => AdapterIdentity;

    public bool IsReadyForInput { get; private set; }

    public void ObserveOutput(string output)
    {
        if (!string.IsNullOrEmpty(output))
        {
            IsReadyForInput = true;
        }
    }

    public void ObserveInputSubmitted() => IsReadyForInput = false;
}
