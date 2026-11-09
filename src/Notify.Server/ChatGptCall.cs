using ModelContextProtocol;
using static Notify.Database;

namespace Notify;

internal sealed record ChatGptCallAttempt(string Fingerprint, string CallId, long CreatedAt);
internal sealed record ChatGptSayAttempt(string Fingerprint, string CallId, long CreatedAt);

public sealed class ChatGptCall(Database db, CallService calls, SileroSpeech speech)
{
    internal const string Scope = "calls:start";
    internal const string AgentScope = "agents:connect";
    internal const string AttemptKind = "chatgpt_call";
    internal const string ConversationAttemptKind = "chatgpt_conversation";
    internal const string SayAttemptKind = "chatgpt_say";
    private const int MaxPcmBytes = 60 * 32000;
    private readonly SemaphoreSlim synthesis = new(1, 1);

    public Task<string> StartConversation(Actor actor, string title, string key, CancellationToken ct)
    {
        if (actor.Kind != "integration" || !actor.Scopes.Contains(Scope)) throw new McpException("insufficient_scope");
        if (key.Length is < 8 or > 128 || key.Any(c => c is < '!' or > '~')) throw new McpException("invalid_idempotency_key");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl)) throw new McpException("invalid_call");
        var fingerprint = Wire.Hash(Wire.Encode(new { title, key }));
        var attemptId = Wire.Hash($"{actor.Id}:{key}");
        var attempt = db.Run((c, t) => Get<ChatGptCallAttempt>(c, t, ConversationAttemptKind, attemptId));
        if (attempt != null)
        {
            if (attempt.Fingerprint != fingerprint) throw new McpException("idempotency_conflict");
            return Task.FromResult(Wire.Encode(calls.State(actor, attempt.CallId)));
        }
        try
        {
            calls.EnsureIdle();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Wire.Encode(calls.StartBotConversation(actor, title, attemptId, fingerprint)));
        }
        catch (Fault error) { throw new McpException(error.Code); }
    }

    public string Transcript(Actor actor, string callId)
    {
        try { return Wire.Encode(calls.Transcript(actor, callId)); }
        catch (Fault error) { throw new McpException(error.Code); }
    }

    public string Speak(Actor actor, string callId, string text, string key)
    {
        if (actor.Kind != "integration" || !actor.Scopes.Contains(Scope)) throw new McpException("insufficient_scope");
        if (key.Length is < 8 or > 128 || key.Any(c => c is < '!' or > '~')) throw new McpException("invalid_idempotency_key");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r'))) throw new McpException("invalid_call");
        var fingerprint = Wire.Hash(Wire.Encode(new { callId, text }));
        var attemptId = Wire.Hash($"{actor.Id}:{key}");
        var existing = db.Run((c, t) => Get<ChatGptSayAttempt>(c, t, SayAttemptKind, attemptId));
        if (existing != null)
        {
            if (existing.Fingerprint != fingerprint || existing.CallId != callId) throw new McpException("idempotency_conflict");
            return Wire.Encode(new { ok = true, callId, turnId = (string?)null });
        }
        try { calls.SayFromActor(actor, callId, text); }
        catch (Fault error) { throw new McpException(error.Code); }
        db.Run((c, t) => { Put(c, t, SayAttemptKind, attemptId, new ChatGptSayAttempt(fingerprint, callId, Wire.Now)); return 0; });
        return Wire.Encode(new { ok = true, callId });
    }

    public async Task<string> Start(Actor actor, string title, string text, string key, CancellationToken ct)
    {
        if (actor.Kind != "integration" || (!actor.Scopes.Contains(Scope) && !actor.Scopes.Contains(AgentScope)))
            throw new McpException("insufficient_scope");
        if (key.Length is < 8 or > 128 || key.Any(c => c is < '!' or > '~')) throw new McpException("invalid_idempotency_key");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(text) || text.Length > 300 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r')))
            throw new McpException("invalid_call");

        var (agentId, agentName) = await ResolveAgent(actor, ct);
        var fingerprint = Wire.Hash(Wire.Encode(new { title, text, agentId }));
        var attemptId = Wire.Hash($"{actor.Id}:{key}");
        // Hold only during synthesis and publication, never while the phone rings or plays audio.
        await synthesis.WaitAsync(ct);
        try
        {
            var attempt = db.Run((c, t) => Get<ChatGptCallAttempt>(c, t, AttemptKind, attemptId));
            if (attempt != null)
            {
                if (attempt.Fingerprint != fingerprint) throw new McpException("idempotency_conflict");
                return Wire.Encode(calls.State(actor, attempt.CallId));
            }
            calls.EnsureIdle();
            var pcm = await speech.WithWav(text, MaxPcmBytes + 4096, async (stream, length, token) =>
            {
                var wav = new byte[(int)length];
                await stream.ReadExactlyAsync(wav, token);
                using var input = new MemoryStream(wav, writable: false);
                AudioService.ValidateWav(input, length, out var offset, out var count);
                if (count > MaxPcmBytes) throw new Fault(400, "audio_too_long");
                return wav.AsMemory(offset, count);
            }, ct);
            ct.ThrowIfCancellationRequested();
            return Wire.Encode(calls.StartPlayback(actor, title, text, pcm, attemptId, fingerprint, agentId, agentName));
        }
        catch (Fault error) { throw new McpException(error.Code); }
        catch (SpeechException error) { throw new McpException(error.Code); }
        finally { synthesis.Release(); }
    }

    public async Task<object> StartRest(Actor actor, string title, string text, string key, CancellationToken ct)
    {
        try
        {
            var json = await Start(actor, title, text, key, ct);
            return Wire.Decode<System.Text.Json.JsonElement>(json);
        }
        catch (McpException error)
        {
            throw new Fault(error.Message switch
            {
                "insufficient_scope" => 403,
                "invalid_idempotency_key" or "invalid_call" => 400,
                "busy" or "idempotency_conflict" or "agent_offline" or "agent_unsupported" or "call_protocol_unsupported" => 409,
                "silero_unavailable" or "silero_timeout" => 503,
                _ => 400,
            }, error.Message);
        }
    }

    private async Task<(string? AgentId, string? AgentName)> ResolveAgent(Actor actor, CancellationToken ct)
    {
        if (actor.Scopes.Contains(Scope)) return (null, null);
        var agent = db.Run((c, t) =>
        {
            var source = Get<Source>(c, t, "source", actor.Id) ?? throw new Fault(403, "insufficient_scope");
            if (source.Revoked) throw new Fault(403, "insufficient_scope");
            return All<Agent>(c, t, "agent").FirstOrDefault(a => a.SourceId == actor.Id && !a.Revoked) ?? throw new Fault(409, "agent_not_registered");
        });
        var online = calls.AgentOnline(agent.Id) ?? throw new Fault(409, "agent_offline");
        if (!online.Capabilities.Contains("realtime_audio", StringComparer.Ordinal)) throw new Fault(409, "agent_unsupported");
        if (online.CallProtocol != CallService.Protocol) throw new Fault(409, "call_protocol_unsupported");
        await Task.CompletedTask;
        return (agent.Id, online.Agent.Name);
    }
}
