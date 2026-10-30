namespace Lges.Ama.AgentHost;

public sealed class AgentHostReadinessState
{
    private int _ready;
    private int _durableStorage;

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public bool HasDurableStorage => Volatile.Read(ref _durableStorage) == 1;

    public void MarkReady(bool hasDurableStorage)
    {
        Volatile.Write(ref _durableStorage, hasDurableStorage ? 1 : 0);
        Volatile.Write(ref _ready, 1);
    }
}
