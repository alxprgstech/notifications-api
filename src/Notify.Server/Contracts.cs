using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notify;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidOperationException("Invalid persisted value");
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Secret() => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Canonical(FormValues values) => Encode(values.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    public static bool SameForm(FormValues left, FormValues right) => Canonical(left) == Canonical(right);
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed record FormOption(string Id, string Label);
public sealed record FormField(string Id, string Label, string Type, bool Required = false, string? Description = null, int? MaxLength = null, double? Min = null, double? Max = null, List<FormOption>? Options = null);
public sealed record FormSpec(List<FormField> Fields);
public sealed record FormValues(Dictionary<string, JsonElement> Values);
public sealed record FormAnswer(Dictionary<string, JsonElement> Values);
public sealed record VoiceAttachment(string AudioId, int DurationMs, int SizeBytes, long ExpiresAt, string? Transcript = null, bool TranscriptProtected = false);
public sealed record VoiceInput(string UploadId);
public sealed record VoiceSend(string UploadId, string? NotificationId = null, DateTimeOffset? ExpiresAt = null);
public sealed record AudioGrantInput(string ChallengeId, string Signature);
public sealed record AudioAsset(string Id, string SourceId, string? DeviceId, string Status, int SizeBytes, int DurationMs, long CreatedAt, long ExpiresAt, string? NotificationId = null);
public sealed record AudioGrant(string Id, string AudioId, string DeviceId, long ExpiresAt, bool Used = false);
public sealed record VoiceMessage(string Id, string SourceId, string DeviceId, string? NotificationId, string AudioId, int DurationMs, int SizeBytes, long CreatedAt, long ExpiresAt, string? Transcript = null, string? TranscriptionStatus = null, string? TranscriptionError = null);
public sealed record CreateNotification(string Kind, string Title, string Body, string SecurityLevel = "standard", DateTimeOffset? ExpiresAt = null, bool AllowCustom = true, string? IconId = null, string? CorrelationId = null, FormSpec? Form = null, VoiceInput? VoiceUpload = null, VoiceAttachment? Voice = null, string? VoiceText = null, string? VoiceTranscript = null);
public sealed record Answer(string Decision, string? Text = null);
public sealed record SubmitAnswer(Answer? Answer = null, string? ChallengeId = null, string? Signature = null, FormValues? Form = null);
public sealed record Notification(string Id, string SourceId, CreateNotification Content, long CreatedAt, string Status, Answer? Answer = null, long? AnsweredAt = null, string? ManagementId = null, bool PreviewOnly = false, FormAnswer? FormAnswer = null);
public sealed record Source(string Id, string Name, string[] Scopes, string Preview = "hidden", string? IconId = null, string? WebhookUrl = null, string? ProtectedWebhookSecret = null, bool Revoked = false);
public sealed record Device(string Id, string Name, string PublicKey, string? FcmToken = null, bool Revoked = false);
public sealed record Challenge(string Id, string NotificationId, string DeviceId, string Payload, long ExpiresAt, Answer? Answer = null, FormValues? Form = null, string? AudioId = null);
public sealed record Delivery(string NotificationId, string DeviceId, string Status, long UpdatedAt);
public sealed record Job(string Id, string Kind, string NotificationId, string TargetId, string Body, int Attempts = 0);
public sealed record EnrollDevice(string Name, string PublicKey, string? FcmToken = null, bool Recovery = false);
public sealed record Enrollment(string Id, EnrollDevice Device, string Owner, long ExpiresAt, string? ProtectedToken = null, bool Completed = false);
public sealed record StepUpState(string Id, string Kind, string TargetId, string Owner, string PayloadHash, string State, string Nonce, string Verifier, long ExpiresAt);
public sealed record Agent(string Id, string SourceId, string Name, string[] Capabilities, long CreatedAt, long? LastSeenAt = null, bool Revoked = false, string[]? CallProtocols = null, string? CallProtocol = null);
public sealed record AgentSession(string Id, string Name);
public sealed record CallStart(string SourceId, string? SessionId = null, string SecurityLevel = "standard", string? ChallengeId = null, string? Signature = null, string Mode = "one_shot");
public sealed record CallPlaybackInput(string IdempotencyKey, string Title, string Text, string? IconId = null, string Target = "android");
public sealed record CallConversationInput(string IdempotencyKey, string Title, string Target = "android", string? SessionId = null);
public sealed record CallSpeakInput(string IdempotencyKey, string Text);
public sealed record CallChallengeInput(string SourceId, string? SessionId = null, string SecurityLevel = "standard");
public sealed record CallAcceptInput(string? ChallengeId = null, string? Signature = null);
public sealed record CallChallenge(string Id, string DeviceId, string Payload, long ExpiresAt);
public sealed record CallChallengeProof(string Protocol, string ChallengeId, string DeviceId, string Direction, string Action, string SourceId, string AgentId, string? SessionId, string? CallId, string? InviteId, string SecurityLevel, string Nonce, long ExpiresAt);
public sealed record CallTurn(string Id, string Speaker, string Text, long At, bool Final = true, string? Status = null, string? Error = null);
public sealed record CallTranscript(string CallId, string ProtectedTurns, bool Partial, string? Error, long UpdatedAt);
public sealed record CallRecord(string Id, string SourceId, string AgentId, string? DeviceId, string? SessionId, string SecurityLevel, long StartedAt, long EndedAt, string Status, string? Reason, int DurationMs, string Direction = "outgoing", bool HasTranscript = false, bool TranscriptPartial = false, string Target = "android");
public sealed record AgentCommand(string Id, string AgentId, string SourceId, string Type, string Payload, long CreatedAt, long ExpiresAt, string Status = "queued", int Attempts = 0, long? AckedAt = null, string? CorrelationId = null, string? SessionId = null);
public sealed record ManagementInput(string Action, string? Name = null, string? TargetId = null, string[]? Scopes = null, string? Preview = null, string? IconId = null, string? WebhookUrl = null, string[]? Capabilities = null, long? ExpectedRevision = null);
public sealed record Management(string Id, string SessionId, ManagementInput Input, long ExpiresAt, string Status = "pending", string? ProtectedResult = null);
public sealed record Icon(string Id, string? SourceId, string ContentType, string FileName);
public sealed record Actor(string Kind, string Id, string[] Scopes, string? SessionId = null)
{
    public bool IsOwner => Kind is "device" or "web";
    public void RequireOwner() { if (!IsOwner) throw new Fault(403, "owner_required"); }
    public void RequireDevice() { if (Kind != "device") throw new Fault(403, "device_required"); }
    public void RequireScope(string scope) { if (Kind != "integration" || !Scopes.Contains(scope)) throw new Fault(403, "scope_required"); }
}
public sealed class Fault(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed class Settings
{
    public string DataPath { get; init; } = "data";
    public string PublicUrl { get; init; } = "https://notify.alxprgs.tech";
    public string SsoIssuer { get; init; } = "https://auth.alxprgs.tech";
    public string OwnerSub { get; init; } = "";
    public string WebClientId { get; init; } = "notify-web";
    public string WebClientSecret { get; init; } = "";
    public string AndroidClientId { get; init; } = "notify-android";
    public string SileroUrl { get; init; } = "http://silero:8080";
    public string SttUrl { get; init; } = "";
    public int ConversationUtteranceSeconds { get; init; } = 15;
    public int ConversationSilenceMilliseconds { get; init; } = 700;
    public string? FirebaseCredentials { get; init; }
    public string? TrustedProxyNetwork { get; init; }
    public string[] WebhookHosts { get; init; } = [];
    public string[] AndroidSigningFingerprints { get; init; } = [];
    public int RetentionDays { get; init; } = 30;
    public int SourceDailyLimit { get; init; } = 2000;
    public int AgentHeartbeatSeconds { get; init; } = 30;
    public int AgentOfflineSeconds { get; init; } = 90;
    public int AgentSendTimeoutSeconds { get; init; } = 5;
    public long AudioQuotaBytes { get; init; } = 512L * 1024 * 1024;
    public int AudioUploadMinutes { get; init; } = 15;
    public int AudioMessageTtlDays { get; init; } = 7;
    public int CallRingSeconds { get; init; } = 45;
    public int CallMaxMinutes { get; init; } = 30;
    public int CallMediaConnectSeconds { get; init; } = 10;
    public int CallQueueFrames { get; init; } = 100;
    public int CallAudioStartDelayMs { get; init; } = 3000;
    public bool SsoConfigured => Guid.TryParse(OwnerSub, out _) && WebClientSecret.Length >= 16;
    public void Validate(bool development)
    {
        foreach (var value in new[] { PublicUrl, SsoIssuer })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                (uri.Scheme != "https" && !(development && uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidOperationException("PublicUrl/SsoIssuer must be HTTPS origins (loopback HTTP only in Development)");
        }
        if (!development && !SsoConfigured) throw new InvalidOperationException("Configure owner and SSO client before production startup");
        if (!string.IsNullOrEmpty(TrustedProxyNetwork) &&
            (!System.Net.IPNetwork.TryParse(TrustedProxyNetwork, out var network) || network.PrefixLength == 0))
            throw new InvalidOperationException("TrustedProxyNetwork must be an explicit proxy CIDR");
        if (RetentionDays != 30) throw new InvalidOperationException("RetentionDays must be 30 for contract v1");
        if (SourceDailyLimit is < 1 or > 100000) throw new InvalidOperationException("Invalid SourceDailyLimit");
        if (AgentHeartbeatSeconds is < 1 or > 60 || AgentOfflineSeconds < AgentHeartbeatSeconds * 2 || AgentOfflineSeconds > 600 || AgentSendTimeoutSeconds is < 1 or > 30) throw new InvalidOperationException("Invalid agent heartbeat/offline limits");
        if (AudioQuotaBytes is < 1L * 1024 * 1024 or > 50L * 1024 * 1024 * 1024 || AudioUploadMinutes is < 1 or > 60 || AudioMessageTtlDays is < 1 or > 30) throw new InvalidOperationException("Invalid audio quota/upload/retention limits");
        if (CallAudioStartDelayMs is < 0 or > 10000) throw new InvalidOperationException("Invalid call audio start delay");
        if (CallRingSeconds is < 1 or > 120 || CallMaxMinutes is < 1 or > 120 || CallMediaConnectSeconds is < 1 or > 60 || CallQueueFrames is < 1 or > 200) throw new InvalidOperationException("Invalid call limits");
        if (ConversationUtteranceSeconds is < 3 or > 30 || ConversationSilenceMilliseconds is < 300 or > 2000) throw new InvalidOperationException("Invalid conversation limits");
        if (!string.IsNullOrEmpty(SttUrl) && (!Uri.TryCreate(SttUrl, UriKind.Absolute, out var stt) || stt.Scheme is not ("http" or "https") || stt.UserInfo.Length != 0 || stt.Query.Length != 0 || stt.Fragment.Length != 0)) throw new InvalidOperationException("SttUrl must be an HTTP(S) base URL without credentials or query");
    }
}
