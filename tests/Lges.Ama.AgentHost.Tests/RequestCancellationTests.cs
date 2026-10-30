using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class RequestCancellationTests
{
    [TestMethod]
    public void RecognizesCancellationRequestedByCaller()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.IsTrue(RequestCancellation.IsRequested(
            new OperationCanceledException(cancellation.Token),
            cancellation.Token));
    }

    [TestMethod]
    public void DoesNotMisclassifyDependencyCancellation()
    {
        Assert.IsFalse(RequestCancellation.IsRequested(
            new OperationCanceledException(),
            CancellationToken.None));
    }

    [TestMethod]
    public void DoesNotMisclassifyOtherFailures()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.IsFalse(RequestCancellation.IsRequested(
            new TimeoutException(),
            cancellation.Token));
    }
}
