using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using static Notify.Database;

namespace Notify;

// Media stays in memory. Calls use an agent WebSocket or an in-process playback peer.
public sealed partial class CallService
{
    public const string Protocol = "alxprgs.notify.call.v2";
    public static readonly string[] SupportedProtocols = [Protocol];
    public const int AudioBytes = 640;
    public const int HeaderBytes = 12;
    public const int FrameBytes = HeaderBytes + AudioBytes;
    private readonly AgentHub agents; private readonly Database db; private readonly Settings settings; private readonly ILogger<CallService> log; private readonly IPushSender push;
    private readonly SileroSpeech speech; private readonly SttSpeech stt;
    private readonly IDataProtector transcriptProtector;
    private readonly object sync = new();
    private readonly Dictionary<string, Call> calls = new();
    private string? currentId;
    private SipPeer? sipPeer;

    public CallService(AgentHub agents, Database db, Settings settings, ILogger<CallService> log, IPushSender push,
        SileroSpeech speech, SttSpeech stt, IDataProtectionProvider protection)
    {
        this.agents = agents; this.db = db; this.settings = settings; this.log = log; this.push = push;
        this.speech = speech; this.stt = stt;
        transcriptProtector = protection.CreateProtector("notify.v1.calltranscript");
        agents.ControlEvent += HandleAgentFrame; agents.DisconnectedEvent += HandleAgentDisconnected;
    }

    private sealed class Call(string id, string inviteId, string direction, string sourceId, string agentId, string agentName, string? deviceId, string? sessionId, string securityLevel, string title, string? iconId, ReadOnlyMemory<byte> playback = default, string mode = "one_shot", string target = "android", SipPeer? sip = null, bool phoneAssistant = false)
    {
        public string Id { get; } = id;
        public string InviteId { get; } = inviteId;
        public string Direction { get; } = direction;
        public string SourceId { get; } = sourceId;
        public string AgentId { get; } = agentId;
        public string AgentName { get; } = agentName;
        public bool UsesAgent { get; } = agentId != sourceId;
        public string? DeviceId { get; set; } = deviceId;
        public string? SessionId { get; } = sessionId;
        public string SecurityLevel { get; } = securityLevel;
        public string Title { get; } = title;
        public string? IconId { get; } = iconId;
        public ReadOnlyMemory<byte> Playback { get; } = playback;
        public string Mode { get; } = mode;
        public string Target { get; } = target;
        public SipPeer? Sip { get; } = sip;
        public bool PhoneAssistant { get; } = phoneAssistant;
        public TaskCompletionSource<bool>? AgentDecision { get; } =
            phoneAssistant ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        public bool SipMediaClaimed;
        public long AudioReadyAt;
        public TaskCompletionSource? PlaybackSent { get; } = playback.IsEmpty ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long StartedAt { get; } = Wire.Now; public long? ActiveAt, EndedAt; public string Status = "ringing"; public string? Reason;
        public int PlaybackGeneration;
        public bool Ended; public CancellationTokenSource Cts { get; } = new(); public MediaSide? Phone, AgentMedia;
        public ConversationState? Conversation;
    }

    internal void EnsureIdle()
    {
        lock (sync) { if (currentId != null) throw new Fault(409, "busy"); }
    }

    internal object StartPlayback(Actor actor, string title, string text, ReadOnlyMemory<byte> pcm, string attemptId, string fingerprint, string? agentId = null, string? agentName = null, string target = "android")
    {
        Call call;
        lock (sync)
        {
            if (currentId != null) throw new Fault(409, "busy");
            var sip = ResolveTarget(target);
            call = db.Run((c, t) =>
            {
                var source = Get<Source>(c, t, "source", actor.Id);
                if (source is not { Revoked: false } || !source.Scopes.Any(s => s is ChatGptCall.Scope or ChatGptCall.AnnounceScope or ChatGptCall.AgentScope))
                    throw new Fault(403, "insufficient_scope");
                var resolvedAgent = agentId ?? source.Id;
                var created = new Call(Wire.Id(), Wire.Id(), "incoming", source.Id, resolvedAgent, agentName ?? source.Name,
                    null, null, "standard", title, source.IconId, pcm, "playback", target, sip);
                // A restart interrupts playback, but a retry must still resolve to this call.
                Put(c, t, "call", created.Id, new CallRecord(created.Id, source.Id, resolvedAgent, null, null,
                    created.SecurityLevel, created.StartedAt, created.StartedAt, "ended", "interrupted", 0, created.Direction, Target: target));
                Put(c, t, ChatGptCall.AttemptKind, attemptId, new ChatGptCallAttempt(fingerprint, created.Id, created.StartedAt));
                return created;
            });
            calls[call.Id] = call;
            currentId = call.Id;
        }
        RecordTurn(call, "agent", text, final: true, status: "complete");
        _ = RingTimerAsync(call);
        _ = InvitePhoneAsync(call);
        return View(call);
    }
    private sealed class MediaSide(WebSocket socket, CancellationToken lifetime, int queueFrames, string name)
    {
        public WebSocket Socket { get; } = socket;
        public string Name { get; } = name;
        public BoundedFrameQueue Outbound { get; } = new(queueFrames);
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public CancellationTokenSource ReceiveCts { get; } = new();
        public TaskCompletionSource PeerClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Per-call, in-memory conversation state. No utterance is written to disk.</summary>
    private sealed class ConversationState
    {
        public Channel<Utterance> Utterances { get; } = Channel.CreateBounded<Utterance>(new BoundedChannelOptions(4)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });
        public readonly object Gate = new();
        public readonly List<byte> Pending = new();
        public readonly Queue<byte[]> PreRoll = new();
        public bool Speaking;
        public int SilentFrames;
        public double Noise = 180;
        public string? LastUserTurnId;
        public long LastVoiceTick;
        public long LastVoiceMediaMs;
        public int UserGeneration;
        public readonly HashSet<string> Answered = new(StringComparer.Ordinal);
        public CancellationTokenSource? Playback;
        public Task? Loop;
        public uint PhoneSequence;
        public bool Ended;
    }

    private sealed record Utterance(byte[] Pcm, string Id, long LastVoiceTick, long ReadyTick, long LastVoiceMediaMs, int UserGeneration);

    internal (Agent Agent, string[] Capabilities, AgentSession[] Sessions, string[] CallProtocols, string? CallProtocol)? AgentOnline(string agentId) => agents.Online(agentId);

    internal object StartBotConversation(Actor actor, string title, string attemptId, string fingerprint, string target, string? sessionId, Agent? boundAgent)
    {
        if (!stt.Enabled) throw new Fault(503, "stt_unavailable");
        Call call;
        lock (sync)
        {
            var existing = db.Run((c, t) => Get<ChatGptCallAttempt>(c, t, ChatGptCall.ConversationAttemptKind, attemptId));
            if (existing != null)
            {
                if (existing.Fingerprint != fingerprint) throw new Fault(409, "idempotency_conflict");
                return State(actor, existing.CallId);
            }
            if (currentId != null) throw new Fault(409, "busy");
            var sip = ResolveTarget(target);
            call = db.Run((c, t) =>
            {
                var source = Get<Source>(c, t, "source", actor.Id);
                if (source is not { Revoked: false } || !source.Scopes.Contains(boundAgent == null ? ChatGptCall.Scope : ChatGptCall.AgentScope))
                    throw new Fault(403, "insufficient_scope");
                var created = new Call(Wire.Id(), Wire.Id(), "incoming", source.Id, boundAgent?.Id ?? source.Id, boundAgent?.Name ?? source.Name,
                    null, sessionId, "standard", title, source.IconId, mode: "conversation", target: target, sip: sip);
                Put(c, t, "call", created.Id, new CallRecord(created.Id, source.Id, created.AgentId, null, sessionId,
                    created.SecurityLevel, created.StartedAt, created.StartedAt, "ended", "interrupted", 0, created.Direction, Target: target));
                Put(c, t, ChatGptCall.ConversationAttemptKind, attemptId, new ChatGptCallAttempt(fingerprint, created.Id, created.StartedAt));
                return created;
            });
            calls[call.Id] = call;
            currentId = call.Id;
        }
        _ = RingTimerAsync(call);
        _ = InvitePhoneAsync(call);
        return View(call);
    }

    internal void SayFromActor(Actor actor, string id, string text)
    {
        if (actor.Kind != "integration") throw new Fault(403, "integration_required");
        Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.SourceId != actor.Id) throw new Fault(404, "call_not_found");
        if (call.Mode != "conversation" || call.UsesAgent) throw new Fault(400, "invalid_call");
        if (call.Status != "active") throw new Fault(409, "call_not_ready");
        SileroSpeech.ValidateText(text);
        _ = SayAsync(call, text, null);
    }

    public object Challenge(Actor actor, CallChallengeInput input)    {
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
        if (call.Target != "android") throw new Fault(404, "call_not_found");
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
        if (input.SecurityLevel is not ("standard" or "elevated") || input.Mode is not ("one_shot" or "conversation") || input.SourceId.Length is < 1 or > 64 || input.SessionId?.Length > 64 || input.SessionId?.Any(char.IsControl) == true) throw new Fault(400, "invalid_call");
        if (input.Mode == "conversation" && !stt.Enabled) throw new Fault(503, "stt_unavailable");
        var agent = agents.ForSource(input.SourceId) ?? throw new Fault(404, "agent_not_registered");
        var online = agents.Online(agent.Id) ?? throw new Fault(409, "agent_offline");
        if (!online.Capabilities.Contains("realtime_audio", StringComparer.Ordinal)) throw new Fault(409, "agent_unsupported");
        if (online.CallProtocol != Protocol) throw new Fault(409, "call_protocol_unsupported");
        if (input.SessionId != null && online.Sessions.All(s => s.Id != input.SessionId)) throw new Fault(400, "invalid_session");
        lock (sync) { if (currentId != null) throw new Fault(409, "busy"); }
        if (input.SecurityLevel == "elevated") VerifyChallenge(actor, input.ChallengeId, input.Signature, "outgoing", "start", input.SourceId, agent.Id, input.SessionId, null, null, "elevated");
        else if (input.ChallengeId != null || input.Signature != null) throw new Fault(400, "invalid_call");
        var iconId = ResolveIcon(input.SourceId, null);
        Call call;
        lock (sync)
        {
            if (currentId != null) throw new Fault(409, "busy");
            call = new Call(Wire.Id(), Wire.Id(), "outgoing", input.SourceId, agent.Id, online.Agent.Name, actor.Id, input.SessionId, input.SecurityLevel, "Звонок", iconId, mode: input.Mode);
            calls[call.Id] = call; currentId = call.Id;
        }
        _ = RingTimerAsync(call);
        var invited = await agents.SendControlAsync(call.AgentId, "call.invite", new
        {
            callId = call.Id, inviteId = call.InviteId, direction = call.Direction, sourceId = call.SourceId, sessionId = call.SessionId, securityLevel = call.SecurityLevel, mode = call.Mode,
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
        if (call.Target != "android") throw new Fault(404, "call_not_found");
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
        if (call.Playback.IsEmpty)
            await agents.SendControlAsync(call.AgentId, "call.accepted", new { callId = call.Id, inviteId = call.InviteId, deviceId = actor.Id, deviceName });
        _ = PushCallAsync(call, "accepted");
        return View(call);
    }
    public async Task RejectAsync(Actor actor, string id)
    {
        actor.RequireDevice();
        Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.Target != "android" || call.Direction != "incoming") throw new Fault(404, "call_not_found");
        if (!await EndAsync(call, "rejected", expectedStatus: "ringing")) throw new Fault(409, "call_taken");
    }

    private async Task DialAsync(Agent agent, JsonElement frame, JsonElement payload)
    {
        var requestEventId = frame.TryGetProperty("eventId", out var eventId) ? eventId.GetString() : null;
        if (requestEventId is not { Length: 32 }) return;
        var online = agents.Online(agent.Id);
        if (online == null) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "agent_offline" }); return; }
        if (!online.Value.Capabilities.Contains("realtime_audio", StringComparer.Ordinal)) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "agent_unsupported" }); return; }
        if (online.Value.CallProtocol != Protocol) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "call_protocol_unsupported" }); return; }
        var target = payload.TryGetProperty("target", out var route)
            ? route.ValueKind == JsonValueKind.String ? route.GetString() : null
            : "android";
        var security = payload.TryGetProperty("securityLevel", out var level) ? level.GetString() : "standard";
        var mode = payload.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String ? modeValue.GetString() : "one_shot";
        var sessionId = payload.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
        var title = payload.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
        if (target is not ("android" or "asterisk") || (target == "asterisk" && security != "standard") || security is not ("standard" or "elevated") || mode is not ("one_shot" or "conversation") || sessionId?.Length > 64 || sessionId?.Any(char.IsControl) == true || title?.Length > 160 || title?.Any(char.IsControl) == true)
        {
            await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "invalid_call" }); return;
        }
        if (mode == "conversation" && !stt.Enabled)
        {
            await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "stt_unavailable" }); return;
        }
        if (sessionId != null && online.Value.Sessions.All(s => s.Id != sessionId))
        {
            await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "invalid_session" }); return;
        }
        string? requestedIcon = null;
        if (payload.TryGetProperty("iconId", out var iconValue))
        {
            requestedIcon = iconValue.ValueKind == JsonValueKind.String ? iconValue.GetString() : null;
            if (requestedIcon is not { Length: 64 } || !requestedIcon.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "invalid_call" }); return;
            }
        }
        var iconId = ResolveIcon(agent.SourceId, requestedIcon);
        if (requestedIcon != null && iconId == null)
        {
            await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = "icon_not_found" }); return;
        }
        Call? call = null;
        string failure = "busy";
        lock (sync)
        {
            if (target == "asterisk" && sipPeer == null) failure = "asterisk_unavailable";
            else if (currentId == null)
            {
                call = new Call(Wire.Id(), Wire.Id(), "incoming", agent.SourceId, agent.Id, online.Value.Agent.Name, null, sessionId, security, string.IsNullOrWhiteSpace(title) ? online.Value.Agent.Name : title, iconId, mode: mode, target: target!, sip: target == "asterisk" ? sipPeer : null);
                calls[call.Id] = call; currentId = call.Id;
            }
        }
        if (call == null) { await agents.SendControlAsync(agent.Id, "call.dial_ack", new { requestEventId, ok = false, code = failure }); return; }
        var ack = await agents.SendControlAsync(call.AgentId, "call.dial_ack", new { requestEventId, ok = true, callId = call.Id, inviteId = call.InviteId, status = "ringing", mode = call.Mode, target = call.Target, expiresAt = RingDeadline(call) });
        if (!ack) { await EndAsync(call, "agent_offline"); return; }
        _ = RingTimerAsync(call);
        _ = InvitePhoneAsync(call);
    }

    public object Incoming(Actor actor)
    {
        actor.RequireDevice();
        Call? call; lock (sync) call = calls.Values.FirstOrDefault(c => !c.Ended && c.Direction == "incoming" && c.Target == "android" && c.Status == "ringing");
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

    private string RecordTurn(Call call, string speaker, string text, bool final = true, string? status = null, string? error = null, string? id = null)
    {
        var turnId = id ?? Wire.Id();
        try
        {
            var safe = text.Length > 16000 ? text[..16000] : text;
            db.Run((c, t) =>
            {
                var existing = Get<CallTranscript>(c, t, "calltranscript", call.Id);
                var turns = existing == null ? new List<CallTurn>() : Wire.Decode<List<CallTurn>>(transcriptProtector.Unprotect(existing.ProtectedTurns));
                turns.Add(new CallTurn(turnId, speaker, safe, Wire.Now, final, status, error));
                Put(c, t, "calltranscript", call.Id, new CallTranscript(call.Id,
                    transcriptProtector.Protect(Wire.Encode(turns)),
                    existing?.Partial == true || error != null, error ?? existing?.Error, Wire.Now));
                return 0;
            });
        }
        catch (Exception e)
        {
            log.LogWarning("Call {CallId} transcript write failed: {Type}", call.Id, e.GetType().Name);
        }
        return turnId;
    }

    public object Transcript(Actor actor, string id)
    {
        if (actor.Kind == "integration" && !actor.Scopes.Any(s => s is ChatGptCall.Scope or ChatGptCall.AgentScope)) throw new Fault(403, "scope_required");
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)) throw new Fault(404, "call_not_found");
        Call? live; lock (sync) calls.TryGetValue(id, out live);
        if (live != null) RequireParticipant(actor, live);
        else
        {
            var record = db.Run((c, t) => Get<CallRecord>(c, t, "call", id)) ?? throw new Fault(404, "call_not_found");
            RequireParticipant(actor, record);
        }
        var transcript = db.Run((c, t) => Get<CallTranscript>(c, t, "calltranscript", id));
        if (transcript == null)
        {
            if (live == null) throw new Fault(404, "transcript_not_found");
            return new { callId = id, turns = Array.Empty<CallTurn>(), partial = false, error = (string?)null, updatedAt = Wire.Now };
        }
        var turns = Wire.Decode<List<CallTurn>>(transcriptProtector.Unprotect(transcript.ProtectedTurns));
        return new { callId = id, turns, partial = transcript.Partial, error = transcript.Error, updatedAt = transcript.UpdatedAt };
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
        await AttachAsync(PhoneCall(actor, id), socket, phone: true, ct);
    }
    public void RequirePhone(Actor actor, string id) => PhoneCall(actor, id);
    private Call PhoneCall(Actor actor, string id)
    {
        actor.RequireDevice(); Call? call; lock (sync) calls.TryGetValue(id, out call);
        if (call == null || call.DeviceId != actor.Id) throw new Fault(404, "call_not_found");
        lock (sync) { if (call.Ended || call.Status is not ("connecting" or "active")) throw new Fault(409, "call_not_ready"); if (call.Phone != null) throw new Fault(409, "already_attached"); }
        return call;
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
        if (call == null || call.Mode == "conversation" || !call.Playback.IsEmpty || call.AgentId != agent.Id) throw new Fault(404, "call_not_found");
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
            both = call.Phone != null && (call.Mode == "conversation" || call.AgentMedia != null || !call.Playback.IsEmpty);
            if (both && call.Status == "connecting")
            {
                call.Status = "active"; call.ActiveAt = Wire.Now;
                call.AudioReadyAt = Environment.TickCount64 + settings.CallAudioStartDelayMs;
            }
        }
        _ = SendLoopAsync(call, side);
        _ = ReceiveLoopAsync(call, side, phone);
        log.LogInformation("Call {CallId} media attached: {Side} status={Status}", call.Id, phone ? "phone" : "agent", call.Status);
        if (both)
        {
            _ = MaxTimerAsync(call);
            if (call.Mode == "one_shot")
            {
                try { await WaitForAudioAsync(call, call.Cts.Token); }
                catch (OperationCanceledException) when (call.Cts.IsCancellationRequested) { return; }
            }
            object activeState = call.Mode == "conversation"
                ? new { type = "state", status = "active", mode = call.Mode }
                : new { type = "state", status = "active" };
            var notified = await call.Phone!.TextAsync(Wire.Encode(activeState), call.Cts.Token);
            if (call.Mode == "conversation")
            {
                if (notified)
                {
                    StartConversation(call);
                    if (call.UsesAgent)
                        await agents.SendControlAsync(call.AgentId, "call.active", new { callId = call.Id, inviteId = call.InviteId, mode = call.Mode });
                }
                else await EndAsync(call, "disconnected");
            }
            else if (call.Playback.IsEmpty)
                await agents.SendControlAsync(call.AgentId, "call.active", new { callId = call.Id, inviteId = call.InviteId });
            else if (notified)
                _ = PlayAsync(call, side);
            else
                await EndAsync(call, "disconnected");
        }
        await side.Closed.Task;
    }

    private static Task WaitForAudioAsync(Call call, CancellationToken ct) =>
        Task.Delay((int)Math.Max(0, call.AudioReadyAt - Environment.TickCount64), ct);

    private async Task PlayAsync(Call call, MediaSide phone)
    {
        try
        {
            await WaitForAudioAsync(call, call.Cts.Token);
            var pcm = call.Playback;
            for (var offset = 0; offset < pcm.Length; offset += AudioBytes)
            {
                call.Cts.Token.ThrowIfCancellationRequested();
                var frame = new byte[FrameBytes];
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(offset / AudioBytes));
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)(offset / AudioBytes * 20));
                pcm.Span.Slice(offset, Math.Min(AudioBytes, pcm.Length - offset)).CopyTo(frame.AsSpan(HeaderBytes));
                if (!phone.Outbound.TryEnqueue(frame))
                {
                    await EndAsync(call, "degraded");
                    return;
                }
                await Task.Delay(20, call.Cts.Token);
            }
            await call.PlaybackSent!.Task.WaitAsync(TimeSpan.FromSeconds(2), call.Cts.Token);
            await Task.Delay(350, call.Cts.Token);
            await EndAsync(call, "playback_sent");
        }
        catch (OperationCanceledException) when (call.Cts.IsCancellationRequested) { }
        catch (TimeoutException) { await EndAsync(call, "degraded"); }
    }

    private async Task ReceiveLoopAsync(Call call, MediaSide side, bool phone)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (side.Socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                // Cancelling a pending WebSocket receive aborts the connection. Bounded shutdown
                // sends the final state first, then closes the socket and unblocks this receive.
                var result = await side.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), side.ReceiveCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    side.PeerClosed.TrySetResult();
                    await EndAsync(call, "disconnected");
                    return;
                }
                if (side.Cts.IsCancellationRequested) continue;
                if (result.MessageType != WebSocketMessageType.Binary || !result.EndOfMessage || !ValidFrame(buffer, result.Count, side, out var generation)) { await EndAsync(call, "invalid_audio"); return; }
                bool active; lock (sync) active = call.Status == "active" && !call.Ended;
                if (!active || (call.Mode == "one_shot" && Environment.TickCount64 < call.AudioReadyAt)) continue;
                if (phone && call.Mode == "conversation")
                {
                    AcceptConversationFrame(call, buffer.AsMemory(HeaderBytes, AudioBytes), side.LastTimestamp);
                    continue;
                }
                if (phone && !call.Playback.IsEmpty) continue;
                if (!phone && generation < (uint)call.PlaybackGeneration) continue;
                var peer = phone ? call.AgentMedia : call.Phone;
                var frame = new byte[FrameBytes]; Buffer.BlockCopy(buffer, 0, frame, 0, FrameBytes);
                if (peer == null || !peer.Outbound.TryEnqueue(frame)) { await EndAsync(call, "degraded"); return; }
            }
            if (!side.Cts.IsCancellationRequested) await EndAsync(call, "disconnected");
        }
        catch (OperationCanceledException) when (side.ReceiveCts.IsCancellationRequested) { }
        catch (WebSocketException e) { log.LogWarning("Call {CallId} media receive closed on {Side}: {Message} state={State} close={Close}", call.Id, side.Name, e.Message, side.Socket.State, side.Socket.CloseStatus); await EndAsync(call, "disconnected"); }
        catch (Exception e) { log.LogWarning("Call {CallId} media receive failed: {Type}", call.Id, e.GetType().Name); await EndAsync(call, "disconnected"); }
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
                    if (side.Socket.State == WebSocketState.Open)
                    {
                        await side.Socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, side.Cts.Token);
                        if (call.PlaybackSent != null && BinaryPrimitives.ReadUInt32BigEndian(frame) == (call.Playback.Length - 1) / AudioBytes)
                            call.PlaybackSent.TrySetResult();
                    }
                }
                finally { side.SendLock.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogWarning("Call {CallId} media send failed on {Side}: {Type} state={State} close={Close} message={Message}", call.Id, side.Name, e.GetType().Name, side.Socket.State, side.Socket.CloseStatus, e.Message); await EndAsync(call, "disconnected"); }
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
            if (call == null || !call.Playback.IsEmpty || call.AgentId != agent.Id) return;
            switch (type)
            {
                case "call.accept": await AcceptByAgentAsync(call); break;
                case "call.reject": await EndAsync(call, "rejected"); break;
                case "call.end": await EndAsync(call, "ended"); break;
                case "call.interrupt":
                    if (!payload.TryGetProperty("generation", out var generation) || !generation.TryGetInt32(out var number) || number < 0) return;
                    await InterruptAsync(call, number);
                    break;
                case "call.say":
                    if (call.Mode != "conversation" || !payload.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String) return;
                    var replyText = textValue.GetString();
                    var turnId = payload.TryGetProperty("turnId", out var turnValue) && turnValue.ValueKind == JsonValueKind.String ? turnValue.GetString() : null;
                    await SayAsync(call, replyText, turnId);
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
        call.AgentDecision?.TrySetResult(true);
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
        call.Conversation?.Playback?.Cancel();
        if (phone == null) return;
        phone.Outbound.Clear();
        await phone.TextAsync(Wire.Encode(new { type = "interrupt", generation }));
    }

    private const int ConversationMaxPcm = 60 * 32000;
    private const int PhoneAssistantTailFrames = 6;

    private void StartConversation(Call call)
    {
        if (call.Conversation != null) return;
        var conversation = new ConversationState();
        lock (sync) call.Conversation = conversation;
        conversation.Loop = ConversationLoopAsync(call, conversation);
    }

    private static double Rms(ReadOnlySpan<byte> pcm)
    {
        double sum = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | pcm[i + 1] << 8);
            sum += sample * (double)sample;
        }
        return pcm.Length < 2 ? 0 : Math.Sqrt(sum / (pcm.Length / 2));
    }

    /// <summary>Energy VAD with 200 ms pre-roll and configurable silence and utterance limits.</summary>
    private void AcceptConversationFrame(Call call, ReadOnlyMemory<byte> pcm, long mediaMs)
    {
        var state = call.Conversation;
        if (state == null || state.Ended) return;
        var level = Rms(pcm.Span);
        var speaking = state.Speaking;
        var threshold = Math.Max(480, state.Noise * 2.4);
        var voice = level > threshold;
        if (!speaking)
        {
            state.Noise = Math.Min(6000, state.Noise * 0.95 + level * 0.05);
            if (voice)
            {
                state.Speaking = true; state.SilentFrames = 0; state.Pending.Clear();
                foreach (var previous in state.PreRoll) state.Pending.AddRange(previous);
                state.PreRoll.Clear();
                BeginUserSpeech(call);
            }
            else
            {
                state.PreRoll.Enqueue(pcm.ToArray());
                while (state.PreRoll.Count > 10) state.PreRoll.Dequeue();
                return;
            }
        }
        if (state.Speaking)
        {
            if (voice)
            {
                state.LastVoiceTick = Stopwatch.GetTimestamp();
                state.LastVoiceMediaMs = mediaMs;
            }
            state.Pending.AddRange(pcm.ToArray());
            state.SilentFrames = voice ? 0 : state.SilentFrames + 1;
            var maxSamples = settings.ConversationUtteranceSeconds * 32000;
            if (state.SilentFrames * AudioBytes >= settings.ConversationSilenceMilliseconds * 32 || state.Pending.Count >= maxSamples)
                FinalizeUtterance(call, state);
        }
    }

    private void BeginUserSpeech(Call call)
    {
        var state = call.Conversation;
        if (state == null) return;
        uint generation;
        lock (state.Gate)
        {
            state.UserGeneration++;
            if (call.PhoneAssistant) state.LastUserTurnId = null;
            call.PlaybackGeneration++;
            generation = (uint)call.PlaybackGeneration;
            state.Playback?.Cancel(); state.Playback = null;
        }
        var phone = call.Phone;
        phone?.Outbound.Clear();
        _ = phone?.TextAsync(Wire.Encode(new { type = "interrupt", generation }));
        _ = phone?.TextAsync(Wire.Encode(new { type = "conversation", status = "listening" }));
        if (call.UsesAgent)
            _ = agents.SendControlAsync(call.AgentId, "call.interrupted", new { callId = call.Id, generation, reason = "user_speech" });
    }

    private void FinalizeUtterance(Call call, ConversationState state)
    {
        if (!state.Speaking) return;
        state.Speaking = false;
        var trimFrames = call.PhoneAssistant ? Math.Max(0, state.SilentFrames - PhoneAssistantTailFrames) : 0;
        state.SilentFrames = 0;
        state.PreRoll.Clear();
        var pcm = new byte[state.Pending.Count - trimFrames * AudioBytes];
        state.Pending.CopyTo(0, pcm, 0, pcm.Length);
        state.Pending.Clear();
        if (pcm.Length < AudioBytes * 8)
        {
            _ = call.Phone?.TextAsync(Wire.Encode(new { type = "conversation", status = "idle" }));
            return;
        }
        var turn = new Utterance(pcm, Wire.Id(), state.LastVoiceTick, Stopwatch.GetTimestamp(),
            state.LastVoiceMediaMs, state.UserGeneration);
        PhoneTiming(call, turn.Id, "vad_end", turn.LastVoiceTick);
        if (!state.Utterances.Writer.TryWrite(turn))
        {
            _ = EndAsync(call, "degraded");
            return;
        }
        _ = call.Phone?.TextAsync(Wire.Encode(new { type = "conversation", status = "transcribing" }));
    }

    private async Task ConversationLoopAsync(Call call, ConversationState state)
    {
        try
        {
            await foreach (var turn in state.Utterances.Reader.ReadAllAsync(call.Cts.Token))
            {
                var sttStarted = Stopwatch.GetTimestamp();
                PhoneTiming(call, turn.Id, "stt_queue", turn.ReadyTick);
                string? text = null, error = null;
                try
                {
                    text = await stt.RecognizeAsync(Wav(turn.Pcm), call.Cts.Token);
                }
                catch (SpeechException exception)
                {
                    error = exception.Code;
                }
                PhoneTiming(call, turn.Id, "vosk_complete", sttStarted);
                if (text != null)
                {
                    var turnId = RecordTurn(call, "user", text, status: "complete", id: turn.Id);
                    lock (state.Gate)
                    {
                        if (call.PhoneAssistant && state.UserGeneration != turn.UserGeneration) continue;
                        state.LastUserTurnId = turnId;
                    }
                    if (call.PhoneAssistant && settings.PhoneAssistantTiming)
                        await call.Phone!.TextAsync(Wire.Encode(new { type = "assistant_timing", turnId, lastVoiceMediaMs = turn.LastVoiceMediaMs }));
                    await SendTranscript(call, turnId, "user", text, "complete", null);
                    if (call.UsesAgent)
                    {
                        var delivery = Stopwatch.GetTimestamp();
                        if (!await agents.SendControlAsync(call.AgentId, "dialog.user", new
                        {
                            callId = call.Id, sessionId = call.SessionId, turnId, text, at = Wire.Now
                        }))
                            await EndAsync(call, "disconnected");
                        PhoneTiming(call, turnId, "agent_delivery", delivery);
                    }
                }
                else
                {
                    var turnId = RecordTurn(call, "user", "", status: "error", error: error ?? "stt_unavailable", id: turn.Id);
                    lock (state.Gate)
                    {
                        if (call.PhoneAssistant && state.UserGeneration != turn.UserGeneration) continue;
                        state.LastUserTurnId = turnId;
                    }
                    await SendTranscript(call, turnId, "user", "", "error", error ?? "stt_unavailable");
                    if (call.UsesAgent)
                        await agents.SendControlAsync(call.AgentId, "dialog.error", new
                        {
                            callId = call.Id, sessionId = call.SessionId, turnId, error = error ?? "stt_unavailable"
                        });
                    if (call.PhoneAssistant)
                        await SayAsync(call, "Не удалось распознать речь. Повторите, пожалуйста.", turnId);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            log.LogWarning("Call {CallId} conversation stopped: {Type}", call.Id, exception.GetType().Name);
            if (!state.Ended) _ = EndAsync(call, "disconnected");
        }
    }

    private static byte[] Wav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), AudioService.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), AudioService.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    private Task SendTranscript(Call call, string turnId, string speaker, string text, string status, string? error) =>
        call.Phone?.TextAsync(Wire.Encode(new
        {
            type = "transcript",
            turn = new { id = turnId, speaker, text, at = Wire.Now, final = true, status, error }
        })) ?? Task.CompletedTask;

    private async Task SayAsync(Call call, string? text, string? turnId)
    {
        if (call.Mode != "conversation" || call.Conversation is not { } state) return;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r')))
            return;
        uint generation; CancellationTokenSource playback;
        lock (state.Gate)
        {
            if (state.Ended || call.Ended || call.Status != "active") return;
            if (call.UsesAgent)
            {
                var replyTo = turnId ?? state.LastUserTurnId;
                if (replyTo == null || (call.PhoneAssistant && replyTo != state.LastUserTurnId) ||
                    !state.Answered.Add(replyTo)) return;
            }
            call.PlaybackGeneration++;
            generation = (uint)call.PlaybackGeneration;
            state.Playback?.Cancel();
            playback = CancellationTokenSource.CreateLinkedTokenSource(call.Cts.Token);
            state.Playback = playback;
        }
        call.Phone?.Outbound.Clear();
        _ = call.Phone?.TextAsync(Wire.Encode(new { type = "interrupt", generation }));
        _ = PlayConversationSpeechAsync(call, state, text, turnId, generation, playback);
    }

    private async Task PlayConversationSpeechAsync(Call call, ConversationState state, string text, string? userTurnId,
        uint generation, CancellationTokenSource playback)
    {
        var received = Stopwatch.GetTimestamp();
        PhoneTiming(call, userTurnId, "agent_reply_received");
        try
        {
            ReadOnlyMemory<byte> pcm;
            try
            {
                var ttsStarted = Stopwatch.GetTimestamp();
                PhoneTiming(call, userTurnId, "silero_queue", received);
                var wav = await speech.WithWav(text, ConversationMaxPcm + 4096, async (stream, length, token) =>
                {
                    PhoneTiming(call, userTurnId, "silero_headers", ttsStarted);
                    var bytes = new byte[(int)length];
                    await stream.ReadExactlyAsync(bytes, token);
                    using var input = new MemoryStream(bytes, writable: false);
                    AudioService.ValidateWav(input, length, out var offset, out var count);
                    if (count > ConversationMaxPcm) throw new SpeechException("agent_reply_too_long");
                    return bytes.AsMemory(offset, count);
                }, playback.Token);
                pcm = wav;
                PhoneTiming(call, userTurnId, "silero_complete", ttsStarted);
            }
            catch (SpeechException error)
            {
                var id = RecordTurn(call, "agent", "", status: "error", error: error.Code);
                await SendTranscript(call, id, "agent", "", "error", error.Code);
                await agents.SendControlAsync(call.AgentId, "call.speech_error", new { callId = call.Id, error = error.Code });
                return;
            }
            await WaitForAudioAsync(call, playback.Token);
            if (call.Ended || call.PlaybackGeneration != generation) return;
            var turnId = RecordTurn(call, "agent", text, status: "complete");
            await SendTranscript(call, turnId, "agent", text, "complete", null);
            await call.Phone!.TextAsync(Wire.Encode(new { type = "conversation", status = "speaking", turnId, userTurnId, generation }));
            var phone = call.Phone!;
            for (var offset = 0; offset < pcm.Length; offset += AudioBytes)
            {
                playback.Token.ThrowIfCancellationRequested();
                if (call.PlaybackGeneration != generation) return;
                var frame = new byte[FrameBytes];
                var index = (uint)(offset / AudioBytes);
                BinaryPrimitives.WriteUInt32BigEndian(frame, state.PhoneSequence++);
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), index * 20);
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), generation);
                pcm.Slice(offset, Math.Min(AudioBytes, pcm.Length - offset)).Span.CopyTo(frame.AsSpan(HeaderBytes));
                if (!phone.Outbound.TryEnqueue(frame))
                {
                    _ = EndAsync(call, "degraded");
                    return;
                }
                if (offset == 0) PhoneTiming(call, userTurnId, "first_audio_queued", received);
                await Task.Delay(20, playback.Token);
            }
            await Task.Delay(200, playback.Token);
            if (call.PlaybackGeneration == generation) await phone.TextAsync(Wire.Encode(new { type = "conversation", status = "idle" }));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            log.LogWarning("Call {CallId} conversation speech failed: {Type}", call.Id, exception.GetType().Name);
            if (!state.Ended && !call.Ended) _ = EndAsync(call, "disconnected");
        }
        finally
        {
            lock (state.Gate) if (ReferenceEquals(state.Playback, playback)) state.Playback = null;
            playback.Dispose();
        }
    }

    private void PhoneTiming(Call call, string? turnId, string stage, long? started = null)
    {
        if (!call.PhoneAssistant || !settings.PhoneAssistantTiming) return;
        var ticks = Stopwatch.GetTimestamp();
        log.LogInformation("PhoneTiming {CallId} {TurnId} {Stage} ticks={Ticks} frequency={Frequency} elapsedMs={ElapsedMs}",
            call.Id, turnId, stage, ticks, Stopwatch.Frequency,
            started is { } start ? Stopwatch.GetElapsedTime(start, ticks).TotalMilliseconds : null);
    }
    private async Task HandleAgentDisconnected(Agent agent)
    {
        Call? call; lock (sync) call = calls.Values.FirstOrDefault(c => c.AgentId == agent.Id && !c.Ended);
        if (call != null) await EndAsync(call, "disconnected");
    }

    private async Task RingTimerAsync(Call call)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(settings.CallRingSeconds), call.Cts.Token); } catch (OperationCanceledException) { return; }
        await EndAsync(call, "missed", expectedStatus: "ringing");
    }
    private async Task MediaTimerAsync(Call call)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(settings.CallMediaConnectSeconds), call.Cts.Token); } catch (OperationCanceledException) { return; }
        await EndAsync(call, "timeout", expectedStatus: "connecting");
    }
    private async Task MaxTimerAsync(Call call)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(settings.CallMaxMinutes), call.Cts.Token); } catch (OperationCanceledException) { return; }
        lock (sync) { if (call.Ended || call.Status != "active") return; }
        await EndAsync(call, "timeout");
    }

    private async Task<bool> EndAsync(Call call, string reason, string? expectedStatus = null)
    {
        MediaSide? phone, agentMedia; string? deviceId;
        lock (sync)
        {
            if (call.Ended || expectedStatus != null && call.Status != expectedStatus) return false;
            call.Ended = true; call.Status = "ended"; call.Reason = reason; call.EndedAt = Wire.Now;
            if (currentId == call.Id) currentId = null;
            phone = call.Phone; agentMedia = call.AgentMedia; deviceId = call.DeviceId;
        }
        call.AgentDecision?.TrySetResult(false);
        if (call.Conversation is { } conversation)
        {
            conversation.Ended = true;
            conversation.Utterances.Writer.TryComplete();
            conversation.Playback?.Cancel();
        }
        call.Cts.Cancel();
        log.LogInformation("Call {CallId} ({Direction}) ended: {Reason}", call.Id, call.Direction, reason);
        try
        {
            try
            {
                var duration = call.ActiveAt is { } active ? (int)Math.Min(int.MaxValue, call.EndedAt!.Value - active) : 0;
                var transcript = db.Run((c, t) => Get<CallTranscript>(c, t, "calltranscript", call.Id));
                db.Run((c, t) => { Put(c, t, "call", call.Id, new CallRecord(call.Id, call.SourceId, call.AgentId, deviceId, call.SessionId, call.SecurityLevel, call.StartedAt, call.EndedAt!.Value, "ended", reason, duration, call.Direction, transcript != null, transcript?.Partial == true, call.Target)); return 0; });
            }
            catch (Exception e) { log.LogWarning("Call {CallId} history write failed: {Type}", call.Id, e.GetType().Name); }
            if (call.Sip != null) _ = call.Sip.SendAsync(new { type = "end", callId = call.Id });
            if (call.Direction == "incoming" && call.Target == "android") _ = PushCallAsync(call, reason is "rejected" or "missed" or "disconnected" or "revoked" ? reason : "ended");
            await CallShutdown.RunAsync(
                ct => call.UsesAgent && call.Playback.IsEmpty
                    ? agents.SendControlAsync(call.AgentId, "call.ended", new { callId = call.Id, inviteId = call.InviteId, reason }, ct)
                    : Task.CompletedTask,
                ct => phone?.TextAsync(Wire.Encode(new { type = "ended", reason }), ct) ?? Task.CompletedTask,
                () => CloseSide(phone, reason), () => CloseSide(agentMedia, reason),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            lock (sync) calls.Remove(call.Id);
        }
        return true;
    }
    private static async Task CloseSide(MediaSide? side, string reason)
    {
        if (side == null) return;
        side.Outbound.Complete(); side.Cts.Cancel();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (side.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await side.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason.Length > 100 ? reason[..100] : reason, cts.Token);
            if (side.Socket.State == WebSocketState.CloseSent) await side.PeerClosed.Task.WaitAsync(cts.Token);
        }
        catch { }
        if (side.Socket.State != WebSocketState.Closed)
            try { side.Socket.Abort(); } catch { }
        side.ReceiveCts.Cancel();
        try { side.Socket.Dispose(); }
        finally { side.Closed.TrySetResult(); }
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

    private string? ResolveIcon(string sourceId, string? requestedIcon) => db.Run((c, t) =>
    {
        if (requestedIcon == null) return Get<Source>(c, t, "source", sourceId)?.IconId;
        var icon = Get<Icon>(c, t, "icon", requestedIcon);
        return icon != null && (icon.SourceId == null || icon.SourceId == sourceId) ? icon.Id : null;
    });

    private long RingDeadline(Call call) => call.StartedAt + settings.CallRingSeconds * 1000L;
    private object View(Call call) => new
    {
        call.Id, call.InviteId, call.Direction, call.Status, call.Reason, call.SourceId, call.AgentId, call.AgentName, call.SessionId, call.SecurityLevel,
        call.DeviceId, call.StartedAt, call.ActiveAt, call.EndedAt, call.Title, call.IconId, call.Mode, target = call.Target,
        hasTranscript = db.Run((c, t) => Get<CallTranscript>(c, t, "calltranscript", call.Id) != null),
        expiresAt = RingDeadline(call)
    };
    private static void RequireParticipant(Actor actor, Call call)
    {
        if (actor.Kind == "integration" && actor.Id == call.SourceId) return;
        if (actor.Kind == "web" && actor.IsOwner) return;
        if (actor.Kind == "device" && (call.DeviceId == actor.Id || (call.Direction == "incoming" && call.Target == "android"))) return;
        throw new Fault(404, "call_not_found");
    }
    private static void RequireParticipant(Actor actor, CallRecord call)
    {
        if (actor.Kind == "integration" && actor.Id == call.SourceId) return;
        if (actor.Kind == "web" && actor.IsOwner) return;
        if (actor.Kind == "device" && (call.DeviceId == actor.Id || (call.Direction == "incoming" && call.Target == "android"))) return;
        throw new Fault(404, "call_not_found");
    }
}
