namespace Lges.Ama.AgentHost;

public sealed class AgentTurnCoordinator
{
    private const int DefaultCompletedActivityCapacity = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<string, ConversationGate> _gates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedActivities = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedOrder = new();
    private readonly int _completedActivityCapacity;

    public AgentTurnCoordinator() : this(DefaultCompletedActivityCapacity)
    {
    }

    internal AgentTurnCoordinator(int completedActivityCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedActivityCapacity, 1);
        _completedActivityCapacity = completedActivityCapacity;
    }

    public async Task<AgentTurnLease> EnterAsync(
        AgentFrontendMode frontendMode,
        string? conversationId,
        string? activityId,
        CancellationToken cancellationToken)
    {
        var conversationHash = AgentTurnDiagnostics.HashIdentifier(conversationId);
        var activityHash = AgentTurnDiagnostics.HashIdentifier(activityId);
        var gateKey = string.IsNullOrEmpty(conversationHash)
            ? $"{frontendMode}:{activityHash}:{Guid.NewGuid():N}"
            : $"{frontendMode}:{conversationHash}";
        var activityKey = string.IsNullOrEmpty(activityHash)
            ? string.Empty
            : $"{frontendMode}:{conversationHash}:{activityHash}";

        ConversationGate gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(gateKey, out gate!))
            {
                gate = new ConversationGate();
                _gates.Add(gateKey, gate);
            }

            gate.ReferenceCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            ReleaseReference(gateKey, gate);
            throw;
        }

        var duplicate = false;
        if (!string.IsNullOrEmpty(activityKey))
        {
            lock (_sync)
            {
                duplicate = !_completedActivities.Add(activityKey);
                if (!duplicate)
                {
                    _completedOrder.Enqueue(activityKey);
                    while (_completedOrder.Count > _completedActivityCapacity)
                    {
                        _completedActivities.Remove(_completedOrder.Dequeue());
                    }
                }
            }
        }

        return new AgentTurnLease(
            duplicate,
            () =>
            {
                gate.Semaphore.Release();
                ReleaseReference(gateKey, gate);
            });
    }

    private void ReleaseReference(string gateKey, ConversationGate gate)
    {
        lock (_sync)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0)
            {
                _gates.Remove(gateKey);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class ConversationGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }
}

public sealed class AgentTurnLease : IDisposable
{
    private readonly Action _release;
    private bool _disposed;

    internal AgentTurnLease(bool isDuplicate, Action release)
    {
        IsDuplicate = isDuplicate;
        _release = release;
    }

    public bool IsDuplicate { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _release();
        _disposed = true;
    }
}
