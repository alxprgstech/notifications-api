using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using static Notify.Database;

namespace Notify;

internal sealed record ChatGptVoiceAttempt(string Fingerprint, string UploadId, long CreatedAt);

public sealed class ChatGptVoice(Database db, AudioService audio, NotificationService notify, SileroSpeech speech)
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    public async Task<string> Send(Actor actor, string title, string text, DateTimeOffset expiresAt, string key, CancellationToken ct)
    {
        if (actor.Kind != "integration" || !actor.Scopes.Contains("notify:send")) throw new McpException("insufficient_scope");
        if (key.Length is < 8 or > 128 || key.Any(c => c is < '!' or > '~')) throw new McpException("invalid_idempotency_key");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            throw new McpException("invalid_voice_message");

        expiresAt = expiresAt.ToUniversalTime();
        var fingerprint = Wire.Hash(Wire.Encode(new { title, text, expiresAt }));
        var id = Wire.Hash($"{actor.Id}:{key}");
        var gate = gates[(uint)StringComparer.Ordinal.GetHashCode(id) % (uint)gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            var attempt = db.Run((c, t) => Get<ChatGptVoiceAttempt>(c, t, "chatgpt_voice", id));
            if (attempt != null)
            {
                if (attempt.Fingerprint != fingerprint) throw new McpException("idempotency_conflict");
                try { return Create(attempt.UploadId); }
                catch (Fault error) when (error.Code is "audio_upload_not_found" or "audio_upload_expired") { }
            }
            var now = DateTimeOffset.UtcNow;
            if (expiresAt <= now || expiresAt > now.AddDays(30)) throw new McpException("invalid_notification");
            var upload = (await speech.WithWav(text, AudioService.MaxBytes,
                (stream, length, token) => audio.UploadAsync(actor, stream, length, token), ct)).UploadId;
            db.Run((c, t) => { Put(c, t, "chatgpt_voice", id, new ChatGptVoiceAttempt(fingerprint, upload, Wire.Now)); return 0; });
            return Create(upload);
        }
        catch (Fault error) { throw new McpException(error.Code); }
        finally { gate.Release(); }

        string Create(string uploadId) => Wire.Encode(notify.Create(actor,
            new CreateNotification("voice", title, "", ExpiresAt: expiresAt, VoiceUpload: new VoiceInput(uploadId)), key));
    }

}

public sealed class SileroSpeech(IHttpClientFactory clients)
{
    internal async Task<T> WithWav<T>(string text, long maxBytes,
        Func<Stream, long, CancellationToken, Task<T>> consume, CancellationToken ct)
    {
        using var client = clients.CreateClient("silero");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "synthesize")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var length = response.Content.Headers.ContentLength;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "audio/wav" ||
                length is not > 44 || length > maxBytes)
                throw new McpException("silero_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await consume(stream, length.Value, deadline.Token);
        }
        catch (HttpRequestException) { throw new McpException("silero_unavailable"); }
        catch (IOException) { throw new McpException("silero_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("silero_timeout"); }
    }
}
