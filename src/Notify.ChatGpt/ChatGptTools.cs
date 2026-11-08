using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;

namespace Notify.ChatGpt;

[McpServerToolType]
internal sealed class ChatGptTools(NotifyApi notify, VoiceSender voice, IHttpContextAccessor context)
{
    [McpServerTool(Name = "send_notification"), Description("Send a text notification to the owner's Android devices.")]
    public Task<JsonElement> SendNotification(string title, string body, string idempotencyKey, CancellationToken ct,
        string? iconId = null, string? correlationId = null) =>
        Create(AdapterOAuth.Send, new { kind = "message", title, body, iconId, correlationId }, idempotencyKey, ct);

    [McpServerTool(Name = "request_confirmation"), Description("Request a decision on Android. Elevated requests require verification on the phone. Pending, expired, cancelled or denied are never approval.")]
    public Task<JsonElement> RequestConfirmation(string title, string body, DateTimeOffset expiresAt, string idempotencyKey,
        CancellationToken ct, string securityLevel = "standard", bool allowCustom = true, string? iconId = null, string? correlationId = null) =>
        Create(AdapterOAuth.Create, new { kind = "confirmation", title, body, expiresAt, securityLevel, allowCustom, iconId, correlationId }, idempotencyKey, ct);

    [McpServerTool(Name = "request_form"), Description("Ask the owner to complete a form on Android. fieldsJson is a JSON array of Notify form fields. Elevated forms require verification on the phone.")]
    public async Task<JsonElement> RequestForm(string title, string body, DateTimeOffset expiresAt, string idempotencyKey,
        string fieldsJson, CancellationToken ct, string securityLevel = "standard", string? iconId = null, string? correlationId = null)
    {
        await Require(AdapterOAuth.Create, ct);
        JsonElement fields;
        try { fields = JsonSerializer.Deserialize<JsonElement>(fieldsJson); }
        catch (JsonException) { throw new McpException("invalid_form"); }
        if (fields.ValueKind != JsonValueKind.Array) throw new McpException("invalid_form");
        return await notify.Create(new { kind = "form", title, body, expiresAt, securityLevel, iconId, correlationId, form = new { fields } }, idempotencyKey, ct);
    }

    [McpServerTool(Name = "get_request", ReadOnly = true), Description("Read the current result of a request created by this Notify source.")]
    public async Task<JsonElement> GetRequest(string id, CancellationToken ct)
    {
        await Require(AdapterOAuth.Read, ct);
        return await notify.Send(HttpMethod.Get, PathFor(id), null, null, ct);
    }

    [McpServerTool(Name = "wait_for_response", ReadOnly = true), Description("Wait up to 25 seconds. A pending response means the owner has not decided.")]
    public async Task<JsonElement> WaitForResponse(string id, CancellationToken ct, int seconds = 20)
    {
        await Require(AdapterOAuth.Read, ct);
        if (seconds is < 0 or > 25) throw new McpException("invalid_wait_seconds");
        return await notify.Send(HttpMethod.Get, $"{PathFor(id)}/wait?seconds={seconds}", null, null, ct);
    }

    [McpServerTool(Name = "cancel_request", Destructive = true), Description("Cancel a pending request created by this Notify source.")]
    public async Task<JsonElement> CancelRequest(string id, CancellationToken ct)
    {
        await Require(AdapterOAuth.Cancel, ct);
        return await notify.Send(HttpMethod.Post, PathFor(id) + "/cancel", null, null, ct);
    }

    [McpServerTool(Name = "send_voice"), Description("Synthesize Russian text locally with Silero baya and send it as a standard Android voice notification. Use the same idempotencyKey when retrying.")]
    public async Task<JsonElement> SendVoice(string title, string text, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
    {
        await Require(AdapterOAuth.Send, ct);
        return await voice.Send(title, text, expiresAt, idempotencyKey, ct);
    }

    private async Task<JsonElement> Create(string scope, object body, string key, CancellationToken ct)
    {
        await Require(scope, ct);
        return await notify.Create(body, key, ct);
    }

    private Task Require(string scope, CancellationToken ct)
    {
        if (context.HttpContext?.User.HasScope(scope) != true) throw new McpException("insufficient_scope");
        return notify.RequireSource(scope, ct);
    }

    private static string PathFor(string id) =>
        id.Length == 32 && id.All(char.IsAsciiHexDigit)
            ? $"api/v1/notifications/{id}" : throw new McpException("notification_not_found");
}

internal sealed class NotifyApi(HttpClient client, AdapterSettings settings)
{
    public async Task RequireSource(string scope, CancellationToken ct)
    {
        var session = await Send(HttpMethod.Get, "api/v1/session", null, null, ct);
        if (!session.TryGetProperty("actor", out var actor) ||
            actor.GetProperty("kind").GetString() != "integration" ||
            actor.GetProperty("id").GetString() != settings.NotifySourceId ||
            !actor.GetProperty("scopes").EnumerateArray().Any(item => item.GetString() == scope))
            throw new McpException("notify_source_scope_required");
    }

    public Task<JsonElement> Create(object body, string key, CancellationToken ct) =>
        Send(HttpMethod.Post, "api/v1/notifications", JsonContent.Create(body), key, ct);

    public async Task<string> Upload(byte[] wav, CancellationToken ct)
    {
        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var response = await Send(HttpMethod.Post, "api/v1/audio/uploads", content, null, ct);
        return response.TryGetProperty("uploadId", out var id) && id.GetString() is { Length: 32 } value
            ? value : throw new McpException("invalid_notify_response");
    }

    public async Task<JsonElement> Send(HttpMethod method, string path, HttpContent? content, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (key != null)
        {
            ValidateKey(key);
            request.Headers.Add("Idempotency-Key", key);
        }
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json"))
                throw new McpException(response.IsSuccessStatusCode ? "invalid_notify_response" : $"notify_http_{(int)response.StatusCode}");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (response.IsSuccessStatusCode) return body;
            throw new McpException(SafeError(body) ?? $"notify_http_{(int)response.StatusCode}");
        }
        catch (JsonException) { throw new McpException("invalid_notify_response"); }
        catch (HttpRequestException) { throw new McpException("notify_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("notify_timeout"); }
    }

    private static string? SafeError(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("code", out var code) &&
        code.ValueKind == JsonValueKind.String && code.GetString() is { Length: > 0 and <= 80 } safe &&
        safe.All(c => char.IsAsciiLetterLower(c) || c == '_') ? safe : null;

    public static void ValidateKey(string key)
    {
        if (key.Length is < 8 or > 128 || key.Any(c => c is < '!' or > '~')) throw new McpException("invalid_idempotency_key");
    }
}

internal sealed class VoiceLocks
{
    private readonly SemaphoreSlim[] locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public SemaphoreSlim For(string key) => locks[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)locks.Length];
}

internal sealed class VoiceSender(NotifyApi notify, IHttpClientFactory clients, AdapterDb db, VoiceLocks locks)
{
    public async Task<JsonElement> Send(string title, string text, DateTimeOffset expiresAt, string key, CancellationToken ct)
    {
        NotifyApi.ValidateKey(key);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(text) || text.Length > 1500)
            throw new McpException("invalid_voice_message");
        expiresAt = expiresAt.ToUniversalTime();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { title, text, expiresAt })));
        var gate = locks.For(key);
        await gate.WaitAsync(ct);
        try
        {
            var attempt = await db.VoiceAttempts.FindAsync([key], ct);
            if (attempt != null && attempt.Fingerprint != fingerprint) throw new McpException("idempotency_conflict");
            if (attempt?.UploadId != null)
            {
                try { return await Create(title, expiresAt, key, attempt.UploadId, ct); }
                catch (McpException error) when (error.Message is "audio_upload_not_found" or "audio_upload_expired") { }
            }

            var now = DateTimeOffset.UtcNow;
            if (expiresAt <= now || expiresAt > now.AddDays(30)) throw new McpException("invalid_notification");
            var wav = await Synthesize(text, ct);
            var uploadId = await notify.Upload(wav, ct);
            if (attempt == null)
            {
                attempt = new VoiceAttempt { Key = key, Fingerprint = fingerprint };
                db.VoiceAttempts.Add(attempt);
            }
            attempt.UploadId = uploadId;
            await db.SaveChangesAsync(ct);
            return await Create(title, expiresAt, key, uploadId, ct);
        }
        finally { gate.Release(); }
    }

    private Task<JsonElement> Create(string title, DateTimeOffset expiresAt, string key, string uploadId, CancellationToken ct) =>
        notify.Create(new { kind = "voice", title, body = "", expiresAt, voiceUpload = new { uploadId } }, key, ct);

    private async Task<byte[]> Synthesize(string text, CancellationToken ct)
    {
        using var client = clients.CreateClient("silero");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "synthesize") { Content = JsonContent.Create(new { text }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            const int maxBytes = 10 * 1024 * 1024;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "audio/wav" ||
                response.Content.Headers.ContentLength is not (> 44 and <= maxBytes))
                throw new McpException("silero_unavailable");
            await response.Content.LoadIntoBufferAsync(maxBytes, deadline.Token);
            return await response.Content.ReadAsByteArrayAsync(deadline.Token);
        }
        catch (HttpRequestException) { throw new McpException("silero_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("silero_timeout"); }
    }
}
