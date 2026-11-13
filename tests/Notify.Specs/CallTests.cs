using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class RecordingPush : IPushSender
{
    public List<(string Token, string CallId, string State)> Calls { get; } = [];
    public Task Send(string token, string notificationId, bool urgent, CancellationToken ct) => Task.CompletedTask;
    public Task SendCall(string token, string callId, string state, CancellationToken ct) { lock (Calls) Calls.Add((token, callId, state)); return Task.CompletedTask; }
    public bool Has(string callId, string state, string token) { lock (Calls) return Calls.Any(c => c.CallId == callId && c.State == state && c.Token == token); }
}

public sealed class FakeMediaSocket : WebSocket
{
    private readonly Channel<byte[]>? incoming;
    private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakeMediaSocket(bool blockBinary = false, bool canReceive = false)
    {
        BlockBinary = blockBinary; if (canReceive) incoming = Channel.CreateUnbounded<byte[]>();
    }
    public bool BlockBinary { get; set; }
    public bool BlockText { get; set; }
    public Task Blocked => blocked.Task;
    public void BlockSends() { BlockText = true; }
    public List<byte[]> Sent { get; } = [];
    public int CloseCalls { get; private set; }
    private WebSocketState state = WebSocketState.Open;
    public void Push(byte[] frame) => incoming!.Writer.TryWrite(frame);
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() { state = WebSocketState.Aborted; closed.TrySetResult(); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { CloseCalls++; state = WebSocketState.Closed; closed.TrySetResult(); return Task.CompletedTask; }
    public override void Dispose() { }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (incoming == null) { await Task.Delay(Timeout.Infinite, cancellationToken); }
        var frame = await incoming!.Reader.ReadAsync(cancellationToken); frame.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Binary, true);
    }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (messageType == WebSocketMessageType.Binary)
        {
            if (BlockBinary) { blocked.TrySetResult(); await blocked.Task.WaitAsync(cancellationToken); }
            Sent.Add(buffer.ToArray());
        }
        else if (BlockText) { blocked.TrySetResult(); await blocked.Task.WaitAsync(cancellationToken); }
    }
}

public sealed class CallTests
{
    private static WebSocketClient Ws(AppFactory app, string token) { var client = app.Server.CreateWebSocketClient(); client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token; return client; }
    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;
    private static Task SendText(WebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static Task SendBinary(WebSocket socket, byte[] value) => socket.SendAsync(value, WebSocketMessageType.Binary, true, CancellationToken.None);
    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAny(WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[65536]; using var stream = new MemoryStream(); WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, Encoding.UTF8.GetBytes(result.CloseStatusDescription ?? ""));
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, stream.ToArray());
    }
    private static async Task<JsonElement> ReceiveJson(WebSocket socket)
    {
        var (type, data) = await ReceiveAny(socket); Assert.Equal(WebSocketMessageType.Text, type);
        using var document = JsonDocument.Parse(data); return document.RootElement.Clone();
    }
    private static byte[] Frame(uint seq, uint timestamp, byte fill = 0x11, uint generation = 0)
    {
        var frame = new byte[CallService.FrameBytes];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), seq); BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), timestamp); BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), generation);
        Array.Fill(frame, fill, CallService.HeaderBytes, CallService.AudioBytes); return frame;
    }
    private static Agent RegisterAgent(AppFactory app, string sourceId, params string[] capabilities)
    {
        var agent = new Agent(Wire.Id(), sourceId, "Call agent", capabilities.Length == 0 ? ["realtime_audio"] : capabilities, Wire.Now);
        app.Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "agent", agent.Id, agent); return 0; });
        return agent;
    }
    private static async Task<(WebSocket Socket, JsonElement Ack)> ConnectAgentHello(AppFactory app, string token, Agent agent, string[] capabilities, string[]? callProtocols)
    {
        var socket = await Ws(app, token).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
        var hello = new Dictionary<string, object?>
        {
            ["type"] = "hello", ["protocolVersion"] = AgentHub.Protocol, ["eventId"] = Wire.Id(), ["agentId"] = agent.Id, ["name"] = "Call runtime",
            ["capabilities"] = capabilities, ["sessions"] = new[] { new { id = "task-1", name = "task-1" } }
        };
        if (callProtocols != null) hello["callProtocols"] = callProtocols;
        await SendText(socket, hello);
        return (socket, await ReceiveJson(socket));
    }
    private static async Task<WebSocket> ConnectAgent(AppFactory app, string token, Agent agent, params string[] capabilities)
    {
        return await ConnectAgentSessions(app, token, agent, ["task-1"], capabilities);
    }
    private static async Task<WebSocket> ConnectAgentSessions(AppFactory app, string token, Agent agent, string[] sessions, params string[] capabilities)
    {
        var socket = await Ws(app, token).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
        await SendText(socket, new { type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Call runtime", capabilities = capabilities.Length == 0 ? new[] { "realtime_audio" } : capabilities, callProtocols = new[] { CallService.Protocol }, sessions = sessions.Select(id => new { id, name = id }).ToArray() });
        _ = await ReceiveJson(socket);
        return socket;
    }
    private static async Task<string> WaitStatus(HttpClient client, string callId, string expected, int timeoutMs = 4000)
    {
        var end = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < end)
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/v1/calls/" + callId)); var status = document.RootElement.GetProperty("status").GetString()!;
            if (status == expected) return status;
            await Task.Delay(50);
        }
        Assert.Fail("Call did not reach " + expected);
        return "";
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < end) { if (condition()) return; await Task.Delay(25); }
        Assert.Fail("Condition was not met in time");
    }
    private static void SetFcm(AppFactory app, string deviceId, string token)
        => app.Services.GetRequiredService<Database>().Run((c, t) => { var device = Get<Device>(c, t, "device", deviceId)!; Put(c, t, "device", device.Id, device with { FcmToken = token }); return 0; });
    private static async Task<JsonElement> Dial(WebSocket control, string sessionId = "task-1", string securityLevel = "standard", string? iconId = null)
    {
        var requestId = Wire.Id();
        var payload = new Dictionary<string, string> { ["sessionId"] = sessionId, ["securityLevel"] = securityLevel, ["title"] = "Входящий звонок" };
        if (iconId != null) payload["iconId"] = iconId;
        await SendText(control, new { type = "call.dial", protocolVersion = CallService.Protocol, eventId = requestId, payload });
        using var document = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control)));
        Assert.Equal("call.dial_ack", document.RootElement.GetProperty("type").GetString()); Assert.Equal(requestId, document.RootElement.GetProperty("payload").GetProperty("requestEventId").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("owner")]
    [InlineData("source-default")]
    public async Task CallIconUsesStoredImageAndRejectsOtherSources(string selection)
    {
        using var app = new AppFactory();
        var (integration, source) = app.Integration(scopes: ["agents:connect", "icons:write"]);
        var (foreign, _) = app.Integration();
        var (phone, _, key) = app.Device();
        using (integration) using (foreign) using (phone) using (key)
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aSgAAAABJRU5ErkJggg==");
            async Task<string> Upload(HttpClient client)
            {
                using var body = new ByteArrayContent(png);
                using var response = await client.PostAsync("/api/v1/icons", body);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            }
            var owned = await Upload(integration);
            Assert.Equal(owned, await Upload(integration));
            var other = await Upload(foreign);
            var shared = await Upload(phone);
            Assert.NotEqual(owned, other);
            app.Services.GetRequiredService<Database>().Run((c, t) =>
            {
                var stored = Get<Source>(c, t, "source", source.Id)!;
                Put(c, t, "source", source.Id, stored with { IconId = shared });
                return 0;
            });
            var agent = RegisterAgent(app, source.Id);
            using var control = await ConnectAgent(app, Token(integration), agent);
            Assert.Equal("invalid_call", (await Dial(control, iconId: "../icon")).GetProperty("code").GetString());
            Assert.Equal("icon_not_found", (await Dial(control, iconId: other)).GetProperty("code").GetString());
            Assert.Equal("icon_not_found", (await Dial(control, iconId: new string('0', 64))).GetProperty("code").GetString());

            var requested = selection switch { "agent" => owned, "owner" => shared, _ => null };
            var expected = requested ?? shared;
            var ack = await Dial(control, iconId: requested);
            Assert.True(ack.GetProperty("ok").GetBoolean());
            var callId = ack.GetProperty("callId").GetString();
            using var incoming = JsonDocument.Parse(await phone.GetStringAsync("/api/v1/calls/incoming"));
            Assert.Equal(expected, incoming.RootElement.GetProperty("iconId").GetString());
            Assert.Equal(png, await phone.GetByteArrayAsync($"/api/v1/icons/{expected}"));
            using var accepted = await phone.PostAsJsonAsync($"/api/v1/calls/{callId}/accept", new { });
            accepted.EnsureSuccessStatusCode();
            Assert.Equal(expected, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("iconId").GetString());
        }
    }

    [Fact]
    public async Task AgentIncomingCallRingsAllDevicesFirstAcceptWinsAndOthersSeeCurrentState()
    {
        var push = new RecordingPush(); using var app = new AppFactory { PushSender = push };
        var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (firstClient, first, firstKey) = app.Device(); var (secondClient, second, secondKey) = app.Device();
        using (sourceClient) using (firstClient) using (secondClient) using (firstKey) using (secondKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            SetFcm(app, first.Id, "fcm-first"); SetFcm(app, second.Id, "fcm-second");
            var ack = await Dial(control); Assert.True(ack.GetProperty("ok").GetBoolean()); var callId = ack.GetProperty("callId").GetString()!; Assert.Equal("ringing", ack.GetProperty("status").GetString());
            await WaitUntil(() => push.Has(callId, "ringing", "fcm-first") && push.Has(callId, "ringing", "fcm-second"));
            using var incoming = JsonDocument.Parse(await firstClient.GetStringAsync("/api/v1/calls/incoming"));
            Assert.Equal(callId, incoming.RootElement.GetProperty("id").GetString()); Assert.Equal("incoming", incoming.RootElement.GetProperty("direction").GetString()); Assert.Equal("Входящий звонок", incoming.RootElement.GetProperty("title").GetString()); Assert.Equal(JsonValueKind.Null, incoming.RootElement.GetProperty("deviceId").ValueKind);
            var accepts = await Task.WhenAll(firstClient.PostAsJsonAsync($"/api/v1/calls/{callId}/accept", new { }), secondClient.PostAsJsonAsync($"/api/v1/calls/{callId}/accept", new { }));
            Assert.Equal(1, accepts.Count(r => r.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, accepts.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            var winner = (await (accepts.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<JsonElement>())).GetProperty("deviceId").GetString();
            Assert.Contains(winner, new[] { first.Id, second.Id });
            using var accepted = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("call.accepted", accepted.RootElement.GetProperty("type").GetString());
            await WaitUntil(() => push.Has(callId, "accepted", "fcm-first") && push.Has(callId, "accepted", "fcm-second"));
            var loser = winner == first.Id ? secondClient : firstClient;
            using var loserState = JsonDocument.Parse(await loser.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("connecting", loserState.RootElement.GetProperty("status").GetString()); Assert.Equal(winner, loserState.RootElement.GetProperty("deviceId").GetString());
            using var state = JsonDocument.Parse(await firstClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("connecting", state.RootElement.GetProperty("status").GetString()); Assert.Equal(winner, state.RootElement.GetProperty("deviceId").GetString());
            var winnerClient = winner == first.Id ? firstClient : secondClient;
            Assert.Equal(HttpStatusCode.NoContent, (await winnerClient.PostAsync($"/api/v1/calls/{callId}/end", null)).StatusCode);
            using var ended = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("call.ended", ended.RootElement.GetProperty("type").GetString());
            using var history = JsonDocument.Parse(await firstClient.GetStringAsync("/api/v1/calls")); Assert.Equal("incoming", history.RootElement[0].GetProperty("direction").GetString()); Assert.Equal(winner, history.RootElement[0].GetProperty("deviceId").GetString());
        }
    }

    [Fact]
    public async Task IncomingCallRejectBusyAndMissedAreReportedToAgentAndDevices()
    {
        var push = new RecordingPush(); using var app = new AppFactory { PushSender = push, CallRingSeconds = 1 };
        var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent); SetFcm(app, phone.Id, "fcm-phone");
            var first = await Dial(control); var firstId = first.GetProperty("callId").GetString()!;
            var busy = await Dial(control); Assert.False(busy.GetProperty("ok").GetBoolean()); Assert.Equal("busy", busy.GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await phoneClient.PostAsync($"/api/v1/calls/{firstId}/reject", null)).StatusCode);
            using var rejected = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("call.ended", rejected.RootElement.GetProperty("type").GetString()); Assert.Equal("rejected", rejected.RootElement.GetProperty("payload").GetProperty("reason").GetString());
            await WaitUntil(() => push.Has(firstId, "rejected", "fcm-phone"));
            var second = await Dial(control); var secondId = second.GetProperty("callId").GetString()!;
            await WaitUntil(() => push.Has(secondId, "ringing", "fcm-phone"));
            await WaitUntil(() => push.Has(secondId, "missed", "fcm-phone"), 3000);
            using var missed = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + secondId)); Assert.Equal("missed", missed.RootElement.GetProperty("reason").GetString());
            using var missedControl = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("missed", missedControl.RootElement.GetProperty("payload").GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task IncomingElevatedAcceptIsBoundToCallDeviceAndChallenge()
    {
        var push = new RecordingPush(); using var app = new AppFactory { PushSender = push };
        var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); var (otherClient, other, otherKey) = app.Device(); using (sourceClient) using (phoneClient) using (otherClient) using (phoneKey) using (otherKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent); SetFcm(app, phone.Id, "fcm-phone");
            var first = await Dial(control, securityLevel: "elevated"); var firstId = first.GetProperty("callId").GetString()!;
            var challengeResponse = await phoneClient.PostAsync($"/api/v1/calls/{firstId}/challenge", null); challengeResponse.EnsureSuccessStatusCode(); using var challenge = JsonDocument.Parse(await challengeResponse.Content.ReadAsStringAsync());
            using var payload = JsonDocument.Parse(Convert.FromBase64String(challenge.RootElement.GetProperty("payload").GetString()!));
            Assert.Equal(firstId, payload.RootElement.GetProperty("callId").GetString()); Assert.Equal(first.GetProperty("inviteId").GetString(), payload.RootElement.GetProperty("inviteId").GetString()); Assert.Equal("incoming", payload.RootElement.GetProperty("direction").GetString()); Assert.Equal("accept", payload.RootElement.GetProperty("action").GetString());
            var signature = Convert.ToBase64String(phoneKey.SignData(Convert.FromBase64String(challenge.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.Equal(HttpStatusCode.Forbidden, (await otherClient.PostAsJsonAsync($"/api/v1/calls/{firstId}/accept", new { challengeId = challenge.RootElement.GetProperty("id").GetString(), signature })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await phoneClient.PostAsync($"/api/v1/calls/{firstId}/reject", null)).StatusCode); _ = await ReceiveJson(control);
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated", challengeId = challenge.RootElement.GetProperty("id").GetString(), signature })).StatusCode);
            var second = await Dial(control, securityLevel: "elevated"); var secondId = second.GetProperty("callId").GetString()!;
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync($"/api/v1/calls/{secondId}/accept", new { challengeId = challenge.RootElement.GetProperty("id").GetString(), signature })).StatusCode);
            var secondChallenge = JsonDocument.Parse(await (await phoneClient.PostAsync($"/api/v1/calls/{secondId}/challenge", null)).Content.ReadAsStringAsync());
            var secondSignature = Convert.ToBase64String(phoneKey.SignData(Convert.FromBase64String(secondChallenge.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            var accepted = await phoneClient.PostAsJsonAsync($"/api/v1/calls/{secondId}/accept", new { challengeId = secondChallenge.RootElement.GetProperty("id").GetString(), signature = secondSignature }); accepted.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync($"/api/v1/calls/{secondId}/accept", new { challengeId = secondChallenge.RootElement.GetProperty("id").GetString(), signature = secondSignature })).StatusCode);
            _ = await ReceiveJson(control);
            await phoneClient.PostAsync($"/api/v1/calls/{secondId}/end", null);
        }
    }

    [Fact]
    public async Task OutgoingElevatedProofBindsAgentSessionDeviceAndIsSingleUse()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (otherSourceClient, otherSource) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); var (otherClient, other, otherKey) = app.Device(); using (sourceClient) using (otherSourceClient) using (phoneClient) using (otherClient) using (phoneKey) using (otherKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgentSessions(app, Token(sourceClient), agent, ["task-1", "task-2"]);
            var otherAgent = RegisterAgent(app, otherSource.Id); using var otherControl = await ConnectAgentSessions(app, Token(otherSourceClient), otherAgent, ["task-1"]);
            var challengeResponse = await phoneClient.PostAsJsonAsync("/api/v1/calls/challenge", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated" }); challengeResponse.EnsureSuccessStatusCode(); using var challenge = JsonDocument.Parse(await challengeResponse.Content.ReadAsStringAsync());
            var challengeId = challenge.RootElement.GetProperty("id").GetString(); var signature = Convert.ToBase64String(phoneKey.SignData(Convert.FromBase64String(challenge.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-2", securityLevel = "elevated", challengeId, signature })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = otherSource.Id, sessionId = "task-1", securityLevel = "elevated", challengeId, signature })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await otherClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated", challengeId, signature = Convert.ToBase64String(otherKey.SignData(Convert.FromBase64String(challenge.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) })).StatusCode);
            var accepted = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated", challengeId, signature }); accepted.EnsureSuccessStatusCode();
            using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()); var acceptedCallId = acceptedJson.RootElement.GetProperty("id").GetString()!; Assert.Equal("outgoing", acceptedJson.RootElement.GetProperty("direction").GetString());
            _ = await ReceiveJson(control);
            Assert.Equal(HttpStatusCode.NoContent, (await phoneClient.PostAsync($"/api/v1/calls/{acceptedCallId}/end", null)).StatusCode); _ = await ReceiveJson(control);
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated", challengeId, signature })).StatusCode);
            var expiring = JsonDocument.Parse(await (await phoneClient.PostAsJsonAsync("/api/v1/calls/challenge", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated" })).Content.ReadAsStringAsync());
            var expiringId = expiring.RootElement.GetProperty("id").GetString()!; var expiringSignature = Convert.ToBase64String(phoneKey.SignData(Convert.FromBase64String(expiring.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            app.Services.GetRequiredService<Database>().Run((c, t) => { var stored = Get<CallChallenge>(c, t, "callchallenge", expiringId)!; Put(c, t, "callchallenge", expiringId, stored with { ExpiresAt = Wire.Now - 1 }); return 0; });
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", securityLevel = "elevated", challengeId = expiringId, signature = expiringSignature })).StatusCode);
        }
    }

    [Fact]
    public async Task BargeInDropsLateOldGenerationAndPlaysTheNewOne()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            var agentMedia = await Ws(app, Token(sourceClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/agents/calls/{callId}/audio"), CancellationToken.None);
            _ = await ReceiveJson(phoneMedia); _ = await ReceiveJson(control);
            var preInterrupt = Frame(0, 0, 0x41, 0); await SendBinary(agentMedia, preInterrupt);
            await SendText(control, new { type = "call.interrupt", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId, generation = 1 } });
            var sawInterrupt = false;
            while (!sawInterrupt)
            {
                var (type, data) = await ReceiveAny(phoneMedia);
                if (type == WebSocketMessageType.Binary) Assert.Equal(preInterrupt, data);
                else { using var eventDocument = JsonDocument.Parse(data); sawInterrupt = eventDocument.RootElement.GetProperty("type").GetString() == "interrupt"; Assert.Equal(1, eventDocument.RootElement.GetProperty("generation").GetInt32()); }
            }
            var lateOld = Frame(1, 20, 0x45, 0); var fresh = Frame(2, 40, 0x55, 1);
            await SendBinary(agentMedia, lateOld); await SendBinary(agentMedia, fresh);
            var received = new List<byte[]>();
            while (received.Count == 0 || !received[^1].SequenceEqual(fresh))
            {
                var (type, data) = await ReceiveAny(phoneMedia); Assert.Equal(WebSocketMessageType.Binary, type); if (data.Length == CallService.FrameBytes) received.Add(data); else break;
            }
            Assert.DoesNotContain(received, frame => frame.SequenceEqual(lateOld));
            Assert.Contains(received, frame => frame.SequenceEqual(fresh));
            await phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null);
        }
    }

    [Fact]
    public async Task HelloNegotiatesCallProtocolAndOldAgentsStayTextCapable()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id);
            var old = await ConnectAgentHello(app, Token(sourceClient), agent, ["text_tasks", "realtime_audio"], null); using (old.Socket)
            {
                Assert.Equal(JsonValueKind.Null, old.Ack.GetProperty("callProtocol").ValueKind);
                using var agents = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/agents")); var listed = agents.RootElement[0];
                Assert.True(listed.GetProperty("online").GetBoolean()); Assert.Equal(JsonValueKind.Null, listed.GetProperty("callProtocol").ValueKind); Assert.Contains("text_tasks", listed.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
                var rejected = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode); Assert.Equal("call_protocol_unsupported", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
                var ack = await Dial(old.Socket); Assert.False(ack.GetProperty("ok").GetBoolean()); Assert.Equal("call_protocol_unsupported", ack.GetProperty("code").GetString());
                Assert.Equal(HttpStatusCode.NotFound, (await phoneClient.GetAsync("/api/v1/calls/incoming")).StatusCode);
            }
            var unknown = await ConnectAgentHello(app, Token(sourceClient), agent, ["realtime_audio"], ["alxprgs.notify.call.v9"]); using (unknown.Socket)
            {
                Assert.Equal(JsonValueKind.Null, unknown.Ack.GetProperty("callProtocol").ValueKind);
                Assert.Equal(HttpStatusCode.Conflict, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id })).StatusCode);
            }
            var both = await ConnectAgentHello(app, Token(sourceClient), agent, ["realtime_audio"], ["alxprgs.notify.call.v1", CallService.Protocol]); using (both.Socket)
            {
                Assert.Equal(CallService.Protocol, both.Ack.GetProperty("callProtocol").GetString());
                var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
                _ = await ReceiveJson(both.Socket);
                await phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null);
            }
        }
    }

    [Fact]
    public async Task HungPhoneSendIsBoundedAndNextCallCanStart()
    {
        using var app = new AppFactory { CallMediaConnectSeconds = 10 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var first = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); first.EnsureSuccessStatusCode(); var callId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = new FakeMediaSocket(); var agentMedia = new FakeMediaSocket(canReceive: true);
            var calls = app.Services.GetRequiredService<CallService>();
            var phoneAttachment = calls.AttachPhoneAsync(new Actor("device", phone.Id, []), callId, phoneMedia, CancellationToken.None);
            var agentAttachment = calls.AttachAgentAsync(new Actor("integration", source.Id, ["agents:connect"]), callId, agentMedia, CancellationToken.None);
            await WaitStatus(phoneClient, callId, "active");
            phoneMedia.BlockBinary = true; agentMedia.Push(Frame(0, 0, 0x11));
            await phoneMedia.Blocked.WaitAsync(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            var endings = await Task.WhenAll(phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null), phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null)).WaitAsync(TimeSpan.FromSeconds(10));
            watch.Stop();
            Assert.All(endings, response => Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
            await phoneAttachment.WaitAsync(TimeSpan.FromSeconds(3)); await agentAttachment.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(phoneMedia.CloseCalls >= 1); Assert.True(agentMedia.CloseCalls >= 1);
            using var state = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("ended", state.RootElement.GetProperty("status").GetString());
            var next = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); next.EnsureSuccessStatusCode();
            var nextId = (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!; _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId = nextId } });
            await WaitStatus(phoneClient, nextId, "connecting");
            await phoneClient.PostAsync($"/api/v1/calls/{nextId}/end", null);
        }
    }

    [Fact]
    public async Task DeterministicQueueOverflowEndsCallDegradedAndClosesMedia()
    {
        using var app = new AppFactory { CallQueueFrames = 1 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = new FakeMediaSocket(blockBinary: true); var agentMedia = new FakeMediaSocket(canReceive: true);
            var calls = app.Services.GetRequiredService<CallService>();
            var phoneAttachment = calls.AttachPhoneAsync(new Actor("device", phone.Id, []), callId, phoneMedia, CancellationToken.None);
            var agentAttachment = calls.AttachAgentAsync(new Actor("integration", source.Id, ["agents:connect"]), callId, agentMedia, CancellationToken.None);
            await WaitStatus(phoneClient, callId, "active");
            agentMedia.Push(Frame(0, 0, 0x11)); agentMedia.Push(Frame(1, 20, 0x22));
            await WaitStatus(phoneClient, callId, "ended");
            using var state = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("degraded", state.RootElement.GetProperty("reason").GetString());
            await Task.WhenAll(phoneAttachment, agentAttachment).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntil(() => phoneMedia.CloseCalls >= 1 && agentMedia.CloseCalls >= 1);
        }
    }

    [Fact]
    public async Task RevokedSourceClosesActiveMediaAndIsRecorded()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            var agentMedia = await Ws(app, Token(sourceClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/agents/calls/{callId}/audio"), CancellationToken.None);
            _ = await ReceiveJson(phoneMedia); _ = await ReceiveJson(control);
            await app.Services.GetRequiredService<CallService>().SourceRevokedAsync(source.Id);
            using var ended = JsonDocument.Parse(Wire.Encode(await ReceiveJson(phoneMedia))); Assert.Equal("ended", ended.RootElement.GetProperty("type").GetString()); Assert.Equal("revoked", ended.RootElement.GetProperty("reason").GetString());
            using var agentEnded = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("revoked", agentEnded.RootElement.GetProperty("payload").GetProperty("reason").GetString());
            using var state = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("revoked", state.RootElement.GetProperty("reason").GetString());
            using var history = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls")); Assert.Equal("revoked", history.RootElement[0].GetProperty("reason").GetString());
            var (phoneClose, _) = await ReceiveAny(phoneMedia); Assert.Equal(WebSocketMessageType.Close, phoneClose);
            var (agentClose, _) = await ReceiveAny(agentMedia); Assert.Equal(WebSocketMessageType.Close, agentClose);
        }
    }

    [Fact]
    public async Task RevokedDeviceEndsItsCallButNotAnotherDevicesCall()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (firstClient, first, firstKey) = app.Device(); var (secondClient, second, secondKey) = app.Device(); using (sourceClient) using (firstClient) using (secondClient) using (firstKey) using (secondKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var firstCall = await firstClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); firstCall.EnsureSuccessStatusCode(); var firstId = (await firstCall.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await app.Services.GetRequiredService<CallService>().DeviceRevokedAsync(first.Id);
            using var firstState = JsonDocument.Parse(await firstClient.GetStringAsync("/api/v1/calls/" + firstId)); Assert.Equal("revoked", firstState.RootElement.GetProperty("reason").GetString());
            using var firstEnded = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("call.ended", firstEnded.RootElement.GetProperty("type").GetString());
            var secondCall = await secondClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); secondCall.EnsureSuccessStatusCode(); var secondId = (await secondCall.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await app.Services.GetRequiredService<CallService>().DeviceRevokedAsync(first.Id);
            using var secondState = JsonDocument.Parse(await secondClient.GetStringAsync("/api/v1/calls/" + secondId)); Assert.Equal("ringing", secondState.RootElement.GetProperty("status").GetString());
            await secondClient.PostAsync($"/api/v1/calls/{secondId}/end", null);
        }
    }

    [Fact]
    public async Task CallLifecycleRelaysBinaryAudioBargeInAndHangup()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "requests:read", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var agentControl = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1" }); started.EnsureSuccessStatusCode();
            using var startedJson = JsonDocument.Parse(await started.Content.ReadAsStringAsync()); var callId = startedJson.RootElement.GetProperty("id").GetString()!; Assert.Equal("ringing", startedJson.RootElement.GetProperty("status").GetString());
            using var invite = JsonDocument.Parse(Wire.Encode(await ReceiveJson(agentControl))); Assert.Equal("call.invite", invite.RootElement.GetProperty("type").GetString()); Assert.Equal(callId, invite.RootElement.GetProperty("payload").GetProperty("callId").GetString()); Assert.Equal("task-1", invite.RootElement.GetProperty("payload").GetProperty("sessionId").GetString());
            await SendText(agentControl, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            var agentMedia = await Ws(app, Token(sourceClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/agents/calls/{callId}/audio"), CancellationToken.None);
            using var state = JsonDocument.Parse(Wire.Encode(await ReceiveJson(phoneMedia))); Assert.Equal("state", state.RootElement.GetProperty("type").GetString()); Assert.Equal("active", state.RootElement.GetProperty("status").GetString());
            using var active = JsonDocument.Parse(Wire.Encode(await ReceiveJson(agentControl))); Assert.Equal("call.active", active.RootElement.GetProperty("type").GetString());
            var outgoing = Frame(0, 0, 0x22); await SendBinary(phoneMedia, outgoing); var (agentType, agentFrame) = await ReceiveAny(agentMedia); Assert.Equal(WebSocketMessageType.Binary, agentType); Assert.Equal(outgoing, agentFrame);
            var incoming = Frame(0, 0, 0x33); await SendBinary(agentMedia, incoming); var (phoneType, phoneFrame) = await ReceiveAny(phoneMedia); Assert.Equal(WebSocketMessageType.Binary, phoneType); Assert.Equal(incoming, phoneFrame);
            await SendText(agentControl, new { type = "call.interrupt", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId, generation = 7 } });
            using var interrupt = JsonDocument.Parse(Wire.Encode(await ReceiveJson(phoneMedia))); Assert.Equal("interrupt", interrupt.RootElement.GetProperty("type").GetString()); Assert.Equal(7, interrupt.RootElement.GetProperty("generation").GetInt32());
            Assert.Equal(HttpStatusCode.NoContent, (await phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null)).StatusCode);
            using var ended = JsonDocument.Parse(Wire.Encode(await ReceiveJson(phoneMedia))); Assert.Equal("ended", ended.RootElement.GetProperty("type").GetString());
            var (phoneClose, _) = await ReceiveAny(phoneMedia); Assert.Equal(WebSocketMessageType.Close, phoneClose);
            using var agentEnded = JsonDocument.Parse(Wire.Encode(await ReceiveJson(agentControl))); Assert.Equal("call.ended", agentEnded.RootElement.GetProperty("type").GetString()); Assert.Equal("ended", agentEnded.RootElement.GetProperty("payload").GetProperty("reason").GetString());
            var (agentClose, _) = await ReceiveAny(agentMedia); Assert.Equal(WebSocketMessageType.Close, agentClose);
            using var history = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls")); var record = history.RootElement[0]; Assert.Equal(callId, record.GetProperty("id").GetString()); Assert.Equal("ended", record.GetProperty("reason").GetString()); Assert.Equal("ended", record.GetProperty("status").GetString());
            using var final = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("ended", final.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task CallRejectsOfflineUnsupportedBusyAndRequiresElevatedProof()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id);
            Assert.Equal(HttpStatusCode.Conflict, (await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id })).StatusCode);
            using (var textOnly = await ConnectAgent(app, Token(sourceClient), agent, "text_tasks"))
            {
                var unsupported = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode); Assert.Equal("agent_unsupported", (await unsupported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }
            using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); using var startedJson = JsonDocument.Parse(await started.Content.ReadAsStringAsync()); var callId = startedJson.RootElement.GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            var busy = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode); Assert.Equal("busy", (await busy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            await phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null); _ = await ReceiveJson(control);
            var missing = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, securityLevel = "elevated" }); Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            var challengeResponse = await phoneClient.PostAsJsonAsync("/api/v1/calls/challenge", new { sourceId = source.Id, securityLevel = "elevated" }); challengeResponse.EnsureSuccessStatusCode(); using var challenge = JsonDocument.Parse(await challengeResponse.Content.ReadAsStringAsync());
            var signature = Convert.ToBase64String(phoneKey.SignData(Convert.FromBase64String(challenge.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            var wrong = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, securityLevel = "elevated", challengeId = challenge.RootElement.GetProperty("id").GetString(), signature = Convert.ToBase64String(phoneKey.SignData(Encoding.UTF8.GetBytes("wrong"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            var elevated = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, securityLevel = "elevated", challengeId = challenge.RootElement.GetProperty("id").GetString(), signature });
            elevated.EnsureSuccessStatusCode(); using var elevatedJson = JsonDocument.Parse(await elevated.Content.ReadAsStringAsync()); Assert.Equal("elevated", elevatedJson.RootElement.GetProperty("securityLevel").GetString()); _ = await ReceiveJson(control);
            await phoneClient.PostAsync($"/api/v1/calls/{elevatedJson.RootElement.GetProperty("id").GetString()}/end", null);
        }
    }

    [Fact]
    public async Task RingMediaTimeoutsRejectAndOfflineDisconnectEndTheCall()
    {
        using var app = new AppFactory { CallRingSeconds = 1, CallMediaConnectSeconds = 1 }; var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await WaitStatus(phoneClient, callId, "ended", 3000);
            using var ended = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("missed", ended.RootElement.GetProperty("reason").GetString());
            using var endedControl = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("call.ended", endedControl.RootElement.GetProperty("type").GetString());
            var second = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); second.EnsureSuccessStatusCode(); var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId = secondId } });
            await WaitStatus(phoneClient, secondId, "ended", 3000);
            using var secondEnded = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + secondId)); Assert.Equal("timeout", secondEnded.RootElement.GetProperty("reason").GetString());
            _ = await ReceiveJson(control);
            var third = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); third.EnsureSuccessStatusCode(); var thirdId = (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId = thirdId } });
            await WaitStatus(phoneClient, thirdId, "connecting");
            await control.CloseAsync(WebSocketCloseStatus.NormalClosure, "offline", CancellationToken.None);
            await WaitStatus(phoneClient, thirdId, "ended", 3000);
            using var disconnected = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + thirdId)); Assert.Equal("disconnected", disconnected.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task CallProofCannotAuthorizeASeparateAction()
    {
        using var app = new AppFactory(); var (source, actor) = app.Integration(scopes: ["notify:send", "requests:create", "requests:read", "agents:connect"]); var (_, phone, key) = app.Device(); using (source) using (key)
        {
            var agent = RegisterAgent(app, actor.Id); using var control = await ConnectAgent(app, Token(source), agent); _ = control;
            var svc = app.Services.GetRequiredService<NotificationService>(); var notification = svc.Create(actor, new CreateNotification("confirmation", "Separate action", "Not approved by joining a call", "elevated", DateTimeOffset.UtcNow.AddMinutes(5)), Wire.Secret());
            using var challenge = JsonDocument.Parse(Wire.Encode(app.Services.GetRequiredService<CallService>().Challenge(phone, new CallChallengeInput(actor.Id, null, "elevated"))));
            var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(challenge.RootElement.GetProperty("payload").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.Respond(phone, notification.Id, new SubmitAnswer(new Answer("yes"), challenge.RootElement.GetProperty("id").GetString(), signature))).Status);
            Assert.Equal("pending", svc.Read(actor, notification.Id).Status);
        }
    }

    [Fact]
    public async Task AgentRejectEndsRingingCall()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.reject", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "ended");
            using var ended = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/calls/" + callId)); Assert.Equal("rejected", ended.RootElement.GetProperty("reason").GetString());
            using var controlEnded = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))); Assert.Equal("call.ended", controlEnded.RootElement.GetProperty("type").GetString()); Assert.Equal("rejected", controlEnded.RootElement.GetProperty("payload").GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task PhoneAssistantAcceptsOnlyItsConfiguredSipSourceAndAgent()
    {
        var bridgeId = Wire.Id();
        var agentId = Wire.Id();
        using var app = new AppFactory
        {
            UseSpeechStub = true, PhoneAssistantExtension = "7820", PhoneAssistantBridgeSourceId = bridgeId,
            PhoneAssistantAgentId = agentId, CallAudioStartDelayMs = 3000
        };
        var (bridgeHttp, _) = app.Integration(bridgeId, ["telephony:bridge"]);
        var (otherBridge, _) = app.Integration(scopes: ["telephony:bridge"]);
        var (agentHttp, source) = app.Integration(scopes: ["agents:connect"]);
        var (phoneHttp, _, phoneKey) = app.Device();
        using var resources = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using (bridgeHttp) using (otherBridge) using (agentHttp) using (phoneHttp) using (phoneKey)
        {
            app.Services.GetRequiredService<Database>().Run((c, t) =>
            {
                Put(c, t, "agent", agentId, new Agent(agentId, source.Id, "Phone assistant", ["realtime_audio"], Wire.Now));
                return 0;
            });
            var route = await agentHttp.GetFromJsonAsync<JsonElement>("/api/v1/telephony/assistant");
            Assert.Equal("7820", route.GetProperty("extension").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await otherBridge.GetAsync("/api/v1/telephony/assistant")).StatusCode);
            using (var stranger = await Ws(app, Token(otherBridge)).ConnectAsync(
                new Uri("ws://localhost/api/v1/telephony/connect"), resources.Token))
            {
                await SendText(stranger, new { type = "incoming", requestId = Wire.Id(), extension = "7820" });
                Assert.Equal("rejected", (await ReceiveJson(stranger)).GetProperty("code").GetString());
                await stranger.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", resources.Token);
            }

            using var control = await Ws(app, Token(bridgeHttp)).ConnectAsync(
                new Uri("ws://localhost/api/v1/telephony/connect"), resources.Token);
            await SendText(control, new { type = "incoming", requestId = Wire.Id(), extension = "7820" });
            Assert.Equal("agent_offline", (await ReceiveJson(control)).GetProperty("code").GetString());
            var registered = app.Services.GetRequiredService<AgentHub>().Get(agentId)!;
            using var agent = await ConnectAgentSessions(app, Token(agentHttp), registered, ["notify-phone-assistant"]);
            await SendText(control, new { type = "incoming", requestId = Wire.Id(), extension = "7821" });
            Assert.Equal("rejected", (await ReceiveJson(control)).GetProperty("code").GetString());
            var requestId = Wire.Id();
            await SendText(control, new { type = "incoming", requestId, extension = "7820" });
            var invite = (await ReceiveJson(agent)).GetProperty("payload");
            Assert.True(invite.GetProperty("phoneAssistant").GetBoolean());
            var callId = invite.GetProperty("callId").GetString();
            await SendText(agent, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            var accepted = await ReceiveJson(control);
            Assert.True(accepted.GetProperty("ok").GetBoolean());
            Assert.Equal(requestId, accepted.GetProperty("requestId").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await phoneHttp.GetAsync("/api/v1/calls/incoming")).StatusCode);
            using var audio = await Ws(app, Token(bridgeHttp)).ConnectAsync(new Uri(
                $"ws://localhost/api/v1/telephony/calls/{callId}/audio?inviteId={accepted.GetProperty("inviteId").GetString()}"), resources.Token);
            Assert.Equal("active", (await ReceiveJson(audio)).GetProperty("status").GetString());
            Assert.Equal("call.accepted", (await ReceiveJson(agent)).GetProperty("type").GetString());
            Assert.Equal("call.active", (await ReceiveJson(agent)).GetProperty("type").GetString());
            await SendText(control, new { type = "ended", callId, reason = "ended" });
            Assert.Equal("call.ended", (await ReceiveJson(agent)).GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task AsteriskAnnouncementHasIndependentRouteAndDelay()
    {
        using var app = new AppFactory { UseSpeechStub = true, CallAudioStartDelayMs = 150 };
        var (sourceClient, _) = app.Integration(scopes: ["calls:announce"]);
        var (bridgeClient, _) = app.Integration(scopes: ["telephony:bridge"]);
        var (phoneClient, _, phoneKey) = app.Device();
        using (sourceClient) using (bridgeClient) using (phoneClient) using (phoneKey)
        {
            const string route = "/api/v1/calls/announce";
            var input = new { title = "Home Assistant", text = "Проверка № 42: всё работает!", idempotencyKey = "asterisk-test-1", target = "asterisk" };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await sourceClient.PostAsJsonAsync(route, input)).StatusCode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var control = await Ws(app, Token(bridgeClient)).ConnectAsync(new Uri("ws://localhost/api/v1/telephony/connect"), deadline.Token);
            foreach (var unsupportedText in new[] { "Hello, world!", "Тестовый звонок из Home Assistant. ALXPRGS Notify работает!" })
            {
                using var rejected = await sourceClient.PostAsJsonAsync(route, input with { text = unsupportedText });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
                Assert.Equal("unsupported_tts_language", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }
            Assert.Empty(app.Speech.Synthesized);
            Assert.Empty(app.Services.GetRequiredService<Database>().Run((c, t) => All<CallRecord>(c, t, "call")));
            await control.SendAsync(System.Text.Encoding.UTF8.GetBytes("""{"type":"heartbeat"}"""), WebSocketMessageType.Text, true, deadline.Token);
            Assert.Equal("heartbeat_ack", (await ReceiveJson(control)).GetProperty("type").GetString());
            var started = await sourceClient.PostAsJsonAsync(route, input);
            started.EnsureSuccessStatusCode();
            Assert.Equal(input.text, Assert.Single(app.Speech.Synthesized));
            var state = await started.Content.ReadFromJsonAsync<JsonElement>();
            var id = state.GetProperty("id").GetString();
            Assert.Equal("asterisk", state.GetProperty("target").GetString());
            var invite = await ReceiveJson(control);
            Assert.Equal(id, invite.GetProperty("callId").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await phoneClient.GetAsync("/api/v1/calls/incoming")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await phoneClient.PostAsJsonAsync($"/api/v1/calls/{id}/accept", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await sourceClient.PostAsJsonAsync(route, input with { target = "android" })).StatusCode);
            var repeat = await sourceClient.PostAsJsonAsync(route, input);
            Assert.Equal(id, (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var audio = await Ws(app, Token(bridgeClient)).ConnectAsync(
                new Uri($"ws://localhost/api/v1/telephony/calls/{id}/audio?inviteId={invite.GetProperty("inviteId").GetString()}"), deadline.Token);
            Assert.Equal("active", (await ReceiveJson(audio)).GetProperty("status").GetString());
            var first = await ReceiveAny(audio, deadline.Token);
            Assert.Equal(WebSocketMessageType.Binary, first.Item1);
            Assert.True(clock.ElapsedMilliseconds >= 130);
            Assert.Equal(HttpStatusCode.Forbidden, (await sourceClient.GetAsync($"/api/v1/calls/{id}/transcript")).StatusCode);
        }
    }

    [Fact]
    public async Task AnnouncementSendsLastFrameThenEndedBeforeClose()
    {
        using var app = new AppFactory { UseSpeechStub = true };
        var (sourceClient, source) = app.Integration(scopes: ["calls:start"]);
        var (phoneClient, phone, phoneKey) = app.Device();
        using (sourceClient) using (phoneClient) using (phoneKey)
        {
            const string route = "/api/v1/calls/announce";
            var input = new
            {
                idempotencyKey = "announce-test-1",
                title = "Announcement",
                text = "Проверка завершена",
            };
            using var rejected = await sourceClient.PostAsJsonAsync(route, input with { text = "Проверка Wi-Fi" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal("unsupported_tts_language", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await phoneClient.GetAsync("/api/v1/calls/incoming")).StatusCode);
            Assert.Empty(app.Speech.Synthesized);
            var started = await sourceClient.PostAsJsonAsync(route, input);
            started.EnsureSuccessStatusCode();
            var created = await started.Content.ReadFromJsonAsync<JsonElement>();
            var callId = created.GetProperty("id").GetString()!;
            Assert.Equal("playback", created.GetProperty("mode").GetString());
            var accepted = await phoneClient.PostAsJsonAsync($"/api/v1/calls/{callId}/accept", new { });
            accepted.EnsureSuccessStatusCode();
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var binary = 0; JsonElement ended = default; WebSocketMessageType? close = null;
            while (true)
            {
                var (type, data) = await ReceiveAny(phoneMedia, deadline.Token);
                if (type == WebSocketMessageType.Binary) { binary++; continue; }
                if (type == WebSocketMessageType.Close) { close = type; break; }
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.GetProperty("type").GetString() == "ended") { ended = document.RootElement.Clone(); break; }
            }
            Assert.True(binary > 0);
            Assert.Equal("ended", ended.GetProperty("type").GetString());
            Assert.Equal("playback_sent", ended.GetProperty("reason").GetString());
            if (close != null) Assert.Equal(WebSocketMessageType.Close, close);
            await WaitStatus(phoneClient, callId, "ended");
            using var transcript = JsonDocument.Parse(await phoneClient.GetStringAsync($"/api/v1/calls/{callId}/transcript"));
            var turn = transcript.RootElement.GetProperty("turns").EnumerateArray().First();
            Assert.Equal("agent", turn.GetProperty("speaker").GetString());
            Assert.Equal("Проверка завершена", turn.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task ChatGptConversationExposesTranscriptAndServerSpeechWithoutAgent()
    {
        using var app = new AppFactory { UseSpeechStub = true };
        var (sourceClient, source) = app.Integration(scopes: ["calls:start"]);
        var (phoneClient, phone, phoneKey) = app.Device();
        using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var actor = new Actor("integration", source.Id, ["calls:start"]);
            var chat = app.Services.GetRequiredService<ChatGptCall>();
            var created = Wire.Decode<JsonElement>(await chat.StartConversation(actor, "ChatGPT call", "chatgpt-conversation-1", CancellationToken.None));
            var callId = created.GetProperty("id").GetString()!;
            var accepted = await phoneClient.PostAsJsonAsync($"/api/v1/calls/{callId}/accept", new { });
            accepted.EnsureSuccessStatusCode();
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            _ = await ReceiveJson(phoneMedia);
            var seq = 0u; var timestamp = 0u;
            for (var i = 0; i < 20; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x33)); timestamp += 20; }
            for (var i = 0; i < 40; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x00)); timestamp += 20; }
            JsonElement transcript = default;
            for (var i = 0; i < 20; i++)
            {
                transcript = JsonDocument.Parse(chat.Transcript(actor, callId)).RootElement.Clone();
                if (transcript.GetProperty("turns").GetArrayLength() > 0) break;
                await Task.Delay(100);
            }
            Assert.Equal("user", transcript.GetProperty("turns").EnumerateArray().First().GetProperty("speaker").GetString());
            using var rejected = await sourceClient.PostAsJsonAsync($"/api/v1/calls/{callId}/say", new { text = "Ответ из ChatGPT", idempotencyKey = "chatgpt-say-1" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal("unsupported_tts_language", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            var say = Wire.Decode<JsonElement>(chat.Speak(actor, callId, "Ответ из чата", "chatgpt-say-1"));
            Assert.True(say.GetProperty("ok").GetBoolean());
            var sawAgent = false; var binary = 0;
            for (var i = 0; i < 12 && (!sawAgent || binary == 0); i++)
            {
                var (type, data) = await ReceiveAny(phoneMedia);
                if (type == WebSocketMessageType.Binary) binary++;
                else if (type == WebSocketMessageType.Text)
                {
                    using var document = JsonDocument.Parse(data);
                    if (document.RootElement.TryGetProperty("turn", out var turn) && turn.GetProperty("speaker").GetString() == "agent") sawAgent = true;
                }
            }
            Assert.True(sawAgent); Assert.True(binary > 0);
            await phoneClient.PostAsync($"/api/v1/calls/{callId}/end", null);
            await WaitStatus(phoneClient, callId, "ended");
            transcript = JsonDocument.Parse(chat.Transcript(actor, callId)).RootElement.Clone();
            Assert.Equal(2, transcript.GetProperty("turns").GetArrayLength());
        }
    }

    [Fact]
    public async Task ConversationContinuesThroughDiagnosticsFromSttToAgentReplyAndSilero()
    {
        using var app = new AppFactory { UseSpeechStub = true };
        var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]);
        var (phoneClient, phone, phoneKey) = app.Device();
        using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", mode = "conversation" });
            started.EnsureSuccessStatusCode();
            var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            using (var invite = JsonDocument.Parse(Wire.Encode(await ReceiveJson(control))))
                Assert.Equal("conversation", invite.RootElement.GetProperty("payload").GetProperty("mode").GetString());
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            _ = await ReceiveJson(phoneMedia); // state active
            _ = await ReceiveJson(control); // call.active

            var seq = 0u; var timestamp = 0u;
            for (var i = 0; i < 20; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x33)); timestamp += 20; }
            for (var i = 0; i < 40; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x00)); timestamp += 20; }

            JsonElement dialog = default;
            for (var i = 0; i < 8; i++)
            {
                var frame = await ReceiveJson(control);
                if (frame.GetProperty("type").GetString() == "dialog.user") { dialog = frame; break; }
            }
            Assert.Equal("dialog.user", dialog.GetProperty("type").GetString());
            Assert.Equal("Проверь сборку", dialog.GetProperty("payload").GetProperty("text").GetString());
            var turnId = dialog.GetProperty("payload").GetProperty("turnId").GetString()!;

            var registration = await sourceClient.GetFromJsonAsync<AgentSelfResponse>("/api/v1/agents/self");
            Assert.NotNull(registration);
            Assert.Equal(agent.Id, registration.AgentId);
            Assert.Equal(source.Id, registration.SourceId);
            Assert.True(registration.Online);
            await WaitStatus(phoneClient, callId, "active");
            await SendText(control, new { type = "heartbeat", protocolVersion = AgentHub.Protocol, eventId = Wire.Id() });
            Assert.Equal("heartbeat_ack", (await ReceiveJson(control)).GetProperty("type").GetString());

            await SendText(control, new { type = "call.say", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId, turnId, text = "Сборка прошла" } });

            var sawAgentTranscript = false; var binaryFrames = 0;
            for (var i = 0; i < 12 && (!sawAgentTranscript || binaryFrames == 0); i++)
            {
                var (type, data) = await ReceiveAny(phoneMedia);
                if (type == WebSocketMessageType.Binary) binaryFrames++;
                else if (type == WebSocketMessageType.Close) Assert.Fail("phone media closed before transcript: " + Encoding.UTF8.GetString(data));
                else
                {
                    using var document = JsonDocument.Parse(data);
                    var kind = document.RootElement.GetProperty("type").GetString();
                    if (kind == "transcript")
                    {
                        var turn = document.RootElement.GetProperty("turn");
                        if (turn.GetProperty("speaker").GetString() == "agent" && turn.GetProperty("text").GetString() == "Сборка прошла") sawAgentTranscript = true;
                    }
                }
            }
            Assert.True(sawAgentTranscript);
            Assert.True(binaryFrames > 0);
            Assert.Contains("Сборка прошла", app.Speech.Synthesized);

            await SendText(control, new { type = "call.end", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "ended");
            using var transcript = JsonDocument.Parse(await phoneClient.GetStringAsync($"/api/v1/calls/{callId}/transcript"));
            var turns = transcript.RootElement.GetProperty("turns").EnumerateArray().ToArray();
            Assert.Equal(2, turns.Length);
            Assert.Equal("user", turns[0].GetProperty("speaker").GetString());
            Assert.Equal("agent", turns[1].GetProperty("speaker").GetString());
            Assert.False(transcript.RootElement.GetProperty("partial").GetBoolean());
            var stored = app.Services.GetRequiredService<Database>().Run((c, t) => Rows(c, t, "SELECT data FROM objects WHERE kind='calltranscript'").Single());
            Assert.DoesNotContain("Проверь сборку", stored);
            Assert.DoesNotContain("Сборка прошла", stored);
        }
    }

    [Fact]
    public async Task ConversationBargeInClearsGenerationAndPartialSttStaysVisible()
    {
        using var app = new AppFactory { UseSpeechStub = true };
        var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]);
        var (phoneClient, phone, phoneKey) = app.Device();
        using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id, sessionId = "task-1", mode = "conversation" });
            started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            _ = await ReceiveJson(phoneMedia); _ = await ReceiveJson(control);
            var seq = 0u; var timestamp = 0u;
            for (var i = 0; i < 20; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x33)); timestamp += 20; }
            for (var i = 0; i < 40; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x00)); timestamp += 20; }
            JsonElement dialog = default;
            for (var i = 0; i < 8; i++) { var frame = await ReceiveJson(control); if (frame.GetProperty("type").GetString() == "dialog.user") { dialog = frame; break; } }
            var turnId = dialog.GetProperty("payload").GetProperty("turnId").GetString()!;
            await SendText(control, new { type = "call.say", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId, turnId, text = "Длинный ответ" } });
            var sawAgentSpeech = false;
            for (var i = 0; i < 10 && !sawAgentSpeech; i++)
            {
                var (type, data) = await ReceiveAny(phoneMedia);
                if (type == WebSocketMessageType.Binary) sawAgentSpeech = true;
                else if (type != WebSocketMessageType.Close)
                {
                    using var document = JsonDocument.Parse(data);
                    sawAgentSpeech = document.RootElement.TryGetProperty("turn", out var turn)
                        && turn.GetProperty("speaker").GetString() == "agent";
                }
            }
            Assert.True(sawAgentSpeech);
            // A new voiced frame must interrupt the old generation and clear its queue.
            for (var i = 0; i < 20; i++) { await SendBinary(phoneMedia, Frame(seq++, timestamp, 0x44)); timestamp += 20; }
            JsonElement interrupted = default;
            for (var i = 0; i < 12; i++) { var frame = await ReceiveJson(control); if (frame.GetProperty("type").GetString() == "call.interrupted") { interrupted = frame; break; } }
            Assert.Equal("call.interrupted", interrupted.GetProperty("type").GetString());
            Assert.True(interrupted.GetProperty("payload").GetProperty("generation").GetInt32() > 1);
            await SendText(control, new { type = "call.end", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "ended");
            using var transcript = JsonDocument.Parse(await phoneClient.GetStringAsync($"/api/v1/calls/{callId}/transcript"));
            Assert.True(transcript.RootElement.GetProperty("turns").GetArrayLength() >= 2);
        }
    }

    [Fact]
    public async Task CallRejectsMalformedMediaFrame()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "agents:connect"]); var (phoneClient, phone, phoneKey) = app.Device(); using (sourceClient) using (phoneClient) using (phoneKey)
        {
            var agent = RegisterAgent(app, source.Id); using var control = await ConnectAgent(app, Token(sourceClient), agent);
            var started = await phoneClient.PostAsJsonAsync("/api/v1/calls", new { sourceId = source.Id }); started.EnsureSuccessStatusCode(); var callId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            _ = await ReceiveJson(control);
            await SendText(control, new { type = "call.accept", protocolVersion = CallService.Protocol, eventId = Wire.Id(), payload = new { callId } });
            await WaitStatus(phoneClient, callId, "connecting");
            var phoneMedia = await Ws(app, Token(phoneClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/calls/{callId}/audio"), CancellationToken.None);
            var agentMedia = await Ws(app, Token(sourceClient)).ConnectAsync(new Uri($"ws://localhost/api/v1/agents/calls/{callId}/audio"), CancellationToken.None);
            _ = await ReceiveJson(phoneMedia); _ = await ReceiveJson(control);
            await SendBinary(phoneMedia, new byte[647]);
            using var ended = JsonDocument.Parse(Wire.Encode(await ReceiveJson(phoneMedia))); Assert.Equal("ended", ended.RootElement.GetProperty("type").GetString()); Assert.Equal("invalid_audio", ended.RootElement.GetProperty("reason").GetString());
            var (agentClose, _) = await ReceiveAny(agentMedia); Assert.Equal(WebSocketMessageType.Close, agentClose);
            await WaitStatus(phoneClient, callId, "ended");
        }
    }
}
