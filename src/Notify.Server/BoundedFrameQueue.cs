namespace Notify;

// Bounded per-direction audio queue. Clear() is used by barge-in to discard audio that has not
// reached the transport yet; the capacity is the 2-second call limit.
public sealed class BoundedFrameQueue(int capacity)
{
    private readonly object gate = new();
    private readonly Queue<byte[]> frames = new();
    private readonly SemaphoreSlim available = new(0);
    private bool completed;
    public int Count { get { lock (gate) return frames.Count; } }
    public int Capacity { get; } = capacity;
    public bool TryEnqueue(byte[] frame) { lock (gate) { if (completed || frames.Count >= Capacity) return false; frames.Enqueue(frame); available.Release(); return true; } }
    public async Task<byte[]?> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            await available.WaitAsync(ct);
            lock (gate) { if (frames.Count > 0) return frames.Dequeue(); if (completed) return null; }
        }
    }
    public void Clear() { lock (gate) { frames.Clear(); while (available.Wait(0)) { } } }
    public void Complete() { lock (gate) { completed = true; } available.Release(); }
}
