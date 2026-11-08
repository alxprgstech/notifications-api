using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using static Notify.Database;

namespace Notify;

// Two-phase storage: a bounded .part upload is validated, then promoted atomically by the same
// transaction that creates the notification or durable agent command.
public sealed class AudioService(Database db, Settings settings, ILogger<AudioService> log)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int SampleRate = 16000;
    public const int MaxDurationMs = 300000;
    public const int GrantSeconds = 60;
    private readonly SemaphoreSlim uploadGate = new(2, 2);

    public static string Directory(Settings settings) => Path.Combine(settings.DataPath, "audio");
    public static string PartPath(Settings settings, string id) => Path.Combine(Directory(settings), id + ".part");
    public static string FinalPath(Settings settings, string id) => Path.Combine(Directory(settings), id + ".wav");
    public static bool ValidId(string? id) => id is { Length: 32 } && id.All(c => char.IsAsciiHexDigitLower(c));

    public Task<VoiceUploadResponse> UploadAsync(Actor actor, HttpRequest request, CancellationToken ct) =>
        UploadAsync(actor, request.Body, request.ContentLength, ct);

    internal async Task<VoiceUploadResponse> UploadAsync(Actor actor, Stream body, long? contentLength, CancellationToken ct)
    {
        if (actor.Kind is not ("device" or "integration")) throw new Fault(403, "device_or_integration_required");
        if (!await uploadGate.WaitAsync(0, ct)) throw new Fault(429, "audio_upload_busy");
        var id = Wire.Id(); var directory = Directory(settings); System.IO.Directory.CreateDirectory(directory); var part = PartPath(settings, id);
        try
        {
            if (contentLength is > MaxBytes) throw new Fault(413, "audio_too_large");
            long total = 0; var buffer = new byte[65536];
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new Fault(413, "audio_too_large");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            int duration;
            using (var stream = File.OpenRead(part)) duration = ValidateWav(stream, total);
            try
            {
                db.Run((c, t) =>
                {
                    var used = long.Parse(Rows(c, t, "SELECT CAST(COALESCE(SUM(json_extract(data,'$.sizeBytes')),0) AS TEXT) FROM objects WHERE kind='audio'")[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (used + total > settings.AudioQuotaBytes) throw new Fault(413, "audio_quota_exceeded");
                    Put(c, t, "audio", id, new AudioAsset(id, actor.Kind == "integration" ? actor.Id : "", actor.Kind == "device" ? actor.Id : null, "pending", (int)total, duration, Wire.Now, Wire.Now + settings.AudioUploadMinutes * 60000L));
                    return 0;
                });
            }
            catch { TryDelete(part); throw; }
            return new VoiceUploadResponse(id, duration, (int)total, Wire.Now + settings.AudioUploadMinutes * 60000L);
        }
        catch { TryDelete(part); throw; }
        finally { uploadGate.Release(); }
    }

    public AudioAsset RequirePending(SqliteConnection c, SqliteTransaction t, Actor actor, string uploadId)
    {
        if (!ValidId(uploadId)) throw new Fault(404, "audio_upload_not_found");
        var asset = Get<AudioAsset>(c, t, "audio", uploadId) ?? throw new Fault(404, "audio_upload_not_found");
        if (asset.Status != "pending" || asset.ExpiresAt <= Wire.Now) throw new Fault(410, "audio_upload_expired");
        if (actor.Kind == "device" && asset.DeviceId != actor.Id) throw new Fault(404, "audio_upload_not_found");
        if (actor.Kind == "integration" && asset.SourceId != actor.Id) throw new Fault(404, "audio_upload_not_found");
        return asset;
    }

    // Called inside the caller's SQLite transaction. The file is renamed before the transaction commits;
    // callers remove the final file if the transaction throws (a crash leaves an orphan for Cleanup).
    public VoiceAttachment Promote(SqliteConnection c, SqliteTransaction t, Actor actor, string uploadId, string sourceId, string? notificationId, long expiresAt)
    {
        var asset = RequirePending(c, t, actor, uploadId);
        var part = PartPath(settings, asset.Id); var final = FinalPath(settings, asset.Id);
        if (!File.Exists(part)) throw new Fault(410, "audio_upload_expired");
        if (File.Exists(final)) throw new Fault(409, "audio_upload_used");
        try { File.Move(part, final); } catch (IOException) { throw new Fault(410, "audio_upload_expired"); }
        var updated = asset with { Status = "ready", SourceId = sourceId, NotificationId = notificationId, ExpiresAt = expiresAt };
        Put(c, t, "audio", asset.Id, updated);
        return new VoiceAttachment(asset.Id, asset.DurationMs, asset.SizeBytes, expiresAt);
    }

    public AudioAsset? Asset(string id) => db.Run((c, t) => Get<AudioAsset>(c, t, "audio", id));
    public string? FileFor(AudioAsset? asset) => asset == null ? null : FinalPath(settings, asset.Id);
    public (string Grant, long ExpiresAt) CreateGrant(AudioAsset asset, string deviceId)
    {
        return db.Run((c, t) => CreateGrant(c, t, asset, deviceId));
    }
    public (string Grant, long ExpiresAt) CreateGrant(SqliteConnection c, SqliteTransaction t, AudioAsset asset, string deviceId)
    {
        var token = Wire.Secret(); var expires = Wire.Now + GrantSeconds * 1000L;
        Put(c, t, "audiogrant", Wire.Hash(token), new AudioGrant(Wire.Hash(token), asset.Id, deviceId, expires));
        return (token, expires);
    }
    public void ConsumeGrant(Actor actor, string audioId, string token)
    {
        if (token.Length is < 20 or > 128) throw new Fault(403, "audio_grant_required");
        var hash = Wire.Hash(token);
        db.Run((c, t) =>
        {
            var grant = Get<AudioGrant>(c, t, "audiogrant", hash) ?? throw new Fault(403, "audio_grant_required");
            if (grant.Used || grant.ExpiresAt <= Wire.Now || grant.AudioId != audioId || grant.DeviceId != actor.Id) throw new Fault(403, "audio_grant_required");
            Put(c, t, "audiogrant", hash, grant with { Used = true }); return 0;
        });
    }

    public void Cleanup()
    {
        var files = new List<string>();
        db.Run((c, t) =>
        {
            foreach (var asset in All<AudioAsset>(c, t, "audio"))
            {
                var orphan = asset.NotificationId != null && Get<Notification>(c, t, "notification", asset.NotificationId) == null;
                if (asset.Status == "pending" && asset.ExpiresAt <= Wire.Now || asset.Status == "ready" && (asset.ExpiresAt <= Wire.Now || orphan))
                {
                    Exec(c, t, "DELETE FROM objects WHERE kind='audio' AND id=$i", ("$i", asset.Id));
                    files.Add(FinalPath(settings, asset.Id)); files.Add(PartPath(settings, asset.Id));
                }
            }
            foreach (var grant in All<AudioGrant>(c, t, "audiogrant").Where(g => g.ExpiresAt < Wire.Now || g.Used)) Exec(c, t, "DELETE FROM objects WHERE kind='audiogrant' AND id=$i", ("$i", grant.Id));
            return 0;
        });
        foreach (var file in files) TryDelete(file);
        SweepOrphans();
    }

    private void SweepOrphans()
    {
        try
        {
            var directory = Directory(settings); if (!System.IO.Directory.Exists(directory)) return;
            var known = db.Run((c, t) => All<AudioAsset>(c, t, "audio").Select(a => a.Id).ToHashSet(StringComparer.Ordinal));
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != 32 || !AudioService.ValidId(name) || known.Contains(name)) continue;
                if (Wire.Now - new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds() < 3600000) continue;
                TryDelete(file);
            }
        }
        catch (Exception e) { log.LogWarning("Audio orphan sweep failed: {Type}", e.GetType().Name); }
    }

    public void DeleteAsset(string id)
    {
        db.Run((c, t) => { Exec(c, t, "DELETE FROM objects WHERE kind='audio' AND id=$i", ("$i", id)); return 0; });
        TryDelete(FinalPath(settings, id)); TryDelete(PartPath(settings, id));
    }
    public static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    public static int ValidateWav(Stream stream, long fileLength)
    {
        if (fileLength < 44 || fileLength > MaxBytes) throw new Fault(fileLength > MaxBytes ? 413 : 400, fileLength > MaxBytes ? "audio_too_large" : "invalid_audio");
        var header = new byte[12]; ReadExactly(stream, header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8) || BitConverter.ToInt32(header, 4) != fileLength - 8) throw new Fault(400, "invalid_audio");
        int? format = null, channels = null, rate = null, byteRate = null, blockAlign = null, bits = null; long dataSize = -1;
        var chunk = new byte[8];
        while (stream.Position + 8 <= fileLength)
        {
            ReadExactly(stream, chunk); var id = Encoding.ASCII.GetString(chunk, 0, 4); var size = BitConverter.ToInt32(chunk, 4);
            if (size < 0 || stream.Position + size > fileLength) throw new Fault(400, "invalid_audio");
            if (id == "fmt ")
            {
                if (size < 16 || format != null) throw new Fault(400, "invalid_audio");
                var fmt = new byte[16]; ReadExactly(stream, fmt);
                format = BitConverter.ToInt16(fmt, 0); channels = BitConverter.ToInt16(fmt, 2); rate = BitConverter.ToInt32(fmt, 4); byteRate = BitConverter.ToInt32(fmt, 8); blockAlign = BitConverter.ToInt16(fmt, 12); bits = BitConverter.ToInt16(fmt, 14);
                stream.Position += size - 16;
            }
            else if (id == "data")
            {
                if (dataSize >= 0) throw new Fault(400, "invalid_audio");
                dataSize = size; stream.Position += size;
            }
            else stream.Position += size;
            if ((size & 1) == 1) { if (stream.Position >= fileLength) throw new Fault(400, "invalid_audio"); stream.Position++; }
        }
        if (format != 1 || channels != 1 || rate != SampleRate || bits != 16 || byteRate != 32000 || blockAlign != 2 || dataSize <= 0 || dataSize % 2 != 0) throw new Fault(400, "invalid_audio");
        var duration = dataSize * 1000L / 32000;
        if (duration <= 0) throw new Fault(400, "invalid_audio");
        if (duration > MaxDurationMs) throw new Fault(400, "audio_too_long");
        return (int)duration;
    }
    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0; while (offset < buffer.Length) { var read = stream.Read(buffer, offset, buffer.Length - offset); if (read <= 0) throw new Fault(400, "invalid_audio"); offset += read; }
    }
}
