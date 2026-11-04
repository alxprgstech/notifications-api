using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Notify.Database;

namespace Notify;

// Signaling over the agent control socket, media over two outbound WebSockets. Audio is relayed in
// memory only: no STT/TTS, no transcoding, no durable queues, no recordings.
public sealed class CallService
{
    public const string Protocol = "alxprgs.notify.call.v2";
    public static readonly string[] SupportedProtocols = [Protocol];
    public const int AudioBytes = 640;
    public const int HeaderBytes = 12;
    public const int FrameBytes = HeaderBytes + AudioBytes;
    private readonly AgentHub agents; private readonly Database db; private readonly Settings settings; private readonly ILogger<CallService> log; private readonly IPushSender push;
    private readonly object sync = new();
    private readonly Dictionary<string, Call> calls = new();
    private string? currentId;

    public CallService(AgentHub agents, Database db, Settings settings, ILogger<CallService> log, IPushSender push)
    {
        this.agents = agents; this.db = db; this.settings = settings; this.log = log; this.push = push;
        agents.ControlEvent += HandleAgentFrame; agents.DisconnectedEvent += HandleAgentDisconnected;
    }

    private sealed class Call(string id, string inviteId, string direction, string sourceId, string agentId, string agentName, string? deviceId, string? sessionId, string securityLevel, string title)
    {
        public string Id { get; } = id;
        public string InviteId { get; } = inviteId;
        public string Direction { get; } = direction;
        public string SourceId { get; } = sourceId;
        public string AgentId { get; } = agentId;
        public string AgentName { get; } = agentName;
        public string? DeviceId { get; set; } = deviceId;
        public string? SessionId { get; } = sessionId;
        public string SecurityLevel { get; } = securityLevel;
        public string Title { get; } = title;
        public long StartedAt { get; } = Wire.Now; public long? ActiveAt, EndedAt; public string Status = "ringing"; public string? Reason;
        public int PlaybackGeneration;
        public bool Ended; public CancellationTokenSource Cts { get; } = new(); public MediaSide? Phone, AgentMedia;
    }
    private sealed class MediaSide(WebSocket socket, CancellationToken lifetime, int queueFrames, string name)
    {
        public WebSocket Socket { get; } = socket;
        public string Name { get; } = name;
        public BoundedFrameQueue Outbound { get; } = new(queueFrames);
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastSeq = -1, LastTimestamp = -1;
        public async Task<bool> TextAsync(string json, CancellationToken ct = default)
        {
            if (!await SendLock.WaitAsync(TimeSpan.FromSeconds(2))) return false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (Socket.State != WebSocketState.Open) return false;
                await Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token).WaitAsync(timeout.Token);
                return true;
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                try { Socket.Abort(); } catch { }
                return false;
            }
            finally { SendLock.Release(); }
        }
    }

    public object Challenge(Actor actor, CallChallengeInput input)
    {
        actor.RequireDevice();
        if (input.SecurityLevel != "elevated") throw new Fault(400, "call_verification_not_required");
        if (input.SourceId.Length is < 1 or > 64 || input.SessionId?.Length > 64 || input.SessionId?.Any(char.IsControl) == true) throw new Fault(400, "invalid_call");
        var agent = agents.ForSource(input.SourceId) ?? throw new Fault(404, "agent_not_registered");
        var online = agents.Online(agent.Id) ?? throw new Fault(409, "agent_offline");
        if (!online.Capabilities.Contains("realtime_audio", StringComparer.Ordinal)) throw new Fault(409, "agent_unsupported");
        if (online.CallProtocol != Protocol) throw new Fault(409, "call_protocol_unsupported");
        if (input.SessionId != null && online.Sessions.All(s => s.Id != input.SessionId)) throw new Fault(400, "invalid_session");
        return CreateChallenge(actor, "outgoing", "start", input.SourceId, agent.Id, input.SessionId, null, null, "elevated");
    }
    public object ChallengeIncoming(Actor actor, string callId)
    {
        actor.RequireDevice();
        Call? call; lock (sync) calls.TryGetValue(callId, out call);
        if (call == null) throw new Fault(404, "call_not_found");
        if (call.Direction != "incoming") throw new Fault(400, "invalid_call");
        lock (sync)
        {
            if (call.Ended || call.Status != "ringing" || call.DeviceId != null) throw new Fault(409, "call_taken");
            if (call.SecurityLevel != "elevated") throw new Fault(409, "call_verification_not_required");
        }
        return CreateChallenge(actor, "incoming", "accept", call.SourceId, call.AgentId, call.SessionId, call.Id, call.InviteId, "elevated");
    }
    private object CreateChallenge(Actor actor, string direction, string action, string sourceId, string agentId, string? sessionId, string? callId, string? inviteId, string securityLevel)
    {
        var id = Wire.Id(); var expires = Wire.Now + 90000;
        var proof = new CallChallengeProof(Protocol, id, actor.Id, direction, action, sourceId, agentId, sessionId, callId, inviteId, securityLevel, Wire.Secret(), expires);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Wire.Encode(proof)));
        db.Run((c, t) => { Put(c, t, "callchallenge", id, new CallChallenge(id, actor.Id, payload, expires)); return 0; });
        return new { id, payload, expiresAt = expires };
    }

    public async Task<object> StartAsync(Actor actor, CallStart input, CancellationToken ct)
    {
        actor.RequireDevice();
        if (input.SecurityLevel is not ("standard" or "elevated") || input.SourceId.Length is < 1 or > 64 || input.SessionId?.Length > 64 || input.SessionId?.Any(char.IsControl) == true) throw new Fault(400, "invalid_call");
        var agent = agents.ForSource(input.SourceId) ?? throw new Fault(404, "agent_not_registered");
        var online = agents.Online(agent.Id) ?? throw new Fault(409, "agent_offline");
        if (!online.Capabilities.Contains("realtime_audio", StringComparer.Ordinal)) throw new Fault(409, "agent_unsupported");
        if (online.CallProtocol != Protocol) throw new Fault(409, "call_protocol_unsupported");
        if (input.SessionId != null && online.Sessions.All(s => s.Id != input.SessionId)) throw new Fault(400, "invalid_session");
        lock (sync) { if (currentId != null) throw new Fault(409, "busy"); }
        if (input.SecurityLevel == "elevated") VerifyChallenge(actor, input.ChallengeId, input.Signature, "outgoing", "start", input.SourceId, agent.Id, input.SessionId, null, null, "elevated");
        else if (input.ChallengeId != null || input.Signature != null) throw new Fault(400, "invalid_call");
        Call call;
        lock (sync)
        {
            if (currentId != null) throw new Fault(409, "busy");
            call = new Call(Wire.Id(), Wire.Id(), "outgoing", input.SourceId, agent.Id, online.Agent.Name, actor.Id, input.SessionId, input.SecurityLevel, "Звонок");
            calls[call.Id] = call; currentId = call.Id;
        }
        _ = RingTimerAsync(call);
        var invited = await agents.SendControlAsync(call.AgentId, "call.invite", new
        {
            callId = call.Id, inviteId = call.InviteId, direction = call.Direction, sourceId = call.SourceId, sessionId = call.SessionId, securityLevel = call.SecurityLevel,
            expiresAt = RingDeadline(call)
        }, ct);
        if (!invited) { await EndAsync(call, "agent_offline"); throw new Fault(409, "agent_offline"); }
        return View(call);
    }

    public async Task<object> AcceptAsync(Actor actor, string id, CallAcceptInput input)
    {
        actor.RequireDevice();
        Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null) throw new Fault(404, "call_not_found");
        if (call.Direction != "incoming") throw new Fault(400, "invalid_call");
        if (call.SecurityLevel == "elevated") VerifyChallenge(actor, input.ChallengeId, input.Signature, "incoming", "accept", call.SourceId, call.AgentId, call.SessionId, call.Id, call.InviteId, call.SecurityLevel);
        else if (input.ChallengeId != null || input.Signature != null) throw new Fault(400, "invalid_call");
        string deviceName = db.Run((c, t) => Get<Device>(c, t, "device", actor.Id)?.Name ?? "Android");
        lock (sync)
        {
            if (call.Ended) throw new Fault(404, "call_not_found");
            if (call.Status != "ringing" || call.DeviceId != null) throw new Fault(409, "call_taken");
            if (Wire.Now >= RingDeadline(call)) throw new Fault(409, "call_missed");
            call.DeviceId = actor.Id; call.Status = "connecting";
        }
        _ = MediaTimerAsync(call);
        await agents.SendControlAsync(call.AgentId, "call.accepted", new { callId = call.Id, inviteId = call.InviteId, deviceId = actor.Id, deviceName });
        _ = PushCallAsync(call, "accepted");
        return View(call);
    }
    public async Task RejectAsync(Actor actor, string id)
    {
        actor.RequireDevice();
        Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.Direction != "incoming") throw new Fault(404, "call_not_found");
        lock (sync) { if (call.Ended || call.Status != "ringing" || call.DeviceId != null) throw new Fault(409, "call_taken"); }
        await EndAsync(call, "rejected");
    }

    private async Task DialAsync(Agent agent, JsonElement frame, JsonElement payload)
    {
        var requestEventId = frame.TryGetProperty("eventId", out var eventId) ? eventId.GetString() : null;
        if (requestEventId is not { Length: 32 }) return;
        var online = agents.Online(agent.Id);
        if (online == null) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "agent_offline" }); return; }
        if (!online.Value.Capabilities.Contains("realtime_audio", StringComparer.Ordinal)) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "agent_unsupported" }); return; }
        if (online.Value.CallProtocol != Protocol) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "call_protocol_unsupported" }); return; }
        var security = payload.TryGetProperty("securityLevel", out var level) ? level.GetString() : "standard";
        var sessionId = payload.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
        var title = payload.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
        if (security is not ("standard" or "elevated") || sessionId?.Length > 64 || sessionId?.Any(char.IsControl) == true || title?.Length > 160 || title?.Any(char.IsControl) == true)
        {
            await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "invalid_call" }); return;
        }
        if (sessionId != null && online.Value.Sessions.All(s => s.Id != sessionId))
        {
            await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "invalid_session" }); return;
        }
        Call? call = null;
        lock (sync)
        {
            if (currentId == null)
            {
                call = new Call(Wire.Id(), Wire.Id(), "incoming", agent.SourceId, agent.Id, online.Value.Agent.Name, null, sessionId, security, string.IsNullOrWhiteSpace(title) ? online.Value.Agent.Name : title);
                calls[call.Id] = call; currentId = call.Id;
            }
        }
        if (call == null) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "busy" }); return; }
        var ack = await agents.SendControlAsync(call.AgentId, "call.dial_ack", new { requestEventId, ok = true, callId = call.Id, inviteId = call.InviteId, status = "ringing", expiresAt = RingDeadline(call) });
        if (!ack) { await EndAsync(call, "agent_offline"); return; }
        _ = RingTimerAsync(call);
        _ = PushCallAsync(call, "ringing");
    }

    public object Incoming(Actor actor)
    {
        actor.RequireDevice();
        Call? call; lock (sync) call = calls.Values.FirstOrDefault(c => !c.Ended && c.Direction == "incoming" && c.Status == "ringing");
        if (call == null) throw new Fault(404, "no_incoming_call");
        return View(call);
    }
    public object State(Actor actor, string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)) throw new Fault(404, "call_not_found");
        Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call != null) { RequireParticipant(actor, call); return View(call); }
        var record = db.Run((c, t) => Get<CallRecord>(c, t, "call", id)) ?? throw new Fault(404, "call_not_found");
        RequireParticipant(actor, record);
        return record;
    }
    public List<CallRecord> History(Actor actor)
    {
        actor.RequireOwner();
        return db.Run((c, t) => All<CallRecord>(c, t, "call").OrderByDescending(c => c.StartedAt).Take(100).ToList());
    }
    public async Task EndByDeviceAsync(Actor actor, string id)
    {
        actor.RequireDevice(); Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.DeviceId != actor.Id) throw new Fault(404, "call_not_found");
        await EndAsync(call, "ended");
    }
    public async Task SourceRevokedAsync(string sourceId)
    {
        try
        {
            Call? call; lock (sync) call = calls.Values.FirstOrDefault(c => c.SourceId == sourceId && !c.Ended);
            if (call != null) await EndAsync(call, "revoked");
        }
        catch (Exception e) { log.LogWarning("Source revoke call cleanup failed: {Type}", e.GetType().Name); }
    }
    public async Task DeviceRevokedAsync(string deviceId)
    {
        try
        {
            Call? call; lock (sync) call = calls.Values.FirstOrDefault(c => c.DeviceId == deviceId && !c.Ended);
            if (call != null) await EndAsync(call, "revoked");
        }
        catch (Exception e) { log.LogWarning("Device revoke call cleanup failed: {Type}", e.GetType().Name); }
    }

    public async Task AttachPhoneAsync(Actor actor, string id, WebSocket socket, CancellationToken ct)
    {
        RequirePhone(actor, id);
        Call? found; lock (sync) calls.TryGetValue(id, out found); var call = found!;
        await AttachAsync(call, socket, phone: true, ct);
    }
    public void RequirePhone(Actor actor, string id)
    {
        actor.RequireDevice(); Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.DeviceId != actor.Id) throw new Fault(404, "call_not_found");
        lock (sync) { if (call.Ended || call.Status is not ("connecting" or "active")) throw new Fault(409, "call_not_ready"); if (call.Phone != null) throw new Fault(409, "already_attached"); }
    }
    public async Task AttachAgentAsync(Actor actor, string id, WebSocket socket, CancellationToken ct)
    {
        RequireAgent(actor, id);
        Call? found; lock (sync) calls.TryGetValue(id, out found); var call = found!;
        await AttachAsync(call, socket, phone: false, ct);
    }
    public void RequireAgent(Actor actor, string id)
    {
        if (actor.Kind != "integration" || !actor.Scopes.Contains("agents:connect")) throw new Fault(403, "agents_scope_required");
        var agent = agents.ForSource(actor.Id) ?? throw new Fault(403, "agent_not_registered");
        Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.AgentId != agent.Id) throw new Fault(404, "call_not_found");
        lock (sync) { if (call.Ended || call.Status is not ("connecting" or "active")) throw new Fault(409, "call_not_ready"); if (call.AgentMedia != null) throw new Fault(409, "already_attached"); }
    }

    private async Task AttachAsync(Call call, WebSocket socket, bool phone, CancellationToken ct)
    {
        var side = new MediaSide(socket, call.Cts.Token, settings.CallQueueFrames, phone ? "phone" : "agent");
        bool both;
        lock (sync)
        {
            if (call.Ended) throw new Fault(409, "call_ended");
            if (call.Status is not ("connecting" or "active")) throw new Fault(409, "call_not_ready");
            if (phone ? call.Phone != null : call.AgentMedia != null) throw new Fault(409, "already_attached");
            if (phone) call.Phone = side; else call.AgentMedia = side;
            both = call.Phone != null && call.AgentMedia != null;
            if (both && call.Status == "connecting") { call.Status = "active"; call.ActiveAt = Wire.Now; }
        }
        _ = SendLoopAsync(call, side);
        _ = ReceiveLoopAsync(call, side, phone);
        log.LogInformation("Call {CallId} media attached: {Side} status={Status}", call.Id, phone ? "phone" : "agent", call.Status);
        if (both)
        {
            _ = MaxTimerAsync(call);
            if (call.Phone != null) await call.Phone.TextAsync(Wire.Encode(new { type = "state", status = "active" }));
            await agents.SendControlAsync(call.AgentId, "call.active", new { callId = call.Id, inviteId = call.InviteId });
        }
        await side.Closed.Task;
    }

    private async Task ReceiveLoopAsync(Call call, MediaSide side, bool phone)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!side.Cts.IsCancellationRequested && side.Socket.State == WebSocketState.Open)
            {
                var result = await side.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), side.Cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) { await EndAsync(call, "disconnected"); return; }
                if (result.MessageType != WebSocketMessageType.Binary || !result.EndOfMessage || !ValidFrame(buffer, result.Count, side, out var generation)) { await EndAsync(call, "invalid_audio"); return; }
                bool active; lock (sync) active = call.Status == "active" && !call.Ended;
                if (!active) continue;
                if (!phone && generation < (uint)call.PlaybackGeneration) continue;
                var peer = phone ? call.AgentMedia : call.Phone;
                var frame = new byte[FrameBytes]; Buffer.BlockCopy(buffer, 0, frame, 0, FrameBytes);
                if (peer == null || !peer.Outbound.TryEnqueue(frame)) { await EndAsync(call, "degraded"); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e) { log.LogWarning("Call {CallId} media receive closed on {Side}: {Message} state={State} close={Close}", call.Id, side.Name, e.Message, side.Socket.State, side.Socket.CloseStatus); await EndAsync(call, "disconnected"); }
        catch (Exception e) { log.LogWarning("Call {CallId} media receive failed: {Type}", call.Id, e.GetType().Name); await EndAsync(call, "disconnected"); }
        finally { side.Closed.TrySetResult(); }
    }
    private async Task SendLoopAsync(Call call, MediaSide side)
    {
        try
        {
            while (true)
            {
                var frame = await side.Outbound.DequeueAsync(side.Cts.Token);
                if (frame == null) break;
                await side.SendLock.WaitAsync(side.Cts.Token);
                try
                {
                    if (side.Name == "phone" && TryGeneration(frame, out var generation) && generation < (uint)call.PlaybackGeneration) continue;
                    if (side.Socket.State == WebSocketState.Open) await side.Socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, side.Cts.Token);
                }
                finally { side.SendLock.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogWarning("Call {CallId} media send failed on {Side}: {Type} state={State} close={Close} message={Message}", call.Id, side.Name, e.GetType().Name, side.Socket.State, side.Socket.CloseStatus, e.Message); await EndAsync(call, "disconnected"); }
        finally { side.Closed.TrySetResult(); }
    }
    private static bool ValidFrame(byte[] buffer, int count, MediaSide side, out uint generation)
    {
        generation = 0;
        if (count != FrameBytes) return false;
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4)); var timestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)); generation = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(8, 4));
        if (side.LastSeq >= 0 && seq != (uint)(side.LastSeq + 1)) return false;
        if (side.LastTimestamp >= 0 && timestamp < side.LastTimestamp) return false;
        side.LastSeq = seq; side.LastTimestamp = timestamp; return true;
    }
    private static bool TryGeneration(byte[] frame, out uint generation) { generation = 0; if (frame.Length != FrameBytes) return false; generation = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8, 4)); return true; }

    private async Task HandleAgentFrame(Agent agent, JsonElement frame)
    {
        try
        {
            if (!frame.TryGetProperty("protocolVersion", out var version) || version.GetString() != Protocol) return;
            var type = frame.GetProperty("type").GetString();
            if (!frame.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            if (type == "call.dial") { await DialAsync(agent, frame, payload); return; }
            var callId = payload.TryGetProperty("callId", out var value) ? value.GetString() : null;
            if (callId == null) return;
            Call? call; lock (sync) calls.TryGetValue(callId, out call);
            if (call == null || call.AgentId != agent.Id) return;
            switch (type)
            {
                case "call.accept": await AcceptByAgentAsync(call); break;
                case "call.reject": await EndAsync(call, "rejected"); break;
                case "call.end": await EndAsync(call, "ended"); break;
                case "call.interrupt":
                    if (!payload.TryGetProperty("generation", out var generation) || !generation.TryGetInt32(out var number) || number < 0) return;
                    await InterruptAsync(call, number);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { log.LogDebug("Ignoring malformed agent call event"); }
    }
    private async Task AcceptByAgentAsync(Call call)
    {
        lock (sync)
        {
            if (call.Ended || call.Status != "ringing" || call.Direction != "outgoing") return;
            call.Status = "connecting";
        }
        _ = MediaTimerAsync(call);
        await Task.CompletedTask;
    }
    private async Task InterruptAsync(Call call, int generation)
    {
        MediaSide? phone;
        lock (sync)
        {
            if (call.Ended || call.Status != "active" || generation <= call.PlaybackGeneration) return;
            call.PlaybackGeneration = generation; phone = call.Phone;
        }
        if (phone == null) return;
        phone.Outbound.Clear();
        await phone.TextAsync(Wire.Encode(new { type = "interrupt", generation }));
    }
    private async Task HandleAgentDisconnected(Agent agent)
    {
        Call? call; lock (sync) call = calls.Values.FirstOrDefault(c => c.AgentId == agent.Id && !c.Ended);
        if (call != null) await EndAsync(call, "disconnected");
    }

    private async Task RingTimerAsync(Call call)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(settings.CallRingSeconds), call.Cts.Token); } catch (OperationCanceledException) { return; }
        lock (sync) { if (call.Ended || call.Status != "ringing") return; }
        await EndAsync(call, "missed");
    }
    private async Task MediaTimerAsync(Call call)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(settings.CallMediaConnectSeconds), call.Cts.Token); } catch (OperationCanceledException) { return; }
        lock (sync) { if (call.Ended || call.Status != "connecting") return; }
        await EndAsync(call, "timeout");
    }
    private async Task MaxTimerAsync(Call call)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(settings.CallMaxMinutes), call.Cts.Token); } catch (OperationCanceledException) { return; }
        lock (sync) { if (call.Ended || call.Status != "active") return; }
        await EndAsync(call, "timeout");
    }

    private async Task EndAsync(Call call, string reason)
    {
        MediaSide? phone, agentMedia; string? deviceId;
        lock (sync)
        {
            if (call.Ended) return;
            call.Ended = true; call.Status = "ended"; call.Reason = reason; call.EndedAt = Wire.Now;
            if (currentId == call.Id) currentId = null;
            phone = call.Phone; agentMedia = call.AgentMedia; deviceId = call.DeviceId;
        }
        call.Cts.Cancel();
        log.LogInformation("Call {CallId} ({Direction}) ended: {Reason}", call.Id, call.Direction, reason);
        try
        {
            try
            {
                var duration = call.ActiveAt is { } active ? (int)Math.Min(int.MaxValue, call.EndedAt!.Value - active) : 0;
                db.Run((c, t) => { Put(c, t, "call", call.Id, new CallRecord(call.Id, call.SourceId, call.AgentId, deviceId, call.SessionId, call.SecurityLevel, call.StartedAt, call.EndedAt!.Value, "ended", reason, duration, call.Direction)); return 0; });
            }
            catch (Exception e) { log.LogWarning("Call {CallId} history write failed: {Type}", call.Id, e.GetType().Name); }
            if (call.Direction == "incoming") _ = PushCallAsync(call, reason is "rejected" or "missed" or "disconnected" or "revoked" ? reason : "ended");
            await CallShutdown.RunAsync(
                ct => agents.SendControlAsync(call.AgentId, "call.ended", new { callId = call.Id, inviteId = call.InviteId, reason }, ct),
                ct => phone?.TextAsync(Wire.Encode(new { type = "ended", reason }), ct) ?? Task.CompletedTask,
                () => CloseSide(phone, reason), () => CloseSide(agentMedia, reason),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            lock (sync) calls.Remove(call.Id);
        }
    }
    private static async Task CloseSide(MediaSide? side, string reason)
    {
        if (side == null) return;
        side.Outbound.Complete(); side.Cts.Cancel();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (side.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await side.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason.Length > 100 ? reason[..100] : reason, cts.Token);
        }
        catch { }
        try { side.Socket.Abort(); } catch { }
        side.Socket.Dispose();
    }
    private async Task PushCallAsync(Call call, string state)
    {
        try
        {
            var tokens = db.Run((c, t) => All<Device>(c, t, "device").Where(d => !d.Revoked && d.FcmToken != null).Select(d => d.FcmToken!).ToList());
            if (tokens.Count == 0) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var token in tokens)
            {
                try { await push.SendCall(token, call.Id, state, cts.Token); }
                catch (Exception e) { log.LogDebug("Call push failed: {Type}", e.GetType().Name); }
            }
        }
        catch (Exception e) { log.LogDebug("Call push skipped: {Type}", e.GetType().Name); }
    }
    private void VerifyChallenge(Actor actor, string? challengeId, string? signature, string direction, string action, string sourceId, string agentId, string? sessionId, string? callId, string? inviteId, string securityLevel)
    {
        if (challengeId == null || signature == null) throw new Fault(403, "call_verification_required");
        db.Run((c, t) =>
        {
            var challenge = Get<CallChallenge>(c, t, "callchallenge", challengeId) ?? throw new Fault(403, "invalid_challenge");
            if (challenge.DeviceId != actor.Id || challenge.ExpiresAt <= Wire.Now) throw new Fault(403, "invalid_challenge");
            CallChallengeProof proof;
            try { proof = Wire.Decode<CallChallengeProof>(Encoding.UTF8.GetString(Convert.FromBase64String(challenge.Payload))); }
            catch (Exception e) when (e is FormatException or JsonException) { throw new Fault(403, "invalid_challenge"); }
            if (proof.ChallengeId != challenge.Id || proof.Protocol != Protocol || proof.DeviceId != actor.Id || proof.Direction != direction || proof.Action != action ||
                proof.SourceId != sourceId || proof.AgentId != agentId || proof.SessionId != sessionId || proof.CallId != callId || proof.InviteId != inviteId ||
                proof.SecurityLevel != securityLevel || proof.ExpiresAt != challenge.ExpiresAt) throw new Fault(403, "invalid_challenge");
            var device = Get<Device>(c, t, "device", actor.Id) ?? throw new Fault(401, "device_revoked");
            try
            {
                using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(device.PublicKey), out _);
                if (!key.VerifyData(Convert.FromBase64String(challenge.Payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) throw new Fault(403, "invalid_signature");
            }
            catch (Exception e) when (e is FormatException or CryptographicException) { throw new Fault(403, "invalid_signature"); }
            Exec(c, t, "DELETE FROM objects WHERE kind='callchallenge' AND id=$i", ("$i", challengeId));
            return 0;
        });
    }

    private long RingDeadline(Call call) => call.StartedAt + settings.CallRingSeconds * 1000L;
    private object View(Call call) => new
    {
        call.Id, call.InviteId, call.Direction, call.Status, call.Reason, call.SourceId, call.AgentId, call.AgentName, call.SessionId, call.SecurityLevel,
        call.DeviceId, call.StartedAt, call.ActiveAt, call.EndedAt, call.Title, expiresAt = RingDeadline(call)
    };
    private static void RequireParticipant(Actor actor, Call call)
    {
        if (actor.Kind == "integration" && actor.Id == call.SourceId) return;
        if (actor.Kind == "web" && actor.IsOwner) return;
        if (actor.Kind == "device" && (call.DeviceId == actor.Id || call.Direction == "incoming")) return;
        throw new Fault(404, "call_not_found");
    }
    private static void RequireParticipant(Actor actor, CallRecord call)
    {
        if (actor.Kind == "integration" && actor.Id == call.SourceId) return;
        if (actor.Kind == "web" && actor.IsOwner) return;
        if (actor.Kind == "device" && (call.DeviceId == actor.Id || call.Direction == "incoming")) return;
        throw new Fault(404, "call_not_found");
    }
}
