using ModelContextProtocol;
using static Notify.Database;

namespace Notify;

internal sealed record ChatGptCallAttempt(string Fingerprint, string CallId, long CreatedAt);

public sealed class ChatGptCall(Database db, CallService calls, SileroSpeech speech)
{
    internal const string Scope = "calls:start";
    internal const string AttemptKind = "chatgpt_call";
    private const int MaxPcmBytes = 60 * 32000;
    private readonly SemaphoreSlim synthesis = new(1, 1);

    public async Task<string> Start(Actor actor, string title, string text, string key, CancellationToken ct)
    {
        if (actor.Kind != "integration" || !actor.Scopes.Contains(Scope)) throw new McpException("insufficient_scope");
        if (key.Length is < 8 or > 128 || key.Any(c => c is < '!' or > '~')) throw new McpException("invalid_idempotency_key");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(text) || text.Length > 300)
            throw new McpException("invalid_call");

        var fingerprint = Wire.Hash(Wire.Encode(new { title, text }));
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
            return Wire.Encode(calls.StartPlayback(actor, title, pcm, attemptId, fingerprint));
        }
        catch (Fault error) { throw new McpException(error.Code); }
        finally { synthesis.Release(); }
    }
}
