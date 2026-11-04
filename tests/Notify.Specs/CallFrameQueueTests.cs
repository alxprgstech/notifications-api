using Notify;
using Xunit;

namespace NotifySpecs;

public sealed class CallFrameQueueTests
{
    private static byte[] Frame(byte fill) => new byte[CallService.FrameBytes].Select((_, index) => index < CallService.HeaderBytes ? (byte)index : fill).ToArray();
    [Fact]
    public async Task ClearDropsAccumulatedAudioAndAllowsTheNewGeneration()
    {
        var queue = new BoundedFrameQueue(8);
        var old = Frame(0x11); Assert.True(queue.TryEnqueue(old)); Assert.True(queue.TryEnqueue(Frame(0x12))); Assert.Equal(2, queue.Count);
        queue.Clear(); Assert.Equal(0, queue.Count);
        var replacement = Frame(0x55); Assert.True(queue.TryEnqueue(replacement));
        var received = await queue.DequeueAsync(CancellationToken.None); Assert.Same(replacement, received);
    }
    [Fact]
    public async Task CapacityIsBoundedAndDeterministic()
    {
        var queue = new BoundedFrameQueue(2); Assert.True(queue.TryEnqueue(Frame(1))); Assert.True(queue.TryEnqueue(Frame(2))); Assert.False(queue.TryEnqueue(Frame(3)));
        var first = await queue.DequeueAsync(CancellationToken.None); Assert.NotNull(first); Assert.True(queue.TryEnqueue(Frame(4)));
    }
    [Fact]
    public async Task CompleteReleasesWaitingConsumer()
    {
        var queue = new BoundedFrameQueue(1); queue.Complete();
        var result = await queue.DequeueAsync(CancellationToken.None); Assert.Null(result);
    }
}
