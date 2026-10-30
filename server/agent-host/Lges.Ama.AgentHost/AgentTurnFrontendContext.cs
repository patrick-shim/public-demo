namespace Lges.Ama.AgentHost;

public enum AgentFrontendMode
{
    AgenticUser,
    OnBehalfOf
}

public sealed class AgentTurnFrontendContext
{
    private readonly AsyncLocal<FrontendState?> _current = new();

    public AgentFrontendMode? Current => _current.Value?.Mode;

    public IDisposable Push(AgentFrontendMode mode)
    {
        var prior = _current.Value;
        var current = new FrontendState(mode);
        _current.Value = current;
        return new FrontendScope(this, current, prior);
    }

    public AgentFrontendMode RequireCurrent() =>
        Current ?? throw new InvalidOperationException(
            "No frontend mode is active for the current agent turn.");

    public AgentFrontendMode ResolveForActivity(
        AgentFrontendMode activityMode,
        bool isLocalEnvironment)
    {
        if (Current is not { } routeMode)
        {
            return activityMode;
        }

        if (!isLocalEnvironment && routeMode != activityMode)
        {
            throw new InvalidOperationException(
                $"Frontend mode '{routeMode}' does not match activity mode '{activityMode}'.");
        }

        return routeMode;
    }

    private sealed class FrontendScope(
        AgentTurnFrontendContext owner,
        FrontendState current,
        FrontendState? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!ReferenceEquals(owner._current.Value, current))
            {
                throw new InvalidOperationException(
                    "Agent frontend scopes must be disposed in creation order.");
            }

            owner._current.Value = prior;
            _disposed = true;
        }
    }

    private sealed record FrontendState(AgentFrontendMode Mode);
}
