namespace Halfway.Terminal.Readiness;

public interface IProcessReadinessAdapter
{
    ProcessReadinessAdapterIdentity Identity { get; }

    bool IsReadyForInput { get; }

    void ObserveOutput(string output);

    void ObserveInputSubmitted();
}
