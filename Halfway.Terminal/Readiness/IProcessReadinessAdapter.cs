namespace Halfway.Terminal.Readiness;

public interface IProcessReadinessAdapter
{
    bool IsReadyForInput { get; }

    void ObserveOutput(string output);

    void ObserveInputSubmitted();
}
