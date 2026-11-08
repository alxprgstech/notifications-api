using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using static Notify.Database;

namespace Notify;

public sealed class NotificationService(Database db, IDataProtectionProvider protection, Settings settings, AgentHub agents, AudioService audio, CallService calls)
{
    // Bound history memory before materializing rows; full content is fetched by ID.
    private const string PreviewSql = "CASE WHEN json_type(data,'$.answer')='object' THEN json_set(data,'$.content.body',substr(json_extract(data,'$.content.body'),1,1000),'$.answer.text',substr(json_extract(data,'$.answer.text'),1,512),'$.previewOnly',json('true')) WHEN json_type(data,'$.formAnswer')='object' THEN json_set(data,'$.content.body',substr(json_extract(data,'$.content.body'),1,1000),'$.formAnswer.values',json('{}'),'$.previewOnly',json('true')) ELSE json_set(data,'$.content.body',substr(json_extract(data,'$.content.body'),1,1000),'$.previewOnly',json('true')) END";
    private readonly IDataProtector secrets = protection.CreateProtector("notify.v1.secrets");
    public string Protect(string value) => secrets.Protect(value);
    public string Unprotect(string value) => secrets.Unprotect(value);
    public Actor? Authenticate(string token) => db.Run((c, t) =>
    {
        using var cmd = Cmd(c, t, "SELECT kind,id FROM credentials WHERE hash=$h AND expires>$n", ("$h", Wire.Hash(token)), ("$n", Wire.Now));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var kind = reader.GetString(0); var id = reader.GetString(1); reader.Close();
        if (kind == "integration" && Get<Source>(c, t, "source", id) is { Revoked: false } source) return new Actor(kind, id, source.Scopes);
        if (kind == "device" && Get<Device>(c, t, "device", id) is { Revoked: false }) return new Actor(kind, id, []);
        return null;
    });
    public static string Credential(SqliteConnection c, SqliteTransaction t, string kind, string id)
    {
        var token = "nt_" + Wire.Secret();
        Exec(c, t, "INSERT INTO credentials VALUES($h,$k,$i,$e)", ("$h", Wire.Hash(token)), ("$k", kind), ("$i", id), ("$e", Wire.Now + (kind == "device" ? 30L : 90L) * 86400000));
        return token;
    }
    public Notification Create(Actor actor, CreateNotification input, string key)
    {
        var scope = input.Kind is "message" or "voice" ? "notify:send" : "requests:create";
        actor.RequireScope(scope);
        if (key.Length is < 8 or > 128 || key.Any(char.IsControl)) throw new Fault(400, "invalid_idempotency_key");
        string? promoted = null;
        try
        {
            return db.Run((c, t) =>
            {
                var source = Get<Source>(c, t, "source", actor.Id);
                if (source is not { Revoked: false } || !source.Scopes.Contains(scope)) throw new Fault(403, "source_revoked_or_scope_changed");
                using var cmd = Cmd(c, t, "SELECT hash,notification_id FROM idempotency WHERE source_id=$s AND key=$k", ("$s", actor.Id), ("$k", key));
                using var r = cmd.ExecuteReader();
                var hash = Wire.Hash(Wire.Encode(input));
                if (r.Read()) { var oldHash = r.GetString(0); var oldId = r.GetString(1); r.Close(); if (oldHash != hash) throw new Fault(409, "idempotency_conflict"); return Find(c, t, oldId, actor, false); }
                r.Close();
                Validate(input);
                if (input.Kind == "form" || input.Kind == "voice") input = input with { AllowCustom = false };
                using (var quota = Cmd(c, t, "SELECT count(*) FROM (SELECT id FROM notifications WHERE source_id=$s AND created >= $d LIMIT $l)", ("$s", actor.Id), ("$d", Wire.Now / 86400000 * 86400000), ("$l", settings.SourceDailyLimit)))
                    if ((long)quota.ExecuteScalar()! >= settings.SourceDailyLimit) throw new Fault(429, "source_daily_quota_exceeded");
                if (input.IconId != null) RequireIcon(c, t, input.IconId, actor);
                if (input.Kind == "voice")
                {
                    var expires = input.ExpiresAt!.Value.ToUnixTimeMilliseconds();
                    var notificationId = Wire.Id();
                    var voice = audio.Promote(c, t, actor, input.VoiceUpload!.UploadId, actor.Id, notificationId, expires);
                    promoted = AudioService.FinalPath(settings, voice.AudioId);
                    input = input with { VoiceUpload = null, Voice = voice };
                    var voiceNotification = Add(c, t, actor.Id, input, id: notificationId);
                    Exec(c, t, "INSERT INTO idempotency VALUES($s,$k,$h,$i)", ("$s", actor.Id), ("$k", key), ("$h", hash), ("$i", voiceNotification.Id));
                    return voiceNotification;
                }
                var n = Add(c, t, actor.Id, input);
                Exec(c, t, "INSERT INTO idempotency VALUES($s,$k,$h,$i)", ("$s", actor.Id), ("$k", key), ("$h", hash), ("$i", n.Id));
                return n;
            });
        }
        catch { if (promoted != null) AudioService.TryDelete(promoted); throw; }
    }
    public static void Validate(CreateNotification input)
    {
        var decision = input.Kind is "confirmation" or "form";
        var now = DateTimeOffset.UtcNow;
        if (input.Kind is not ("message" or "confirmation" or "form" or "voice") || input.SecurityLevel is not ("standard" or "elevated") ||
            string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 160 || input.Title.Any(char.IsControl) || input.Body == null || input.Body.Length > 16000 ||
            input.CorrelationId?.Length > 128 || input.IconId?.Length > 64 ||
            (input.Kind == "message" && input.SecurityLevel != "standard") ||
            (decision && (input.ExpiresAt == null || input.ExpiresAt <= now || input.ExpiresAt > now.AddDays(30))) ||
            (input.Kind == "form") != (input.Form != null) ||
            (input.Kind == "voice") != (input.VoiceUpload != null) ||
            input.Voice != null ||
            (input.Kind == "voice" && (input.ExpiresAt == null || input.ExpiresAt <= now || input.ExpiresAt > now.AddDays(30))))
            throw new Fault(400, "invalid_notification");
        if (input.Kind == "form") ValidateFormSpec(input.Form!);
    }
    private static bool FormId(string? value) => value is { Length: >= 1 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool FormLabel(string? value) => value is not null && !string.IsNullOrWhiteSpace(value) && value.Length <= 160 && !value.Any(char.IsControl);
    private static bool FormDescription(string? value) => value == null || (value.Length <= 1000 && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\t')));
    public static void ValidateFormSpec(FormSpec form)
    {
        if (form.Fields is not { Count: >= 1 and <= 20 }) throw new Fault(400, "invalid_form");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in form.Fields)
        {
            if (candidate is not FormField field || !FormId(field.Id) || !ids.Add(field.Id) || !FormLabel(field.Label) || !FormDescription(field.Description)) throw new Fault(400, "invalid_form");
            switch (field.Type)
            {
                case "text":
                    if (field.Options != null || field.Min != null || field.Max != null || field.MaxLength is < 1 or > 4000) throw new Fault(400, "invalid_form");
                    break;
                case "boolean":
                    if (field.Options != null || field.Min != null || field.Max != null || field.MaxLength != null) throw new Fault(400, "invalid_form");
                    break;
                case "number":
                    if (field.Options != null || field.MaxLength != null || (field.Min is { } min && !double.IsFinite(min)) || (field.Max is { } max && !double.IsFinite(max)) ||
                        (field.Min is { } low && field.Max is { } high && low > high)) throw new Fault(400, "invalid_form");
                    break;
                case "single_choice" or "multi_choice":
                    if (field.MaxLength != null || field.Min != null || field.Max != null || field.Options is not { Count: >= 1 and <= 20 }) throw new Fault(400, "invalid_form");
                    var options = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var candidateOption in field.Options)
                        if (candidateOption is not FormOption option || !FormId(option.Id) || !options.Add(option.Id) || !FormLabel(option.Label)) throw new Fault(400, "invalid_form");
                    break;
                default: throw new Fault(400, "invalid_form");
            }
        }
    }
    public static void ValidateFormValues(FormSpec form, FormValues? values)
    {
        if (values?.Values is not { } actual) throw new Fault(400, "invalid_form_answer");
        var fields = form.Fields.ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var (key, value) in actual)
        {
            if (!fields.TryGetValue(key, out var field)) throw new Fault(400, "invalid_form_answer");
            switch (field.Type)
            {
                case "text":
                    if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > (field.MaxLength ?? 1000)) throw new Fault(400, "invalid_form_answer");
                    break;
                case "boolean":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new Fault(400, "invalid_form_answer");
                    break;
                case "number":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                        (field.Min is { } min && number < min) || (field.Max is { } max && number > max)) throw new Fault(400, "invalid_form_answer");
                    break;
                case "single_choice":
                    if (value.ValueKind != JsonValueKind.String || !field.Options!.Any(o => o.Id == value.GetString())) throw new Fault(400, "invalid_form_answer");
                    break;
                case "multi_choice":
                    if (value.ValueKind != JsonValueKind.Array) throw new Fault(400, "invalid_form_answer");
                    var chosen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var element in value.EnumerateArray())
                        if (element.ValueKind != JsonValueKind.String || !chosen.Add(element.GetString()!) || !field.Options!.Any(o => o.Id == element.GetString())) throw new Fault(400, "invalid_form_answer");
                    break;
                default: throw new Fault(400, "invalid_form");
            }
        }
        foreach (var field in form.Fields.Where(f => f.Required))
            if (!actual.TryGetValue(field.Id, out var value) || (field.Type == "text" && value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) ||
                (field.Type == "multi_choice" && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)) throw new Fault(400, "form_required_missing");
    }
    private static Notification Add(SqliteConnection c, SqliteTransaction t, string sourceId, CreateNotification input, string? managementId = null, string? id = null)
    {
        var n = new Notification(id ?? Wire.Id(), sourceId, input, Wire.Now, input.Kind is "confirmation" or "form" ? "pending" : "active", ManagementId: managementId);
        Exec(c, t, "INSERT INTO notifications VALUES($i,$s,$c,$e,$st,$d)", ("$i", n.Id), ("$s", sourceId), ("$c", n.CreatedAt), ("$e", input.ExpiresAt?.ToUnixTimeMilliseconds()), ("$st", n.Status), ("$d", Wire.Encode(n)));
        Changed(c, t, n);
        foreach (var device in All<Device>(c, t, "device").Where(d => !d.Revoked && d.FcmToken != null))
            Queue(c, t, new Job("push:" + n.Id + ":" + device.Id, "push", n.Id, device.Id, "{}"));
        return n;
    }
    public static void Queue(SqliteConnection c, SqliteTransaction t, Job job) => Exec(c, t, "INSERT OR IGNORE INTO jobs(id,kind,notification_id,target_id,body,due) VALUES($i,$k,$n,$t,$b,$d)", ("$i", job.Id), ("$k", job.Kind), ("$n", job.NotificationId), ("$t", job.TargetId), ("$b", job.Body), ("$d", Wire.Now));
    private static void Changed(SqliteConnection c, SqliteTransaction t, Notification n)
    {
        Exec(c, t, "INSERT INTO changes(notification_id,at) VALUES($i,$n)", ("$i", n.Id), ("$n", Wire.Now));
    }
    private static Notification Find(SqliteConnection c, SqliteTransaction t, string id, Actor actor, bool requireRead = true)
    {
        var data = Rows(c, t, "SELECT data FROM notifications WHERE id=$i", ("$i", id)).FirstOrDefault() ?? throw new Fault(404, "notification_not_found");
        var n = Wire.Decode<Notification>(data);
        if (!actor.IsOwner)
        {
            var source = Get<Source>(c, t, "source", actor.Id);
            if (actor.Kind != "integration" || n.SourceId != actor.Id || source is not { Revoked: false } || (requireRead && !source.Scopes.Contains("requests:read"))) throw new Fault(404, "notification_not_found");
        }
        return n;
    }
    private static void Save(SqliteConnection c, SqliteTransaction t, Notification n)
    {
        Exec(c, t, "UPDATE notifications SET status=$s,data=$d WHERE id=$i", ("$s", n.Status), ("$d", Wire.Encode(n)), ("$i", n.Id)); Changed(c, t, n);
        if (Get<Source>(c, t, "source", n.SourceId) is { Revoked: false, WebhookUrl: not null })
        {
            var eventId = Wire.Id();
            Queue(c, t, new Job(eventId, "webhook", n.Id, n.SourceId, Wire.Encode(new { eventId, type = "notification." + n.Status, occurredAt=Wire.Now, notification = n })));
        }
    }
    public Notification Read(Actor actor, string id) { Sweep(); return db.Run((c, t) => Find(c, t, id, actor)); }
    public object List(Actor actor, long before = long.MaxValue, string beforeId = "~", int limit = 50)
    {
        if (!actor.IsOwner) actor.RequireScope("requests:read"); Sweep();
        if (limit is < 1 or > 200 || beforeId.Length > 64) throw new Fault(400, "invalid_pagination");
        return db.Run((c, t) =>
        {
            var items = Rows(c, t, "SELECT " + PreviewSql + " FROM notifications WHERE " + (actor.IsOwner ? "" : "source_id=$s AND ") + "(created<$b OR (created=$b AND id<$id)) ORDER BY created DESC,id DESC LIMIT $l", ("$s", actor.Id), ("$b", before), ("$id", beforeId), ("$l", limit)).Select(Wire.Decode<Notification>).ToArray();
            return new { items, next = items.Length == limit ? new { before = items[^1].CreatedAt, beforeId = items[^1].Id } : null };
        });
    }
    public object Sync(Actor actor, long after)
    {
        actor.RequireDevice(); if (after < 0) throw new Fault(400, "invalid_cursor"); Sweep();
        return db.Run((c, t) =>
        {
            var max = long.Parse(Rows(c, t, "SELECT CAST(COALESCE(MAX(seq),0) AS TEXT) FROM changes")[0], System.Globalization.CultureInfo.InvariantCulture);
            var min = long.Parse(Rows(c, t, "SELECT CAST(COALESCE(MIN(seq),0) AS TEXT) FROM changes")[0], System.Globalization.CultureInfo.InvariantCulture);
            if (after == 0 || after > max || after < min - 1)
            {
                var snapshot = Rows(c, t, "SELECT " + PreviewSql + " FROM notifications ORDER BY created DESC,id DESC LIMIT 1000").Select(Wire.Decode<Notification>).ToList();
                return (object)new { cursor = max, items = snapshot, deleted = Array.Empty<string>(), hasMore = false, reset = true };
            }
            using var cmd = Cmd(c, t, "SELECT seq,notification_id FROM changes WHERE seq>$a ORDER BY seq LIMIT 200", ("$a", after));
            using var reader = cmd.ExecuteReader(); var ids = new HashSet<string>(); var cursor = after;
            while (reader.Read()) { cursor = reader.GetInt64(0); ids.Add(reader.GetString(1)); }
            reader.Close();
            var items = new List<Notification>(); var deleted = new List<string>();
            foreach (var id in ids) { try { var n = Find(c, t, id, actor); items.Add(n with { Content = n.Content with { Body = n.Content.Body[..Math.Min(1000, n.Content.Body.Length)] }, Answer = n.Answer == null ? null : n.Answer with { Text = n.Answer.Text == null ? null : n.Answer.Text[..Math.Min(512, n.Answer.Text.Length)] }, FormAnswer = n.FormAnswer == null ? null : new FormAnswer(new Dictionary<string, JsonElement>()), PreviewOnly = true }); } catch (Fault e) when (e.Status == 404) { deleted.Add(id); } }
            return (object)new { cursor, items, deleted, hasMore = Rows(c, t, "SELECT CAST(seq AS TEXT) FROM changes WHERE seq>$a LIMIT 1", ("$a", cursor)).Count != 0, reset = false };
        });
    }
    private static void Pending(Notification n)
    {
        if (n.Content.Kind is not ("confirmation" or "form") || n.Status != "pending" || n.Content.ExpiresAt <= DateTimeOffset.UtcNow) throw new Fault(409, "request_not_pending");
    }
    private static void ValidAnswer(Notification n, Answer answer)
    {
        if (answer.Decision is not ("yes" or "no" or "custom") || (answer.Decision == "custom" && (!n.Content.AllowCustom || string.IsNullOrWhiteSpace(answer.Text) || answer.Text.Length > 4000)) || (answer.Decision != "custom" && answer.Text != null)) throw new Fault(400, "invalid_answer");
    }
    private static Challenge StoreChallenge(SqliteConnection c, SqliteTransaction t, Notification n, Actor actor, Func<string, object> payload, long expires, Answer? answer = null, FormValues? form = null, string? audioId = null)
    {
        var challengeId = Wire.Id();
        var body = Wire.Encode(payload(challengeId));
        var ch = new Challenge(challengeId, n.Id, actor.Id, Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), expires, answer, form, audioId);
        Exec(c, t, "DELETE FROM objects WHERE kind='challenge' AND json_extract(data,'$.deviceId')=$d AND json_extract(data,'$.notificationId')=$n", ("$d", actor.Id), ("$n", n.Id));
        Put(c, t, "challenge", ch.Id, ch); return ch;
    }
    private static void VerifySignature(Device device, Challenge ch, string signature)
    {
        try
        {
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(device.PublicKey), out _);
            if (!key.VerifyData(Convert.FromBase64String(ch.Payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) throw new Fault(403, "invalid_signature");
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { throw new Fault(403, "invalid_signature"); }
    }
    public Challenge Challenge(Actor actor, string id, Answer answer)
    {
        actor.RequireDevice(); Sweep();
        return db.Run((c, t) =>
        {
            var n = Find(c, t, id, actor); Pending(n); if (n.Content.Kind != "confirmation") throw new Fault(400, "invalid_answer"); ValidAnswer(n, answer);
            var expires = Math.Min(Wire.Now + 90000, n.Content.ExpiresAt!.Value.ToUnixTimeMilliseconds());
            // The exact UTF-8 bytes are sent to the device; no cross-language JSON canonicalization.
            return StoreChallenge(c, t, n, actor, challengeId => new { protocol = "alxprgs.notify.answer.v1", challengeId, notificationId = id, deviceId = actor.Id, notificationHash = Wire.Hash(Wire.Encode(n.Content)), answer, nonce = Wire.Secret(), expiresAt = expires }, expires, answer);
        });
    }
    public Challenge ChallengeForm(Actor actor, string id, FormValues values)
    {
        actor.RequireDevice(); Sweep();
        return db.Run((c, t) =>
        {
            var n = Find(c, t, id, actor); Pending(n); if (n.Content.Kind != "form") throw new Fault(400, "invalid_form"); ValidateFormValues(n.Content.Form!, values);
            var expires = Math.Min(Wire.Now + 90000, n.Content.ExpiresAt!.Value.ToUnixTimeMilliseconds());
            return StoreChallenge(c, t, n, actor, challengeId => new { protocol = "alxprgs.notify.form.v1", challengeId, notificationId = id, deviceId = actor.Id, notificationHash = Wire.Hash(Wire.Encode(n.Content)), values = values.Values, nonce = Wire.Secret(), expiresAt = expires }, expires, form: values);
        });
    }
    public Notification Respond(Actor actor, string id, SubmitAnswer request)
    {
        actor.RequireDevice(); Sweep(); string? revokedSource = null, revokedDevice = null, stopCallSource = null;
        var saved = db.Run((c, t) =>
        {
            var device = Get<Device>(c, t, "device", actor.Id); if (device is not { Revoked: false }) throw new Fault(401, "device_revoked");
            var n = Find(c, t, id, actor); Pending(n);
            if (n.Content.Kind == "form")
            {
                if (request.Answer != null || request.Form == null) throw new Fault(400, "invalid_form_answer");
                ValidateFormValues(n.Content.Form!, request.Form);
                if (n.Content.SecurityLevel == "elevated")
                {
                    var ch = request.ChallengeId == null ? null : Get<Challenge>(c, t, "challenge", request.ChallengeId);
                    if (ch?.Form == null || ch.ExpiresAt <= Wire.Now || ch.DeviceId != actor.Id || ch.NotificationId != id || !Wire.SameForm(ch.Form, request.Form) || request.Signature == null) throw new Fault(403, "invalid_challenge");
                    VerifySignature(device, ch, request.Signature);
                    Exec(c, t, "DELETE FROM objects WHERE kind='challenge' AND id=$i", ("$i", ch.Id));
                }
                n = n with { Status = "answered", FormAnswer = new FormAnswer(request.Form.Values), AnsweredAt = Wire.Now }; Save(c, t, n); return n;
            }
            if (request.Form != null) throw new Fault(400, "invalid_answer");
            var answer = request.Answer ?? throw new Fault(400, "invalid_answer");
            ValidAnswer(n, answer);
            if (n.Content.SecurityLevel == "elevated")
            {
                var ch = request.ChallengeId == null ? null : Get<Challenge>(c, t, "challenge", request.ChallengeId);
                if (ch == null || ch.ExpiresAt <= Wire.Now || ch.DeviceId != actor.Id || ch.NotificationId != id || ch.Answer != answer || request.Signature == null) throw new Fault(403, "invalid_challenge");
                VerifySignature(device, ch, request.Signature);
                Exec(c, t, "DELETE FROM objects WHERE kind='challenge' AND id=$i", ("$i", ch.Id));
            }
            if (n.ManagementId != null) (revokedSource, revokedDevice, stopCallSource) = ExecuteManagement(c, t, n.ManagementId, answer.Decision);
            n = n with { Status = "answered", Answer = answer, AnsweredAt = Wire.Now }; Save(c, t, n); return n;
        });
        if (revokedSource != null) { agents.DisconnectSource(revokedSource); _ = calls.SourceRevokedAsync(revokedSource); }
        if (revokedDevice != null) _ = calls.DeviceRevokedAsync(revokedDevice);
        if (stopCallSource != null) _ = calls.SourceRevokedAsync(stopCallSource);
        return saved;
    }
    public async Task<object> SendVoiceAsync(Actor actor, VoiceSend input, CancellationToken ct)
    {
        actor.RequireDevice();
        string? promoted = null, agentId = null; object result = default!;
        try
        {
            result = db.Run((c, t) =>
            {
                var related = input.NotificationId == null ? null : Find(c, t, input.NotificationId, actor);
                if (related == null || related.SourceId == "_system") throw new Fault(400, "voice_reply_target_required");
                var source = Get<Source>(c, t, "source", related.SourceId);
                if (source is not { Revoked: false }) throw new Fault(404, "notification_not_found");
                var agent = All<Agent>(c, t, "agent").FirstOrDefault(a => a.SourceId == source.Id && !a.Revoked) ?? throw new Fault(409, "agent_not_registered");
                var expires = input.ExpiresAt?.ToUnixTimeMilliseconds() ?? Wire.Now + settings.AudioMessageTtlDays * 86400000L;
                if (expires <= Wire.Now || expires > Wire.Now + 30L * 86400000) throw new Fault(400, "invalid_voice_message");
                var voice = audio.Promote(c, t, actor, input.UploadId, source.Id, null, expires);
                promoted = AudioService.FinalPath(settings, voice.AudioId); agentId = agent.Id;
                var id = Wire.Id();
                Put(c, t, "voicemessage", id, new VoiceMessage(id, source.Id, actor.Id, related.Id, voice.AudioId, voice.DurationMs, voice.SizeBytes, Wire.Now, expires));
                AgentHub.Enqueue(c, t, agent, "voice_message", new { voiceMessageId = id, audioId = voice.AudioId, durationMs = voice.DurationMs, inReplyTo = related.Id, expiresAt = expires }, expires, correlationId: related.Id, id: id);
                return (object)new { id, status = "queued", expiresAt = expires, inReplyTo = related.Id };
            });
        }
        catch { if (promoted != null) AudioService.TryDelete(promoted); throw; }
        if (agentId != null) await agents.PushAsync(agentId, ct);
        return result;
    }
    public object VoiceStatus(Actor actor, string id)
    {
        if (!AudioService.ValidId(id)) throw new Fault(404, "voice_message_not_found");
        return db.Run((c, t) =>
        {
            var message = Get<VoiceMessage>(c, t, "voicemessage", id) ?? throw new Fault(404, "voice_message_not_found");
            if (actor.Kind == "device" && message.DeviceId != actor.Id) throw new Fault(404, "voice_message_not_found");
            if (actor.Kind == "integration" && message.SourceId != actor.Id) throw new Fault(404, "voice_message_not_found");
            if (actor.Kind is not ("device" or "integration")) throw new Fault(403, "device_required");
            var command = Rows(c, t, "SELECT status FROM agent_commands WHERE id=$i", ("$i", id)).FirstOrDefault();
            var status = command switch
            {
                "acked" => "delivered", "expired" => "expired", "failed" => "failed",
                _ => message.ExpiresAt <= Wire.Now ? "expired" : "queued"
            };
            var asset = Get<AudioAsset>(c, t, "audio", message.AudioId);
            return (object)new { message.Id, status, message.CreatedAt, message.ExpiresAt, message.DurationMs, message.NotificationId, audioAvailable = asset?.Status == "ready" && File.Exists(AudioService.FinalPath(settings, message.AudioId)) };
        });
    }
    public void DeleteVoice(Actor actor, string id)
    {
        actor.RequireDevice();
        if (!AudioService.ValidId(id)) throw new Fault(404, "voice_message_not_found");
        var audioId = db.Run((c, t) =>
        {
            var message = Get<VoiceMessage>(c, t, "voicemessage", id) ?? throw new Fault(404, "voice_message_not_found");
            if (message.DeviceId != actor.Id) throw new Fault(404, "voice_message_not_found");
            Exec(c, t, "DELETE FROM agent_commands WHERE id=$i", ("$i", id));
            Exec(c, t, "DELETE FROM objects WHERE kind='voicemessage' AND id=$i", ("$i", id));
            return message.AudioId;
        });
        audio.DeleteAsset(audioId);
    }
    public Challenge AudioChallenge(Actor actor, string id)
    {
        actor.RequireDevice(); Sweep();
        return db.Run((c, t) =>
        {
            var n = Find(c, t, id, actor);
            if (n.Content.Kind != "voice" || n.Content.Voice == null) throw new Fault(400, "invalid_audio");
            if (n.Content.SecurityLevel != "elevated") throw new Fault(409, "audio_confirmation_not_required");
            var expires = Math.Min(Wire.Now + 90000, n.Content.Voice.ExpiresAt);
            if (expires <= Wire.Now) throw new Fault(410, "audio_expired");
            return StoreChallenge(c, t, n, actor, challengeId => new { protocol = "alxprgs.notify.audio.v1", challengeId, notificationId = id, audioId = n.Content.Voice.AudioId, deviceId = actor.Id, nonce = Wire.Secret(), expiresAt = expires }, expires, audioId: n.Content.Voice.AudioId);
        });
    }
    public object GrantAudio(Actor actor, string id, AudioGrantInput input)
    {
        actor.RequireDevice();
        return db.Run((c, t) =>
        {
            var device = Get<Device>(c, t, "device", actor.Id); if (device is not { Revoked: false }) throw new Fault(401, "device_revoked");
            var n = Find(c, t, id, actor);
            if (n.Content.Kind != "voice" || n.Content.Voice == null) throw new Fault(400, "invalid_audio");
            if (n.Content.SecurityLevel != "elevated") throw new Fault(409, "audio_confirmation_not_required");
            var ch = Get<Challenge>(c, t, "challenge", input.ChallengeId) ?? throw new Fault(403, "invalid_challenge");
            if (ch.ExpiresAt <= Wire.Now || ch.DeviceId != actor.Id || ch.NotificationId != id || ch.AudioId != n.Content.Voice.AudioId) throw new Fault(403, "invalid_challenge");
            VerifySignature(device, ch, input.Signature);
            Exec(c, t, "DELETE FROM objects WHERE kind='challenge' AND id=$i", ("$i", ch.Id));
            var asset = Get<AudioAsset>(c, t, "audio", n.Content.Voice.AudioId) ?? throw new Fault(410, "audio_expired");
            var (grant, expires) = audio.CreateGrant(c, t, asset, actor.Id);
            return (object)new { grant, expiresAt = expires };
        });
    }
    public string AudioFile(Actor actor, string id, string? grant)
    {
        if (!AudioService.ValidId(id)) throw new Fault(404, "audio_not_found");
        var asset = audio.Asset(id) ?? throw new Fault(404, "audio_not_found");
        if (actor.Kind == "integration")
        {
            if (asset.SourceId != actor.Id) throw new Fault(404, "audio_not_found");
        }
        else if (actor.Kind == "device")
        {
            if (asset.NotificationId != null)
            {
                var n = db.Run((c, t) => Find(c, t, asset.NotificationId, actor));
                if (n.Content.Kind != "voice" || n.Content.Voice == null || n.Content.Voice.AudioId != asset.Id) throw new Fault(404, "audio_not_found");
                if (n.Content.SecurityLevel == "elevated")
                {
                    if (string.IsNullOrWhiteSpace(grant)) throw new Fault(403, "audio_confirmation_required");
                    audio.ConsumeGrant(actor, id, grant);
                }
            }
            else if (asset.DeviceId != actor.Id) throw new Fault(404, "audio_not_found");
        }
        else throw new Fault(403, "device_required");
        var path = audio.FileFor(asset);
        if (asset.Status != "ready" || path == null || !File.Exists(path)) throw new Fault(410, "audio_expired");
        return path;
    }
    public Notification Cancel(Actor actor, string id)
    {
        actor.RequireScope("requests:cancel"); Sweep(); return db.Run((c, t) => { var n = Find(c, t, id, actor, false); Pending(n); n = n with { Status = "cancelled" }; Save(c, t, n); return n; });
    }
    public void Receipt(Actor actor, string id, string status)
    {
        actor.RequireDevice(); if (status is not ("device_received" or "opened")) throw new Fault(400, "invalid_receipt");
        db.Run((c, t) => { _ = Find(c, t, id, actor); var k = id + ":" + actor.Id; var previous = Get<Delivery>(c, t, "delivery", k); if (previous?.Status != "opened") Put(c, t, "delivery", k, new Delivery(id, actor.Id, status, Wire.Now)); return 0; });
    }
    public List<Delivery> Deliveries(Actor actor, string id) => db.Run((c, t) => { _ = Find(c, t, id, actor); return Rows(c, t, "SELECT data FROM objects WHERE kind='delivery' AND id >= $a AND id < $b", ("$a", id + ":"), ("$b", id + ";")).Select(Wire.Decode<Delivery>).ToList(); });
    public void Sweep()
    {
        db.Run((c, t) =>
        {
            foreach (var value in Rows(c, t, "SELECT data FROM notifications WHERE status='pending' AND expires<=$n LIMIT 200", ("$n", Wire.Now)))
            {
                var n = Wire.Decode<Notification>(value); if (n.ManagementId != null && Get<Management>(c, t, "management", n.ManagementId) is { } m) Put(c, t, "management", m.Id, m with { Status = "expired" });
                Save(c, t, n with { Status = "expired" });
            }
            return 0;
        });
    }
    public void Cleanup()
    {
        db.Run((c, t) =>
        {
            var cutoff = Wire.Now - 30L * 86400000;
            Exec(c,t,"UPDATE jobs SET status='failed',lease=0 WHERE status='pending' AND ((kind='webhook' AND COALESCE(json_extract(body,'$.occurredAt'),0)<$d) OR (kind='push' AND notification_id IN (SELECT id FROM notifications WHERE created<$d)))",("$d",Wire.Now-86400000));
            foreach (var id in Rows(c, t, "SELECT id FROM notifications WHERE created<$n AND status!='pending' AND (json_extract(data,'$.content.kind') NOT IN ('confirmation','form') OR expires<$recent) AND NOT EXISTS(SELECT 1 FROM jobs WHERE notification_id=notifications.id AND status='pending') LIMIT 500", ("$n", cutoff),("$recent",Wire.Now-86400000)))
            {
                Exec(c, t, "DELETE FROM idempotency WHERE notification_id=$i; DELETE FROM jobs WHERE notification_id=$i; DELETE FROM notifications WHERE id=$i", ("$i", id));
                Exec(c, t, "INSERT INTO changes(notification_id,at) VALUES($i,$n)", ("$i", id), ("$n", Wire.Now));
            }
            Exec(c, t, "DELETE FROM credentials WHERE expires<=$n", ("$n", Wire.Now));
            foreach (var ch in All<Challenge>(c, t, "challenge").Where(v => v.ExpiresAt < Wire.Now)) Exec(c, t, "DELETE FROM objects WHERE kind='challenge' AND id=$i", ("$i", ch.Id));
            foreach (var kind in new[] { "enrollment", "stepup", "management" })
                Exec(c, t, "DELETE FROM objects WHERE kind=$k AND CAST(json_extract(data,'$.expiresAt') AS INTEGER)<$n", ("$k", kind), ("$n", Wire.Now - 3600000));
            Exec(c, t, "DELETE FROM objects WHERE kind='callchallenge' AND CAST(json_extract(data,'$.expiresAt') AS INTEGER)<$n", ("$n", Wire.Now - 3600000));
            Exec(c, t, "DELETE FROM objects WHERE kind='call' AND CAST(json_extract(data,'$.startedAt') AS INTEGER)<$n", ("$n", cutoff));
            Exec(c, t, "DELETE FROM objects WHERE kind='delivery' AND json_extract(data,'$.notificationId') NOT IN (SELECT id FROM notifications)");
            Exec(c, t, "DELETE FROM objects WHERE kind='chatgpt_voice' AND CAST(json_extract(data,'$.createdAt') AS INTEGER)<$n", ("$n", cutoff - 86400000L));
            Exec(c, t, "DELETE FROM objects WHERE kind=$k AND CAST(json_extract(data,'$.createdAt') AS INTEGER)<$n", ("$k", ChatGptCall.AttemptKind), ("$n", cutoff));
            Exec(c, t, "DELETE FROM changes WHERE at<$n", ("$n", cutoff)); return 0;
        });
        agents.Sweep();
        audio.Cleanup();
    }
    public Enrollment BeginEnrollment(EnrollDevice input, string owner)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || input.Name.Any(char.IsControl) || input.PublicKey.Length > 1024 || input.FcmToken?.Length > 4096) throw new Fault(400, "invalid_device");
        try { using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(input.PublicKey), out var read); if (key.KeySize != 256 || read != Convert.FromBase64String(input.PublicKey).Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new Fault(400, "invalid_device_key"); }
        catch (Exception e) when (e is CryptographicException or FormatException) { throw new Fault(400, "invalid_device_key"); }
        var enrollment = new Enrollment(Wire.Secret(), input, owner, Wire.Now + 300000);
        db.Run((c, t) => { Put(c, t, "enrollment", enrollment.Id, enrollment); return 0; }); return enrollment;
    }
    public void CompleteEnrollment(SqliteConnection c, SqliteTransaction t, string id)
    {
        var e = Get<Enrollment>(c, t, "enrollment", id) ?? throw new Fault(404, "enrollment_not_found");
        if (e.Completed || e.ExpiresAt <= Wire.Now) throw new Fault(409, "enrollment_expired");
        if (e.Device.Recovery)
            foreach (var old in All<Device>(c, t, "device")) { Put(c, t, "device", old.Id, old with { Revoked = true, FcmToken = null }); Exec(c, t, "DELETE FROM credentials WHERE kind='device' AND id=$i", ("$i", old.Id)); }
        var device = new Device(Wire.Id(), e.Device.Name, e.Device.PublicKey, e.Device.FcmToken);
        Put(c, t, "device", device.Id, device);
        var token = Credential(c, t, "device", device.Id);
        Put(c, t, "enrollment", id, e with { Completed = true, ProtectedToken = Protect(Wire.Encode(new { deviceId = device.Id, token, expiresAt = Wire.Now + 30L * 86400000 })) });
    }
    public object EnrollmentResult(string id) => db.Run((c, t) =>
    {
        var e = Get<Enrollment>(c, t, "enrollment", id) ?? throw new Fault(404, "enrollment_not_found");
        if (e.ExpiresAt <= Wire.Now) throw new Fault(410, "enrollment_expired");
        if (e.ProtectedToken == null) return (object)new { status = e.Completed ? "consumed" : "pending" };
        Put(c, t, "enrollment", id, e with { ProtectedToken = null }); return Wire.Decode<System.Text.Json.JsonElement>(Unprotect(e.ProtectedToken));
    });
    public List<Device> Devices(Actor actor) { actor.RequireOwner(); return db.Run((c, t) => All<Device>(c, t, "device").Select(d => d with { FcmToken = null }).ToList()); }
    public object Sources(Actor actor)
    {
        if (!actor.IsOwner) actor.RequireScope("requests:read");
        return db.Run((c, t) => (object)All<Source>(c, t, "source").Where(s => actor.IsOwner || s.Id == actor.Id).Select(s => new { s.Id, s.Name, s.Scopes, s.Preview, s.IconId, s.WebhookUrl, s.Revoked }).ToList());
    }
    public void SetFcm(Actor actor, string token)
    {
        actor.RequireDevice(); if (token.Length is < 10 or > 4096) throw new Fault(400, "invalid_fcm_token");
        db.Run((c, t) => { var d = Get<Device>(c, t, "device", actor.Id) ?? throw new Fault(401, "device_revoked"); Put(c, t, "device", d.Id, d with { FcmToken = token }); return 0; });
    }
    public Management BeginManagement(Actor actor, ManagementInput input)
    {
        actor.RequireOwner();
        var updatesPermissions = input.Action == ChatGptOAuth.PermissionsAction;
        if (input.Action is not ("integration.create" or "integration.revoke" or "device.revoke" or "source.update" or "agent.register" or ChatGptOAuth.LinkAction or ChatGptOAuth.PermissionsAction) || input.Name?.Length > 80 || input.Name?.Any(char.IsControl) == true || input.TargetId?.Length > 64 || input.IconId?.Length > 64 || input.Preview is not (null or "hidden" or "full") ||
            (!updatesPermissions && (input.ExpectedRevision != null || input.Scopes?.Any(s => s is not ("notify:send" or "requests:create" or "requests:read" or "requests:cancel" or "icons:write" or "agents:connect")) == true)) ||
            input.Capabilities?.Length > 3 || input.Capabilities?.Any(c => c is not ("text_tasks" or "voice_messages" or "realtime_audio")) == true || input.Capabilities?.Distinct(StringComparer.Ordinal).Count() != input.Capabilities?.Length) throw new Fault(400, "invalid_management_action");
        if (input.Action == "integration.create" && (string.IsNullOrWhiteSpace(input.Name) || input.Scopes is not { Length: > 0 })) throw new Fault(400, "invalid_integration");
        if (input.Action == "agent.register" && (string.IsNullOrWhiteSpace(input.Name) || input.Capabilities is not { Length: > 0 } || input.Scopes != null || input.Preview != null || input.WebhookUrl != null || input.IconId != null)) throw new Fault(400, "invalid_agent");
        if (input.Action == ChatGptOAuth.LinkAction)
        {
            if (actor.Kind != "web") throw new Fault(403, "web_required");
            if (input != new ManagementInput(ChatGptOAuth.LinkAction)) throw new Fault(400, "invalid_management_action");
        }
        else if (updatesPermissions)
        {
            if (actor.Kind != "web") throw new Fault(403, "web_required");
            if (input.TargetId is not { Length: 32 } || input.ExpectedRevision is not >= 0 ||
                input.Scopes is not { } scopes || scopes.Length > ChatGptOAuth.PermissionsForSource.Length ||
                scopes.Any(s => !ChatGptOAuth.PermissionsForSource.Contains(s)) ||
                scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length ||
                input != new ManagementInput(input.Action, TargetId: input.TargetId, Scopes: scopes, ExpectedRevision: input.ExpectedRevision))
                throw new Fault(400, "invalid_chatgpt_permissions");
            input = input with { Scopes = ChatGptOAuth.PermissionsForSource.Where(scopes.Contains).ToArray() };
        }
        else if (input.Action != "integration.create" && input.TargetId == null) throw new Fault(400, "target_required");
        return db.Run((c, t) =>
        {
            string? permissionDescription = null;
            if (updatesPermissions)
            {
                var target = ChatGptOAuth.PermissionTarget(c, t, input) ?? throw new Fault(409, "chatgpt_permissions_changed");
                if (target.Link.PermissionsRevision > 0 && target.Source.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(input.Scopes!))
                    throw new Fault(400, "chatgpt_permissions_unchanged");
                permissionDescription = ChatGptOAuth.PermissionChangeDescription(target.Source.Scopes, input.Scopes!);
            }
            if (!All<Device>(c, t, "device").Any(d => !d.Revoked)) throw new Fault(409, "trusted_device_required");
            using (var count = Cmd(c, t, "SELECT count(*) FROM objects WHERE kind='management' AND json_extract(data,'$.status')='pending' AND json_extract(data,'$.expiresAt')>$n", ("$n", Wire.Now)))
                if ((long)count.ExecuteScalar()! >= 20) throw new Fault(429, "too_many_pending_management_requests");
            if (input.IconId != null) RequireIcon(c, t, input.IconId, actor);
            if (input.Action == "agent.register")
            {
                var owner = Get<Source>(c, t, "source", input.TargetId!) ?? throw new Fault(404, "source_not_found");
                if (owner.Revoked || !owner.Scopes.Contains("agents:connect")) throw new Fault(409, "agent_scope_required");
                if (All<Agent>(c, t, "agent").Any(a => a.SourceId == owner.Id && !a.Revoked)) throw new Fault(409, "agent_already_registered");
                if (Rows(c, t, "SELECT id FROM objects WHERE kind='management' AND json_extract(data,'$.status')='pending' AND json_extract(data,'$.input.action')='agent.register' AND json_extract(data,'$.input.targetId')=$s LIMIT 1", ("$s", owner.Id)).Count != 0) throw new Fault(409, "agent_registration_pending");
            }
            var m = new Management(Wire.Id(), actor.SessionId ?? actor.Id, input, Wire.Now + 300000); Put(c, t, "management", m.Id, m);
            var description = input.Action switch
            {
                ChatGptOAuth.LinkAction => "Выдать ChatGPT доступ к выделенному источнику Notify. ChatGPT сможет отправлять уведомления, управлять своими запросами и звонить с озвучкой. Прежнее подключение ChatGPT будет отозвано.",
                ChatGptOAuth.PermissionsAction => permissionDescription!,
                "integration.create" => "Создать доступ для «" + input.Name + "». Права: " + string.Join(", ", input.Scopes!.Select(s => s switch { "notify:send" => "сообщения", "requests:create" => "запросы подтверждения", "requests:read" => "чтение своих результатов", "requests:cancel" => "отмена своих запросов", "agents:connect" => "подключение агента", _ => "загрузка иконок" })) + (input.WebhookUrl == null ? "" : ". Доставлять ответы на " + input.WebhookUrl),
                "integration.revoke" => "Отозвать доступ интеграции «" + (Get<Source>(c, t, "source", input.TargetId!)?.Name ?? "Неизвестный источник") + "».",
                "device.revoke" => "Отозвать устройство «" + (Get<Device>(c, t, "device", input.TargetId!)?.Name ?? "Неизвестное устройство") + "».",
                "agent.register" => "Зарегистрировать агента «" + input.Name + "» для источника «" + (Get<Source>(c, t, "source", input.TargetId!)?.Name ?? "Неизвестный источник") + "». Возможности: " + string.Join(", ", input.Capabilities!.Select(cap => cap switch { "text_tasks" => "текстовые задачи", "voice_messages" => "голосовые сообщения", _ => "живые звонки" })) + ".",
                _ => "Изменить настройки источника «" + (Get<Source>(c, t, "source", input.TargetId!)?.Name ?? "Неизвестный источник") + "». Содержание на экране блокировки: " + (input.Preview == "full" ? "показывать" : "скрывать")
            };
            Add(c, t, "_system", new CreateNotification("confirmation", "Управление Notify", description, "elevated", DateTimeOffset.FromUnixTimeMilliseconds(m.ExpiresAt), false), m.Id);
            return m;
        });
    }
    public object ManagementResult(Actor actor, string id) => ReadManagementResult(actor, id, deferChatGpt: false)!;

    internal async Task<object> ManagementResult(Actor actor, string id, ChatGptOAuth chatGpt, CancellationToken cancellationToken)
    {
        var result = ReadManagementResult(actor, id, deferChatGpt: true);
        if (result != null) return result;
        await chatGpt.EnsureDefaultCallback(cancellationToken);
        return ManagementResult(actor, id);
    }

    private object? ReadManagementResult(Actor actor, string id, bool deferChatGpt)
    {
        actor.RequireOwner(); return db.Run((c, t) =>
        {
            var m = Get<Management>(c, t, "management", id);
            if (m == null || m.SessionId != (actor.SessionId ?? actor.Id))
                throw new Fault(404, "management_not_found");
            if (m.ProtectedResult == null) return (object)new { m.Id, Status = m.Status == "pending" && m.ExpiresAt <= Wire.Now ? "expired" : m.Status };
            if (deferChatGpt && m.Input.Action == ChatGptOAuth.LinkAction) return null;
            Put(c, t, "management", id, m with { ProtectedResult = null }); return new { m.Id, m.Status, result = Wire.Decode<System.Text.Json.JsonElement>(Unprotect(m.ProtectedResult)) };
        });
    }
    private (string? Source, string? Device, string? StopCallSource) ExecuteManagement(SqliteConnection c, SqliteTransaction t, string id, string decision)
    {
        var m = Get<Management>(c, t, "management", id) ?? throw new Fault(409, "management_not_found");
        if (m.Status != "pending" || m.ExpiresAt <= Wire.Now) throw new Fault(409, "management_expired");
        if (decision != "yes") { Put(c, t, "management", id, m with { Status = "denied" }); return (null, null, null); }
        object result = new { ok = true }; var input = m.Input; string? revokedSource = null, revokedDevice = null, stopCallSource = null;
        switch (input.Action)
        {
            case ChatGptOAuth.LinkAction:
                result = ChatGptOAuth.ApproveLink(c, t, Protect, out revokedSource); break;
            case ChatGptOAuth.PermissionsAction:
                if (!ChatGptOAuth.ApplyPermissions(c, t, input, out stopCallSource))
                {
                    Put(c, t, "management", id, m with { Status = "conflict" });
                    return (null, null, null);
                }
                break;
            case "integration.create":
                var sid = Wire.Id(); var secret = input.WebhookUrl == null ? null : Wire.Secret();
                Put(c, t, "source", sid, new Source(sid, input.Name!, input.Scopes!, input.Preview ?? "hidden", input.IconId, input.WebhookUrl, secret == null ? null : Protect(secret)));
                result = new { sourceId = sid, token = Credential(c, t, "integration", sid), webhookSecret = secret }; break;
            case "integration.revoke":
                var source = Get<Source>(c, t, "source", input.TargetId!) ?? throw new Fault(404, "source_not_found");
                Put(c, t, "source", source.Id, source with { Revoked = true }); Exec(c, t, "DELETE FROM credentials WHERE kind='integration' AND id=$i", ("$i", source.Id));
                foreach (var revoked in All<Agent>(c, t, "agent").Where(a => a.SourceId == source.Id && !a.Revoked)) Put(c, t, "agent", revoked.Id, revoked with { Revoked = true });
                Exec(c, t, "UPDATE agent_commands SET status='failed' WHERE source_id=$s AND status IN ('queued','sent')", ("$s", source.Id));
                revokedSource = source.Id; break;
            case "device.revoke":
                var d = Get<Device>(c, t, "device", input.TargetId!) ?? throw new Fault(404, "device_not_found");
                Put(c, t, "device", d.Id, d with { Revoked = true, FcmToken = null }); Exec(c, t, "DELETE FROM credentials WHERE kind='device' AND id=$i", ("$i", d.Id)); revokedDevice = d.Id; break;
            case "source.update":
                var s = Get<Source>(c, t, "source", input.TargetId!) ?? throw new Fault(404, "source_not_found");
                Put(c, t, "source", s.Id, s with { Preview = input.Preview ?? s.Preview, IconId = input.IconId ?? s.IconId }); break;
            case "agent.register":
                var owner = Get<Source>(c, t, "source", input.TargetId!) ?? throw new Fault(404, "source_not_found");
                if (owner.Revoked || !owner.Scopes.Contains("agents:connect")) throw new Fault(409, "agent_scope_required");
                if (All<Agent>(c, t, "agent").Any(a => a.SourceId == owner.Id && !a.Revoked)) throw new Fault(409, "agent_already_registered");
                var agent = new Agent(Wire.Id(), owner.Id, input.Name!, input.Capabilities!, Wire.Now);
                Put(c, t, "agent", agent.Id, agent); result = new { agentId = agent.Id, sourceId = owner.Id }; break;
        }
        Put(c, t, "management", id, m with { Status = "approved", ProtectedResult = Protect(Wire.Encode(result)) });
        return (revokedSource, revokedDevice, stopCallSource);
    }
    public static Icon RequireIcon(SqliteConnection c, SqliteTransaction t, string id, Actor actor)
    {
        var icon = Get<Icon>(c, t, "icon", id) ?? throw new Fault(404, "icon_not_found");
        if (!actor.IsOwner && icon.SourceId != actor.Id) throw new Fault(404, "icon_not_found"); return icon;
    }
}
