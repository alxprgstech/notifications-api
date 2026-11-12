using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Notify;

public sealed partial class CallService
{
    internal const string BridgeScope = "telephony:bridge";

    private sealed class SipPeer(string sourceId, WebSocket socket)
    {
        public string SourceId { get; } = sourceId;
        public WebSocket Socket { get; } = socket;
        private readonly SemaphoreSlim send = new(1, 1);

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
        if (!await call.Sip.SendAsync(new { type = "invite", callId = call.Id, inviteId = call.InviteId, mode = call.Mode, expiresAt = RingDeadline(call) }))
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
            Call? active;
            lock (sync)
            {
                if (sipPeer == peer) sipPeer = null;
                active = currentId == null ? null : calls.GetValueOrDefault(currentId);
            }
            if (active?.Sip == peer) await EndAsync(active, "disconnected");
        }
    }

    private static bool GetBridgeScope(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, string sourceId) =>
        Database.Get<Source>(c, t, "source", sourceId) is { Revoked: false } source && source.Scopes.Contains(BridgeScope);

    internal async Task AttachBridgeAsync(Actor actor, string id, string inviteId, Func<Task<WebSocket>> acceptSocket, CancellationToken ct)
    {
        actor.RequireScope(BridgeScope);
        Call call;
        lock (sync)
        {
            call = calls.GetValueOrDefault(id) ?? throw new Fault(404, "call_not_found");
            if (call.Sip == null || call.Sip != sipPeer || call.Sip.SourceId != actor.Id || call.InviteId != inviteId)
                throw new Fault(404, "call_not_found");
            if (call.Status != "ringing" || Wire.Now >= RingDeadline(call)) throw new Fault(409, "call_not_ready");
            call.Status = "connecting";
        }
        _ = MediaTimerAsync(call);
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
