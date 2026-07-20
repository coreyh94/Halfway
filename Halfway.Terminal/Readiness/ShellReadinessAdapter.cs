namespace Halfway.Terminal.Readiness;

public sealed class ShellReadinessAdapter : IProcessReadinessAdapter
{
    public bool IsReadyForInput { get; private set; }

    public void ObserveOutput(string output)
    {
        if (!string.IsNullOrEmpty(output))
        {
            IsReadyForInput = true;
        }
    }
}
