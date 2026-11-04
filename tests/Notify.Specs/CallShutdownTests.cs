using System.Diagnostics;
using Notify;
using Xunit;

namespace NotifySpecs;

public sealed class CallShutdownTests
{
    [Fact]
    public async Task HungAgentAndPhoneSignalsAreBoundedAndAlwaysClose()
    {
        var phoneClosed = false; var agentClosed = false; var watch = Stopwatch.StartNew();
        await CallShutdown.RunAsync(
            _ => Task.Delay(Timeout.InfiniteTimeSpan, _),
            _ => Task.Delay(Timeout.InfiniteTimeSpan, _),
            () => { phoneClosed = true; return Task.CompletedTask; },
            () => { agentClosed = true; return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(250));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(phoneClosed); Assert.True(agentClosed);
    }
    [Fact]
    public async Task ThrowingSignalsStillRunBothClosers()
    {
        var phoneClosed = false; var agentClosed = false;
        await CallShutdown.RunAsync(
            _ => throw new InvalidOperationException("agent stalled"),
            _ => throw new InvalidOperationException("phone stalled"),
            () => { phoneClosed = true; return Task.CompletedTask; },
            () => { agentClosed = true; return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(250));
        Assert.True(phoneClosed); Assert.True(agentClosed);
    }
}
