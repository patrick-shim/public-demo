namespace Lges.Ama.AgentHost;

public sealed class ToolUserContext
{
    private readonly AsyncLocal<Guid?> _currentUserId = new();

    public Guid RequireUserId() => _currentUserId.Value
        ?? throw new ToolContentEvaluationException(
            "A human Entra user ID is required before invoking a protected tool.");

    public IDisposable Push(Guid userId)
    {
        var prior = _currentUserId.Value;
        _currentUserId.Value = userId;
        return new Scope(() => _currentUserId.Value = prior);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            dispose();
            _disposed = true;
        }
    }
}
