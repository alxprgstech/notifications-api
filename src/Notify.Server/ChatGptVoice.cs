using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using static Notify.Database;

namespace Notify;

internal sealed record ChatGptVoiceAttempt(string Fingerprint, string UploadId, long CreatedAt);

/// <summary>The server-side speech failure is a stable code that each caller maps to its own API.</summary>
public sealed class SpeechException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

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
        catch (SpeechException error) { throw new McpException(error.Code); }
        finally { gate.Release(); }

        string Create(string uploadId) => Wire.Encode(notify.Create(actor,
            new CreateNotification("voice", title, "", ExpiresAt: expiresAt, VoiceUpload: new VoiceInput(uploadId), VoiceTranscript: text), key));
    }

}

/// <summary>Text-to-Silero for REST, MCP and the SDK. A WAV is streamed, never buffered as a file.</summary>
public sealed class SileroSpeech(IHttpClientFactory clients)
{
    internal const string UnsupportedLanguage = "unsupported_tts_language";

    internal static void ValidateText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && rune.Value is not (>= 'А' and <= 'я' or 'Ё' or 'ё'))
                throw new SpeechException(UnsupportedLanguage);
        }
    }

    internal async Task<T> WithWav<T>(string text, long maxBytes,
        Func<Stream, long, CancellationToken, Task<T>> consume, CancellationToken ct)
    {
        ValidateText(text);
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
                throw new SpeechException("silero_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await consume(stream, length.Value, deadline.Token);
        }
        catch (HttpRequestException) { throw new SpeechException("silero_unavailable"); }
        catch (IOException) { throw new SpeechException("silero_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SpeechException("silero_timeout"); }
    }
}

/// <summary>Private local Vosk recognizer. Audio is posted in memory and is never persisted here.</summary>
public sealed class SttSpeech(IHttpClientFactory clients, Settings settings)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(settings.SttUrl);
    public async Task<string> RecognizeAsync(ReadOnlyMemory<byte> wav, CancellationToken ct)
    {
        if (!Enabled) throw new SpeechException("stt_unavailable");
        using var client = clients.CreateClient("stt");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var content = new ByteArrayContent(wav.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var response = await client.PostAsync("recognize", content, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new SpeechException("stt_unavailable");
            var payload = await response.Content.ReadAsStringAsync(deadline.Token);
            using var document = JsonDocument.Parse(payload);
            var text = document.RootElement.TryGetProperty("text", out var value) ? value.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(text) || text.Length > 16000) throw new SpeechException("stt_empty");
            return text;
        }
        catch (SpeechException) { throw; }
        catch (HttpRequestException) { throw new SpeechException("stt_unavailable"); }
        catch (IOException) { throw new SpeechException("stt_unavailable"); }
        catch (JsonException) { throw new SpeechException("stt_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SpeechException("stt_timeout"); }
    }
}

/// <summary>Normal path: text in, server-side Silero WAV, existing notification/outbox transaction.</summary>
public sealed class VoiceService(NotificationService notify, SileroSpeech speech, SttSpeech stt, AudioService audio, Settings settings, ILogger<VoiceService> log)
{
    public async Task<Notification> CreateAsync(Actor actor, CreateNotification input, string key, CancellationToken ct)
    {
        if (input.Kind != "voice" || input.VoiceText == null) return notify.Create(actor, input, key);
        actor.RequireScope("notify:send");
        var text = input.VoiceText;
        NotificationService.Validate(input with { VoiceUpload = new VoiceInput("synthesized"), VoiceText = null, VoiceTranscript = text });
        var hash = NotificationService.HashContent(input);
        var existing = notify.TryIdempotent(actor, key, hash);
        if (existing != null) return existing;

        string uploadId;
        try
        {
            uploadId = await speech.WithWav(text, AudioService.MaxBytes,
                async (stream, length, token) => (await audio.UploadAsync(actor, stream, length, token)).UploadId, ct);
        }
        catch (SpeechException error) { throw new Fault(error.Code == SileroSpeech.UnsupportedLanguage ? 422 : 503, error.Code); }

        try
        {
            var created = notify.Create(actor, input with
            {
                VoiceText = null,
                VoiceUpload = new VoiceInput(uploadId),
                VoiceTranscript = text,
            }, key, hash);
            if (created.Content.Voice?.AudioId != uploadId) audio.DeleteAsset(uploadId);
            return created;
        }
        catch
        {
            audio.DeleteAsset(uploadId);
            throw;
        }
    }

    public async Task<VoiceMessage> RecognizeVoiceReplyAsync(VoiceMessage message, CancellationToken ct)
    {
        var path = AudioService.FinalPath(settings, message.AudioId);
        if (!File.Exists(path)) return message with { TranscriptionStatus = "missing", TranscriptionError = "audio_missing" };
        try
        {
            var wav = await File.ReadAllBytesAsync(path, ct);
            var text = await stt.RecognizeAsync(wav, ct);
            return message with { Transcript = text, TranscriptionStatus = "complete" };
        }
        catch (SpeechException error)
        {
            log.LogWarning("Voice reply transcription failed: {Code}", error.Code);
            return message with { TranscriptionStatus = "failed", TranscriptionError = error.Code };
        }
    }
}
