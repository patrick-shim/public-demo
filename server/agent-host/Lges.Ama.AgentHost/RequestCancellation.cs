namespace Lges.Ama.AgentHost;

internal static class RequestCancellation
{
    public static bool IsRequested(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
