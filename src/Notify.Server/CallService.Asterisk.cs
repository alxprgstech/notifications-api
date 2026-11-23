using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Notify;

public sealed record PhoneAssistantRouteResponse(string Extension, bool BridgeOnline, bool AgentOnline, bool SessionReady);
public sealed record PhoneInfrastructureResponse(long CheckedAt, long ApiUptimeMs, string UptimeSource,
    string OwnExtension, PhoneAssistantRouteResponse[] Routes);

public sealed partial class CallService
{
    internal const string BridgeScope = "telephony:bridge";
    private const string PhoneAssistantSession = "notify-phone-assistant";

    internal static bool IsOrdinarySession(AgentSession[] sessions, string id) =>
        id != PhoneAssistantSession && sessions.Any(session => session.Id == id);

    // Fixed system phrases synthesized by Silero (v5.6, baya), 16 kHz mono PCM16LE.
    private static readonly Lazy<byte[]> PhoneGreeting = new(() => LoadPhonePrompt("phone-greeting"));
    private static readonly Lazy<byte[]> PhoneSpeechUnavailable = new(() => LoadPhonePrompt("phone-speech-unavailable"));

    private static byte[] LoadPhonePrompt(string name)
    {
        using var stream = typeof(CallService).Assembly.GetManifestResourceStream($"Notify.Server.CallAudio.{name}.pcm")
            ?? throw new InvalidOperationException($"Missing phone prompt {name}.");
        var pcm = new byte[stream.Length];
        stream.ReadExactly(pcm);
        return pcm;
    }

    private sealed class SipPeer(string sourceId, WebSocket socket)
    {
        public string SourceId { get; } = sourceId;
        public WebSocket Socket { get; } = socket;
        private readonly SemaphoreSlim send = new(1, 1);
        public readonly Dictionary<string, (string? Extension, object Reply)> IncomingReplies = new();

        public async Task<bool> SendAsync(object message)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await send.WaitAsync(timeout.Token);
                try
                {
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(message)), WebSocketMessageType.Text, true, timeout.Token);
                    return true;
                }
                finally { send.Release(); }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                Socket.Abort();
                return false;
            }
        }
    }

    internal void EnsureTarget(string target)
    {
        lock (sync) ResolveTarget(target);
    }

    internal void DisconnectBridge(string sourceId)
    {
        SipPeer peer;
        lock (sync)
        {
            if (sipPeer is not { } current || current.SourceId != sourceId) return;
            peer = current;
            sipPeer = null;
        }
        peer.Socket.Abort();
    }

    private SipPeer? ResolveTarget(string target) => target switch
    {
        "android" => null,
        "asterisk" => sipPeer ?? throw new Fault(503, "asterisk_unavailable"),
        _ => throw new Fault(400, "invalid_call")
    };

    private async Task InvitePhoneAsync(Call call)
    {
        if (call.Sip == null)
        {
            await PushCallAsync(call, "ringing");
            return;
        }
        if (call.Ended) return;
        if (!await call.Sip.SendAsync(new { type = "invite", callId = call.Id, inviteId = call.InviteId, mode = call.Mode, expiresAt = RingDeadline(call), trace = true }))
            await EndAsync(call, "asterisk_unavailable");
        else if (call.Ended)
            await call.Sip.SendAsync(new { type = "end", callId = call.Id });
    }

    internal async Task RunBridgeAsync(Actor actor, WebSocket socket, CancellationToken ct)
    {
        actor.RequireScope(BridgeScope);
        var peer = new SipPeer(actor.Id, socket);
        lock (sync)
        {
            if (sipPeer != null) { socket.Abort(); return; }
            sipPeer = peer;
        }
        try
        {
            if (!db.Run((c, t) => GetBridgeScope(c, t, actor.Id))) return;
            var buffer = new byte[2048];
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), heartbeat.Token);
                if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage) break;
                using var json = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
                var frame = json.RootElement;
                var type = frame.GetProperty("type").GetString();
                var enabled = db.Run((c, t) => GetBridgeScope(c, t, actor.Id));
                if (!enabled) break;
                if (type == "heartbeat")
                {
                    if (!await peer.SendAsync(new { type = "heartbeat_ack" })) break;
                    continue;
                }
                if (type == "incoming")
                {
                    var requestId = frame.GetProperty("requestId").GetString();
                    var extension = frame.GetProperty("extension").GetString();
                    if (requestId is not { Length: 32 } || !requestId.All(char.IsAsciiHexDigitLower)) break;
                    if (peer.IncomingReplies.TryGetValue(requestId, out var previous))
                    {
                        if (extension != previous.Extension || !await peer.SendAsync(previous.Reply)) break;
                        continue;
                    }
                    await HandleSipIncomingAsync(peer, requestId, extension, ct);
                    continue;
                }
                if (type == "trace")
                {
                    var callId = frame.GetProperty("callId").GetString();
                    Call? traced;
                    lock (sync) traced = callId is null ? null : calls.GetValueOrDefault(callId);
                    if (traced?.Sip == peer) BridgeTrace(traced, frame);
                    continue;
                }
                if (type != "ended") break;
                var id = frame.GetProperty("callId").GetString();
                var reason = frame.GetProperty("reason").GetString();
                if (reason is not ("busy" or "rejected" or "missed" or "ended" or "disconnected" or "asterisk_unavailable" or "degraded")) break;
                Call? call;
                lock (sync) call = id == null ? null : calls.GetValueOrDefault(id);
                if (call?.Sip == peer) await EndAsync(call, reason);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        finally
        {
            Call[] affected;
            lock (sync)
            {
                if (sipPeer == peer) sipPeer = null;
                affected = calls.Values.Where(call => call.Sip == peer && !call.Ended).ToArray();
            }
            await Task.WhenAll(affected.Select(call => EndAsync(call, "disconnected")));
            if (socket.State == WebSocketState.CloseReceived)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
            }
        }
    }

    private static bool GetBridgeScope(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, string sourceId) =>
        Database.Get<Source>(c, t, "source", sourceId) is { Revoked: false } source && source.Scopes.Contains(BridgeScope);

    internal PhoneAssistantRouteResponse PhoneAssistantRoute(Actor actor)
    {
        var route = OwnPhoneRoute(actor, settings.GetPhoneAssistantRoutes());
        return ReadPhoneRoute(route, BridgeIsOnline());
    }

    internal PhoneInfrastructureResponse PhoneInfrastructure(Actor actor, long apiUptimeMs)
    {
        var routes = settings.GetPhoneAssistantRoutes();
        var own = OwnPhoneRoute(actor, routes);
        var bridgeOnline = BridgeIsOnline();
        return new(Wire.Now, apiUptimeMs, "notify-api-process", own.Extension,
            routes.Select(route => ReadPhoneRoute(route, bridgeOnline)).ToArray());
    }

    private PhoneAssistantDestination OwnPhoneRoute(Actor actor, IEnumerable<PhoneAssistantDestination> routes)
    {
        actor.RequireScope("agents:connect");
        var agentId = agents.ForSource(actor.Id)?.Id;
        return routes.FirstOrDefault(route => route.AgentId == agentId)
            ?? throw new Fault(404, "phone_assistant_not_configured");
    }

    private bool BridgeIsOnline()
    {
        lock (sync) return sipPeer?.SourceId == settings.PhoneAssistantBridgeSourceId;
    }

    private PhoneAssistantRouteResponse ReadPhoneRoute(PhoneAssistantDestination route, bool bridgeOnline)
    {
        var online = agents.Online(route.AgentId);
        var sessionReady = online is { } live && live.CallProtocol == Protocol &&
            live.Capabilities.Contains("realtime_audio") && live.Sessions.Any(session => session.Id == PhoneAssistantSession);
        return new(route.Extension, bridgeOnline, online.HasValue, sessionReady);
    }

    private async Task HandleSipIncomingAsync(SipPeer peer, string requestId, string? extension, CancellationToken ct)
    {
        Call? call = null;
        try
        {
            var route = settings.GetPhoneAssistantRoutes().FirstOrDefault(route => route.Extension == extension);
            if (route == null || peer.SourceId != settings.PhoneAssistantBridgeSourceId)
                throw new Fault(403, "rejected");
            if (!stt.Enabled) throw new Fault(503, "stt_unavailable");
            lock (sync)
            {
                if (peer != sipPeer || phoneAssistantCallId != null ||
                    (currentId is { } id && calls[id].Sip != null)) throw new Fault(409, "busy");
                if (agents.Online(route.AgentId) is not { } online ||
                    online.CallProtocol != Protocol || !online.Capabilities.Contains("realtime_audio") ||
                    !online.Sessions.Any(session => session.Id == PhoneAssistantSession) ||
                    online.Agent.SourceId == peer.SourceId)
                    throw new Fault(409, "agent_offline");
                call = new Call(Wire.Id(), Wire.Id(), "outgoing", online.Agent.SourceId, online.Agent.Id, online.Agent.Name,
                    null, PhoneAssistantSession, "standard", "Notify Phone Assistant", null,
                    mode: "conversation", target: "asterisk", sip: peer, phoneAssistant: true);
                TrackCall(call);
                phoneAssistantCallId = call.Id;
            }
            _ = RingTimerAsync(call);
            if (!await agents.SendControlAsync(call.AgentId, "call.invite", new
            {
                callId = call.Id, inviteId = call.InviteId, direction = call.Direction, sourceId = call.SourceId,
                sessionId = call.SessionId, securityLevel = call.SecurityLevel, mode = call.Mode,
                target = call.Target, extension, phoneAssistant = true, expiresAt = RingDeadline(call)
            }, ct))
                throw new Fault(409, "agent_offline");
            if (!await call.AgentDecision!.Task.WaitAsync(TimeSpan.FromSeconds(5), ct))
                throw new Fault(409, "rejected");
            if (!await ReplyAsync(new { type = "incoming_ack", requestId, ok = true, callId = call.Id, inviteId = call.InviteId, trace = true }))
                await EndAsync(call, "disconnected");
        }
        catch (Exception error) when (error is Fault or TimeoutException or OperationCanceledException)
        {
            var code = error is Fault fault ? fault.Code : "asterisk_unavailable";
            log.LogInformation("Phone assistant SIP request {RequestId} rejected: {Code}, call={CallId}",
                requestId, code, call?.Id);
            if (call != null) await EndAsync(call, code);
            if (!ct.IsCancellationRequested)
                await ReplyAsync(new { type = "incoming_ack", requestId, ok = false, code });
        }

        Task<bool> ReplyAsync(object reply)
        {
            if (peer.IncomingReplies.Count >= 128)
                peer.IncomingReplies.Remove(peer.IncomingReplies.Keys.First());
            peer.IncomingReplies.Add(requestId, (extension, reply));
            return peer.SendAsync(reply);
        }
    }

    internal async Task AttachBridgeAsync(Actor actor, string id, string inviteId, Func<Task<WebSocket>> acceptSocket, CancellationToken ct)
    {
        actor.RequireScope(BridgeScope);
        Call call;
        lock (sync)
        {
            call = calls.GetValueOrDefault(id) ?? throw new Fault(404, "call_not_found");
            if (call.Sip == null || call.Sip != sipPeer || call.Sip.SourceId != actor.Id || call.InviteId != inviteId)
                throw new Fault(404, "call_not_found");
            if (call.PhoneAssistant ? call.Status != "connecting" || call.SipMediaClaimed
                : call.Status != "ringing" || Wire.Now >= RingDeadline(call)) throw new Fault(409, "call_not_ready");
            call.SipMediaClaimed = true;
            if (!call.PhoneAssistant) call.Status = "connecting";
        }
        if (!call.PhoneAssistant) _ = MediaTimerAsync(call);
        try
        {
            using var socket = await acceptSocket();
            if (call.UsesAgent && call.Playback.IsEmpty)
                await agents.SendControlAsync(call.AgentId, "call.accepted", new { callId = call.Id, inviteId = call.InviteId, deviceName = "GXP1400" }, ct);
            await AttachAsync(call, socket, phone: true, ct);
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException)
        {
            await EndAsync(call, "disconnected");
        }
    }
}
