using System.Text.Json;

namespace Notify;

// Response shapes used only to describe the HTTP contract in /openapi/v1.json.
// They mirror the anonymous objects returned by the endpoints; changing a runtime
// response requires updating the matching record and the operation description.

public sealed record SessionResponse(Actor Actor, string CsrfToken);
public sealed record HealthLiveResponse(string Status);
public sealed record HealthReadyResponse(string Status, bool? Sso = null, bool? Fcm = null);
public sealed record CapabilitiesResponse(bool Message, bool Confirmation, bool Elevated, bool Forms, bool VoiceMessages, bool Calls, bool Agents,
    bool VoiceText = false, bool Transcription = false, bool CallTranscripts = false, bool Conversation = false);
public sealed record ProtocolVersionsResponse(string Answer, string Form, string Audio, string Agent, string Call);
public sealed record MinClientVersionResponse(string Forms, string VoiceMessages, string Calls, string? Conversation = null);
public sealed record ConfigResponse(string Version, string SsoIssuer, string AndroidClientId, string PublicUrl, bool SsoConfigured,
    CapabilitiesResponse Capabilities, ProtocolVersionsResponse Protocols, MinClientVersionResponse MinClientVersion);
public sealed record AssetLinkTarget(string Namespace, string PackageName, string[] Sha256CertFingerprints);
public sealed record AssetLink(string[] Relation, AssetLinkTarget Target);
public sealed record ProblemResponse(string? Type, string Title, int Status);
public sealed record EnrollmentStartResponse(string EnrollmentId, string AuthorizationUrl, long ExpiresAt);
public sealed record EnrollmentStateResponse(string Status);
public sealed record EnrollmentCredentialResponse(string DeviceId, string Token, long ExpiresAt);
public sealed record PageCursor(long Before, string BeforeId);
public sealed record NotificationPage(IReadOnlyList<Notification> Items, PageCursor? Next);
public sealed record SyncResponse(long Cursor, IReadOnlyList<Notification> Items, IReadOnlyList<string> Deleted, bool HasMore, bool Reset);
public sealed record VoiceUploadResponse(string UploadId, int DurationMs, int SizeBytes, long ExpiresAt);
public sealed record VoiceCreatedResponse(string Id, string Status, long ExpiresAt, string InReplyTo, string? Transcript = null, string? TranscriptionStatus = null);
public sealed record VoiceStatusResponse(string Id, string Status, long CreatedAt, long ExpiresAt, int DurationMs, string? NotificationId, bool AudioAvailable,
    string? Transcript = null, string? TranscriptionStatus = null, string? TranscriptionError = null);
public sealed record AudioGrantResponse(string Grant, long ExpiresAt, string? Transcript = null);
public sealed record CallChallengeResponse(string Id, string Payload, long ExpiresAt);
public sealed record CallViewResponse(string Id, string InviteId, string Direction, string Status, string? Reason, string SourceId, string AgentId,
    string AgentName, string? SessionId, string SecurityLevel, string? DeviceId, long StartedAt, long? ActiveAt, long? EndedAt,
    string Title, string? IconId, string Mode, bool HasTranscript, long ExpiresAt, string Target = "android");
public sealed record CallTranscriptResponse(string CallId, IReadOnlyList<CallTurn> Turns, bool Partial, string? Error, long UpdatedAt);
public sealed record AgentViewResponse(string Id, string SourceId, string Name, string[] Capabilities, string[] CallProtocols, string? CallProtocol,
    bool Revoked, bool Online, long? LastSeenAt, AgentSession[] Sessions);
public sealed record SourceViewResponse(string Id, string Name, string[] Scopes, string Preview, string? IconId, string? WebhookUrl, bool Revoked);
public sealed record ManagementStatusResponse(string Id, string Status, JsonElement? Result = null);
public sealed record IconResponse(string Id);
public sealed record ErrorResponse(string Code);
