using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class AgentHubHangTests
{
    private sealed class ControlledSocket : WebSocket
    {
        private readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> outgoing = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sends;
        private WebSocketState state = WebSocketState.Open;
        public int BlockAt { get; init; }
        public bool Closed { get; private set; }
        public bool Aborted { get; private set; }
        public Task Blocked => blocked.Task;
        public ChannelReader<string> Outgoing => outgoing.Reader;
        public void PushJson(string json) => inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        public void Release() => release.TrySetResult();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { Aborted = true; state = WebSocketState.Aborted; release.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { Closed = true; state = WebSocketState.Closed; release.TrySetResult(); return Task.CompletedTask; }
        public override void Dispose() { release.TrySetResult(); }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] frame;
            try { frame = await inbound.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true); }
            frame.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType != WebSocketMessageType.Text) return;
            var number = Interlocked.Increment(ref sends);
            if (BlockAt > 0 && number == BlockAt) { blocked.TrySetResult(); await release.Task.WaitAsync(cancellationToken); }
            outgoing.Writer.TryWrite(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        }
    }

    private static Agent Register(AppFactory app, string sourceId, params string[] capabilities)
    {
        var agent = new Agent(Wire.Id(), sourceId, "Blocked agent", capabilities.Length == 0 ? ["text_tasks"] : capabilities, Wire.Now);
        app.Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "agent", agent.Id, agent); return 0; });
        return agent;
    }
    private static string Hello(Agent agent) => Wire.Encode(new
    {
        type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Blocked agent",
        capabilities = new[] { "text_tasks" }, callProtocols = new[] { CallService.Protocol }, sessions = Array.Empty<object>()
    });
    private static async Task<JsonElement> Next(ControlledSocket socket, CancellationToken ct)
    {
        var text = await socket.Outgoing.ReadAsync(ct);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < until) { if (condition()) return; await Task.Delay(10); }
        Assert.Fail("Condition was not met in time");
    }
    private static async Task Finish(AgentHub hub, Agent agent, ControlledSocket socket, Task run, int timeoutMs = 3000)
    {
        try { hub.Disconnect(agent.Id, "test cleanup"); await run.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        finally { socket.Release(); }
    }

    [Fact]
    public async Task BlockedInitialQueueSendIsAbortedByDisconnectAndCommandIsResent()
    {
        using var app = new AppFactory { AgentSendTimeoutSeconds = 5 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var hub = app.Services.GetRequiredService<AgentHub>(); var db = app.Services.GetRequiredService<Database>(); var agent = Register(app, source.Id);
            var commandId = await hub.EnqueueAsync(agent, "task", new { taskId = "initial" }, 60000);
            var first = new ControlledSocket { BlockAt = 2 }; first.PushJson(Hello(agent));
            var run = hub.Run(agent, first, CancellationToken.None);
            await first.Blocked.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(hub.IsOnline(agent.Id));
            var started = DateTimeOffset.UtcNow;
            await Finish(hub, agent, first, run);
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(3));
            Assert.True(first.Closed || first.Aborted); Assert.False(hub.IsOnline(agent.Id));
            Assert.Contains(db.Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]), new[] { "queued", "sent" });

            var second = new ControlledSocket(); second.PushJson(Hello(agent));
            var rerun = hub.Run(agent, second, CancellationToken.None);
            try
            {
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var helloAck = JsonDocument.Parse(Wire.Encode(await Next(second, ready.Token))); Assert.Equal("hello_ack", helloAck.RootElement.GetProperty("type").GetString());
                using var resent = JsonDocument.Parse(Wire.Encode(await Next(second, ready.Token)));
                Assert.Equal("command", resent.RootElement.GetProperty("type").GetString()); Assert.Equal(commandId, resent.RootElement.GetProperty("eventId").GetString()); Assert.Equal("initial", resent.RootElement.GetProperty("payload").GetProperty("taskId").GetString());
                Assert.True(hub.IsOnline(agent.Id));
            }
            finally { await Finish(hub, agent, second, rerun); }
        }
    }

    [Fact]
    public async Task BlockedHelloAckIsAbortedByDisconnect()
    {
        using var app = new AppFactory { AgentSendTimeoutSeconds = 5 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var hub = app.Services.GetRequiredService<AgentHub>(); var agent = Register(app, source.Id);
            var socket = new ControlledSocket { BlockAt = 1 }; socket.PushJson(Hello(agent));
            var run = hub.Run(agent, socket, CancellationToken.None);
            await socket.Blocked.WaitAsync(TimeSpan.FromSeconds(2));
            var started = DateTimeOffset.UtcNow;
            await Finish(hub, agent, socket, run);
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(3));
            Assert.True(socket.Closed || socket.Aborted); Assert.False(hub.IsOnline(agent.Id));
        }
    }

    [Fact]
    public async Task BlockedInitialSendTimesOutWithoutDisconnect()
    {
        using var app = new AppFactory { AgentSendTimeoutSeconds = 1 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var hub = app.Services.GetRequiredService<AgentHub>(); var agent = Register(app, source.Id);
            await hub.EnqueueAsync(agent, "task", new { taskId = "timeout" }, 60000);
            var socket = new ControlledSocket { BlockAt = 2 }; socket.PushJson(Hello(agent));
            var run = hub.Run(agent, socket, CancellationToken.None);
            await socket.Blocked.WaitAsync(TimeSpan.FromSeconds(2));
            var started = DateTimeOffset.UtcNow;
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4));
            Assert.True(socket.Closed || socket.Aborted); Assert.False(hub.IsOnline(agent.Id));
        }
    }

    [Fact]
    public async Task ReplacingBlockedConnectionKeepsNewConnectionAndDeliversCommand()
    {
        using var app = new AppFactory { AgentSendTimeoutSeconds = 5 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var hub = app.Services.GetRequiredService<AgentHub>(); var agent = Register(app, source.Id);
            var commandId = await hub.EnqueueAsync(agent, "task", new { taskId = "replace" }, 60000);
            var first = new ControlledSocket { BlockAt = 2 }; first.PushJson(Hello(agent));
            var firstRun = hub.Run(agent, first, CancellationToken.None);
            await first.Blocked.WaitAsync(TimeSpan.FromSeconds(2));

            var second = new ControlledSocket(); second.PushJson(Hello(agent));
            var secondRun = hub.Run(agent, second, CancellationToken.None);
            try
            {
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _ = await Next(second, ready.Token); // hello_ack
                using var resent = JsonDocument.Parse(Wire.Encode(await Next(second, ready.Token)));
                Assert.Equal(commandId, resent.RootElement.GetProperty("eventId").GetString());
                await firstRun.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(first.Closed || first.Aborted);
                Assert.True(hub.IsOnline(agent.Id));
                Assert.False(second.Closed);
                second.PushJson(Wire.Encode(new { type = "heartbeat", protocolVersion = AgentHub.Protocol, eventId = Wire.Id() }));
                var heartbeatAck = await Next(second, ready.Token);
                Assert.Equal("heartbeat_ack", heartbeatAck.GetProperty("type").GetString());
            }
            finally { await Finish(hub, agent, second, secondRun); }
        }
    }
}
