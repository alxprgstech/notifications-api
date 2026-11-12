using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class AgentTests
{
    private static Agent Register(AppFactory app, string sourceId, params string[] capabilities)
    {
        var agent = new Agent(Wire.Id(), sourceId, "Synthetic agent", capabilities.Length == 0 ? ["text_tasks"] : capabilities, Wire.Now);
        app.Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "agent", agent.Id, agent); return 0; });
        return agent;
    }
    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;
    private static WebSocketClient Client(AppFactory app, string token)
    {
        var client = app.Server.CreateWebSocketClient(); client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token; return client;
    }
    private static Task Send(WebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Receive(WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[65536]; using var stream = new MemoryStream(); WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed by server: " + result.CloseStatus);
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray()); return document.RootElement.Clone();
    }
    private static Task<WebSocketReceiveResult> ReceiveClose(WebSocket socket, CancellationToken ct) { var buffer = new byte[4096]; return socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }

    [Fact]
    public void AgentRegistrationIsBoundToOwnerSourceAndPhoneApproval()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var web = new Actor("web", AppFactory.Owner, [], "management-session");
            var wrong = app.Integration(scopes: ["notify:send"]).actor;
            Assert.Equal(409, Assert.Throws<Fault>(() => svc.BeginManagement(web, new ManagementInput("agent.register", "Agent", wrong.Id, Capabilities: ["text_tasks"]))).Status);
            var management = svc.BeginManagement(web, new ManagementInput("agent.register", "Agent", source.Id, Capabilities: ["text_tasks", "voice_messages"]));
            Assert.Equal(409, Assert.Throws<Fault>(() => svc.BeginManagement(web, new ManagementInput("agent.register", "Agent", source.Id, Capabilities: ["text_tasks"]))).Status);
            var n = app.Services.GetRequiredService<Database>().Run((c, t) => Rows(c, t, "SELECT data FROM notifications WHERE source_id='_system'").Select(Wire.Decode<Notification>).Single());
            var ch = svc.Challenge(phone, n.Id, new Answer("yes")); var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(ch.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes"), ch.Id, signature));
            using var result = JsonDocument.Parse(Wire.Encode(svc.ManagementResult(web, management.Id))); var agentId = result.RootElement.GetProperty("result").GetProperty("agentId").GetString()!;
            var agent = app.Services.GetRequiredService<AgentHub>().Get(agentId); Assert.NotNull(agent); Assert.Equal(source.Id, agent.SourceId); Assert.Equal(new[] { "text_tasks", "voice_messages" }, agent.Capabilities);
            Assert.False(app.Services.GetRequiredService<AgentHub>().IsOnline(agentId));
        }
    }

    [Fact]
    public async Task WebSocketHelloHeartbeatDurableCommandAndAck()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var agent = Register(app, source.Id, "text_tasks"); var hub = app.Services.GetRequiredService<AgentHub>(); var db = app.Services.GetRequiredService<Database>();
            using var socket = await Client(app, Token(sourceClient)).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
            await Send(socket, new { type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Runtime", capabilities = new[] { "text_tasks" }, sessions = new[] { new { id = "task-1", name = "Build" } }, client = "tests" });
            using var hello = JsonDocument.Parse(Wire.Encode(await Receive(socket))); Assert.Equal("hello_ack", hello.RootElement.GetProperty("type").GetString()); Assert.Equal(30, hello.RootElement.GetProperty("heartbeatSeconds").GetInt32());
            await Send(socket, new { type = "heartbeat", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), sessions = new[] { new { id = "task-2", name = "New" } } });
            using var heartbeat = JsonDocument.Parse(Wire.Encode(await Receive(socket))); Assert.Equal("heartbeat_ack", heartbeat.RootElement.GetProperty("type").GetString());
            var commandId = await hub.EnqueueAsync(agent, "task", new { taskId = "task-2" }, 60000);
            using var command = JsonDocument.Parse(Wire.Encode(await Receive(socket))); Assert.Equal(commandId, command.RootElement.GetProperty("eventId").GetString()); Assert.Equal("task", command.RootElement.GetProperty("command").GetString()); Assert.Equal("task-2", command.RootElement.GetProperty("payload").GetProperty("taskId").GetString());
            Assert.Equal("sent", db.Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]));
            await Send(socket, new { type = "ack", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), commandEventId = commandId, status = "done" });
            await Task.Delay(150);
            Assert.Equal("acked", db.Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]));
            await Send(socket, new { type = "ack", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), commandEventId = commandId, status = "done" });
            await Task.Delay(100);
            Assert.Equal("acked", db.Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]));
            var (phoneClient, _, phoneKey) = app.Device(); using (phoneClient) using (phoneKey)
            {
                using var list = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/agents")); var entry = list.RootElement.EnumerateArray().Single();
                Assert.True(entry.GetProperty("online").GetBoolean()); Assert.Equal("task-2", entry.GetProperty("sessions")[0].GetProperty("id").GetString());
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    [Fact]
    public async Task SourceDiagnosticsAndLegacyProbeLeaveLiveConnectionIntact()
    {
        using var app = new AppFactory();
        var (sourceClient, source) = app.Integration(scopes: ["agents:connect"]);
        var (other, _) = app.Integration(scopes: ["agents:connect"]);
        var (unscoped, _) = app.Integration(scopes: ["notify:send"]);
        using var sourceHttp = sourceClient;
        using var otherHttp = other;
        using var unscopedHttp = unscoped;
        var agent = Register(app, source.Id);
        var hub = app.Services.GetRequiredService<AgentHub>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var address = new Uri("ws://localhost/api/v1/agents/connect");
        object Hello(string client) => new
        {
            type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id,
            name = "Runtime", capabilities = new[] { "text_tasks" }, client
        };
        using var active = await Client(app, Token(sourceClient)).ConnectAsync(address, timeout.Token);
        await Send(active, Hello("runtime"));
        Assert.Equal("hello_ack", (await Receive(active, timeout.Token)).GetProperty("type").GetString());
        var disconnects = 0;
        hub.DisconnectedEvent = _ => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; };

        var registration = await sourceClient.GetFromJsonAsync<AgentSelfResponse>("/api/v1/agents/self", timeout.Token);
        Assert.NotNull(registration);
        Assert.Equal(agent.Id, registration.AgentId);
        Assert.Equal(source.Id, registration.SourceId);
        Assert.True(registration.Online);
        using var foreign = await other.GetAsync("/api/v1/agents/self", timeout.Token);
        using var forbidden = await unscoped.GetAsync("/api/v1/agents/self", timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var probe = await Client(app, Token(sourceClient)).ConnectAsync(address, timeout.Token);
        await Send(probe, Hello("alxprgs-notify-harness-check"));
        var close = await ReceiveClose(probe, timeout.Token);
        Assert.Equal("use_agents_self_for_diagnostics", close.CloseStatusDescription);
        await probe.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        await Send(active, new { type = "heartbeat", protocolVersion = AgentHub.Protocol, eventId = Wire.Id() });
        Assert.Equal("heartbeat_ack", (await Receive(active, timeout.Token)).GetProperty("type").GetString());
        Assert.Equal(0, Volatile.Read(ref disconnects));
        Assert.True(hub.IsOnline(agent.Id));
        await active.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }

    [Fact]
    public async Task WebSocketRequiresScopeRegisteredAgentAndProtocol()
    {
        using var app = new AppFactory(); var (noScope, _) = app.Integration(scopes: ["notify:send"]); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (noScope) using (sourceClient)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await noScope.GetAsync("/api/v1/agents/connect")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await sourceClient.GetAsync("/api/v1/agents/connect")).StatusCode);
            var agent = Register(app, source.Id);
            Assert.Equal(HttpStatusCode.BadRequest, (await sourceClient.GetAsync("/api/v1/agents/connect")).StatusCode);
            using var socket = await Client(app, Token(sourceClient)).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
            await Send(socket, new { type = "hello", protocolVersion = "alxprgs.notify.agent.v0", eventId = Wire.Id(), agentId = agent.Id, name = "Runtime", capabilities = Array.Empty<string>(), sessions = Array.Empty<object>() });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var result = await ReceiveClose(socket, timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType); Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
        }
    }

    [Fact]
    public async Task MissingHeartbeatClosesSessionAndOfflineAgentQueuesCommands()
    {
        using var app = new AppFactory { AgentHeartbeatSeconds = 1, AgentOfflineSeconds = 2 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var agent = Register(app, source.Id); var hub = app.Services.GetRequiredService<AgentHub>();
            using (var socket = await Client(app, Token(sourceClient)).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None))
            {
                await Send(socket, new { type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Runtime", capabilities = new[] { "text_tasks" }, sessions = Array.Empty<object>() });
                _ = await Receive(socket);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)); var result = await ReceiveClose(socket, timeout.Token);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType); Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus); Assert.Contains("heartbeat", result.CloseStatusDescription);
            }
            await Task.Delay(250);
            Assert.False(hub.IsOnline(agent.Id));
            var commandId = await hub.EnqueueAsync(agent, "task", new { taskId = "offline" }, 1000);
            Assert.Equal("queued", app.Services.GetRequiredService<Database>().Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]));
            await Task.Delay(1100); hub.Sweep();
            Assert.Equal("expired", app.Services.GetRequiredService<Database>().Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]));
        }
    }

    [Fact]
    public async Task UnacknowledgedCommandIsResentAfterReconnectWithSameEventId()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); using (sourceClient)
        {
            var agent = Register(app, source.Id); var hub = app.Services.GetRequiredService<AgentHub>(); var db = app.Services.GetRequiredService<Database>();
            var first = await Client(app, Token(sourceClient)).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
            await Send(first, new { type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Runtime", capabilities = new[] { "text_tasks" }, sessions = Array.Empty<object>() });
            _ = await Receive(first);
            var commandId = await hub.EnqueueAsync(agent, "task", new { taskId = "retry" }, 60000, correlationId: "corr-1", sessionId: "task-1");
            using (var sent = JsonDocument.Parse(Wire.Encode(await Receive(first)))) Assert.Equal(commandId, sent.RootElement.GetProperty("eventId").GetString());
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "drop", CancellationToken.None); first.Dispose();
            await Task.Delay(250);
            using var second = await Client(app, Token(sourceClient)).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
            await Send(second, new { type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Runtime", capabilities = new[] { "text_tasks" }, sessions = Array.Empty<object>() });
            _ = await Receive(second);
            using var resent = JsonDocument.Parse(Wire.Encode(await Receive(second))); Assert.Equal(commandId, resent.RootElement.GetProperty("eventId").GetString()); Assert.Equal("retry", resent.RootElement.GetProperty("payload").GetProperty("taskId").GetString()); Assert.Equal("corr-1", resent.RootElement.GetProperty("correlationId").GetString()); Assert.Equal("task-1", resent.RootElement.GetProperty("sessionId").GetString());
            await Send(second, new { type = "ack", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), commandEventId = commandId, status = "done" });
            await Task.Delay(150);
            Assert.Equal("acked", db.Run((c, t) => Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", commandId))[0]));
            await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    [Fact]
    public void InvalidAgentManagementInputIsRejected()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var web = new Actor("web", AppFactory.Owner, [], "management-session");
            Assert.Equal(400, Assert.Throws<Fault>(() => svc.BeginManagement(web, new ManagementInput("agent.register", "Agent", source.Id, Capabilities: []))).Status);
            Assert.Equal(400, Assert.Throws<Fault>(() => svc.BeginManagement(web, new ManagementInput("agent.register", "Agent", source.Id, Capabilities: ["unknown"]))).Status);
            Assert.Equal(400, Assert.Throws<Fault>(() => svc.BeginManagement(web, new ManagementInput("agent.register", "Agent", source.Id, Capabilities: ["text_tasks", "text_tasks"]))).Status);
            Assert.Equal(400, Assert.Throws<Fault>(() => svc.BeginManagement(web, new ManagementInput("agent.register", "", source.Id, Capabilities: ["text_tasks"]))).Status);
        }
    }
}
