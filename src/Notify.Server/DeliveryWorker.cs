using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using static Notify.Database;

namespace Notify;

public static class WebhookPolicy
{
    public static Uri Validate(string url, Settings settings)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !settings.WebhookHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase)) throw new Fault(400, "webhook_not_allowlisted_https");
        if (IPAddress.TryParse(uri.Host, out var ip) && !IsPublic(ip)) throw new Fault(400, "webhook_private_address"); return uri;
    }
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4(); var b = ip.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 2))) && !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = async (context, ct) =>
        {
            var ips = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (ips.Length == 0 || ips.Any(ip => !IsPublic(ip))) throw new HttpRequestException("Non-public webhook DNS result");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(ips, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
}

public interface IPushSender
{
    Task Send(string token, string notificationId, bool urgent, CancellationToken ct);
    Task SendCall(string token, string callId, string state, CancellationToken ct);
}
public sealed class GooglePushSender(Settings settings, IHttpClientFactory clients) : IPushSender
{
    private string? accessToken; private long tokenExpires; private readonly SemaphoreSlim tokenGate = new(1, 1);
    public Task Send(string token, string notificationId, bool urgent, CancellationToken ct) => SendData(token, new { notification_id = notificationId }, urgent, "300s", ct);
    public Task SendCall(string token, string callId, string state, CancellationToken ct) => SendData(token, new { type = "call", call_id = callId, call_state = state }, true, "120s", ct);
    private async Task SendData<T>(string token, T data, bool urgent, string ttl, CancellationToken ct)
    {
        if (settings.FirebaseCredentials == null) throw new InvalidOperationException("Firebase credentials not configured");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings.FirebaseCredentials, ct)); var account = json.RootElement;
        var project = account.GetProperty("project_id").GetString()!;
        if (project.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) throw new InvalidOperationException("Invalid Firebase project");
        await tokenGate.WaitAsync(ct);
        try
        {
            if (accessToken == null || tokenExpires < Wire.Now + 60000)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var header = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
                var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(Wire.Encode(new { iss = account.GetProperty("client_email").GetString(), scope = "https://www.googleapis.com/auth/firebase.messaging", aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600 })));
                using var rsa = RSA.Create(); rsa.ImportFromPem(account.GetProperty("private_key").GetString()!);
                var assertion = header + "." + payload; assertion += "." + WebEncoders.Base64UrlEncode(rsa.SignData(Encoding.ASCII.GetBytes(assertion), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using var oauth = await clients.CreateClient("google").PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer" }, { "assertion", assertion } }), ct);
                oauth.EnsureSuccessStatusCode(); var data2 = await oauth.Content.ReadFromJsonAsync<JsonElement>(ct);
                accessToken = data2.GetProperty("access_token").GetString(); tokenExpires = Wire.Now + data2.GetProperty("expires_in").GetInt64() * 1000;
            }
        }
        finally { tokenGate.Release(); }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{project}/messages:send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new { message = new { token, data, android = new { priority = urgent ? "high" : "normal", ttl } } });
        using var response = await clients.CreateClient("google").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            try { var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct); if (error.TryGetProperty("error", out var outer) && outer.TryGetProperty("details", out var details) && details.EnumerateArray().Any(v => v.TryGetProperty("errorCode", out var code) && code.GetString() == "UNREGISTERED")) throw new InvalidFcmToken(token); }
            catch (JsonException) { }
        }
        DeliveryRetry.Check(response); response.EnsureSuccessStatusCode();
    }
}
public sealed class InvalidFcmToken(string token) : Exception { public string Token { get; } = token; }
public sealed class DeliveryRetry(long due) : Exception
{
    public long Due { get; } = due;
    public static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return;
        var retry = response.Headers.RetryAfter; var due = retry?.Date?.ToUnixTimeMilliseconds() ?? (Wire.Now + (long)(retry?.Delta?.TotalMilliseconds ?? 0));
        throw new DeliveryRetry(Math.Max(due, Wire.Now + (response.StatusCode == HttpStatusCode.TooManyRequests ? 60000 : 1000)));
    }
}

public sealed class DeliveryWorker(Database db, NotificationService service, IPushSender push, IHttpClientFactory clients, Settings settings, ILogger<DeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = 0L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                service.Sweep(); if (Wire.Now > nextCleanup) { service.Cleanup(); nextCleanup = Wire.Now + 3600000; }
                if (!await ProcessOne(stoppingToken)) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError("Delivery loop failed: {Type}", e.GetType().Name); await Task.Delay(3000, stoppingToken); }
        }
    }
    public async Task<bool> ProcessOne(CancellationToken ct)
    {
        var job = db.Run((c, t) =>
        {
            using var cmd = Cmd(c, t, "SELECT id,kind,notification_id,target_id,body,attempts FROM jobs WHERE status='pending' AND due<=$n AND lease<=$n ORDER BY due,id LIMIT 1", ("$n", Wire.Now));
            using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
            var j = new Job(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) + 1); r.Close();
            Exec(c, t, "UPDATE jobs SET lease=$l,attempts=attempts+1 WHERE id=$i", ("$l", Wire.Now + 30000), ("$i", j.Id)); return j;
        });
        if (job == null) return false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            if (job.Kind == "push")
            {
                var d = db.Run((c, t) => Get<Device>(c, t, "device", job.TargetId));
                if (d is { Revoked: false, FcmToken: not null })
                {
                    var n = service.Read(new Actor("device", d.Id, []), job.NotificationId);
                    if (n.Status is "pending" or "active")
                    {
                        await push.Send(d.FcmToken, n.Id, n.Content.Kind == "confirmation", deadline.Token);
                        db.Run((c, t) => { var key = n.Id + ":" + d.Id; var old = Get<Delivery>(c, t, "delivery", key); if (old?.Status is not ("device_received" or "opened")) Put(c, t, "delivery", key, new Delivery(n.Id, d.Id, "fcm_accepted", Wire.Now)); return 0; });
                    }
                }
            }
            else
            {
                var s = db.Run((c, t) => Get<Source>(c, t, "source", job.TargetId));
                if (s is { Revoked: false, WebhookUrl: not null, ProtectedWebhookSecret: not null })
                {
                    var uri = WebhookPolicy.Validate(s.WebhookUrl, settings); var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(service.Unprotect(s.ProtectedWebhookSecret)), Encoding.UTF8.GetBytes(timestamp + "." + job.Body)));
                    using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(job.Body, Encoding.UTF8, "application/json") };
                    request.Headers.Add("X-Notify-Event-Id", job.Id); request.Headers.Add("X-Notify-Timestamp", timestamp); request.Headers.Add("X-Notify-Signature", "v1=" + signature);
                    using var response = await clients.CreateClient("webhook").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); DeliveryRetry.Check(response); response.EnsureSuccessStatusCode();
                }
            }
            db.Run((c, t) => Exec(c, t, "UPDATE jobs SET status='done',lease=0 WHERE id=$i", ("$i", job.Id)));
        }
        catch (InvalidFcmToken invalid)
        {
            db.Run((c, t) => { var d = Get<Device>(c, t, "device", job.TargetId); if (d != null && d.FcmToken == invalid.Token) Put(c, t, "device", d.Id, d with { FcmToken = null }); return Exec(c, t, "UPDATE jobs SET status='failed',lease=0 WHERE id=$i", ("$i", job.Id)); });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Delivery {JobId} attempt {Attempt} failed: {Type}", job.Id, job.Attempts, e.GetType().Name);
            var due = Wire.Now + Math.Min(3600000, 1000L * (1L << Math.Min(job.Attempts, 12))) + Random.Shared.Next(500);
            if (e is DeliveryRetry retry) due = Math.Max(due, retry.Due);
            db.Run((c, t) => Exec(c, t, "UPDATE jobs SET status=$s,lease=0,due=$d WHERE id=$i", ("$i", job.Id), ("$s", job.Attempts >= 20 ? "failed" : "pending"), ("$d", due)));
        }
        return true;
    }
}
