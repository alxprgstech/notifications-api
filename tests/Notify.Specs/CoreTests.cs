using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notify;
using Xunit;
using static Notify.Database;
using static Notify.NotificationService;

namespace NotifySpecs;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "artifacts", Wire.Id());
    public string Issuer { get; set; } = "https://auth.alxprgs.tech";
    public string? TrustedProxyNetwork { get; set; }
    public string? WebhookReceiver { get; set; }
    public int DailyLimit { get; set; } = 2000;
    public int AgentHeartbeatSeconds { get; set; } = 30;
    public int AgentOfflineSeconds { get; set; } = 90;
    public int AgentSendTimeoutSeconds { get; set; } = 5;
    public long AudioQuotaBytes { get; set; } = 512L * 1024 * 1024;
    public int AudioMessageTtlDays { get; set; } = 7;
    public int CallRingSeconds { get; set; } = 45;
    public int CallMediaConnectSeconds { get; set; } = 10;
    public int CallQueueFrames { get; set; } = 100;
    public IPushSender? PushSender { get; set; }
    public static readonly string Owner = "67560410-fdaf-427f-bf96-76c3cbba266b";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notify:DataPath"] = Root,
            ["Notify:PublicUrl"] = "https://localhost",
            ["Notify:OwnerSub"] = Owner,
            ["Notify:WebClientSecret"] = "synthetic-client-secret-not-a-real-credential",
            ["Notify:SsoIssuer"] = Issuer
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Settings>(); services.AddSingleton(new Settings { DataPath = Root, PublicUrl = "https://localhost", OwnerSub = Owner, WebClientSecret = "synthetic-client-secret-not-a-real-credential", SsoIssuer = Issuer, TrustedProxyNetwork = TrustedProxyNetwork, WebhookHosts = ["fixture.test"], SourceDailyLimit = DailyLimit, AgentHeartbeatSeconds = AgentHeartbeatSeconds, AgentOfflineSeconds = AgentOfflineSeconds, AgentSendTimeoutSeconds = AgentSendTimeoutSeconds, AudioQuotaBytes = AudioQuotaBytes, AudioMessageTtlDays = AudioMessageTtlDays, CallRingSeconds = CallRingSeconds, CallMediaConnectSeconds = CallMediaConnectSeconds, CallQueueFrames = CallQueueFrames });
            foreach (var registration in services.Where(d => d.ImplementationType == typeof(DeliveryWorker)).ToList()) services.Remove(registration);
            if (PushSender != null) { services.RemoveAll<IPushSender>(); services.AddSingleton(PushSender); }
            if (WebhookReceiver != null) services.AddHttpClient("webhook").ConfigurePrimaryHttpMessageHandler(() => new FixtureForwarder(WebhookReceiver));
        });
    }
    public (HttpClient client, Actor actor) Integration(string? id = null, string[]? scopes = null)
    {
        id ??= Wire.Id(); scopes ??= ["notify:send", "requests:create", "requests:read", "requests:cancel", "icons:write"];
        var token = Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "source", id, new Source(id, "Synthetic test", scopes)); return Credential(c, t, "integration", id); });
        return (Client(token), new Actor("integration", id, scopes));
    }
    public (HttpClient client, Actor actor, ECDsa key) Device()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var id = Wire.Id();
        var token = Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "device", id, new Device(id, "Synthetic phone", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))); return Credential(c, t, "device", id); });
        return (Client(token), new Actor("device", id, []), key);
    }
    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return client;
    }
}

public sealed class CoreTests
{
    private static CreateNotification Input(string level = "standard", DateTimeOffset? expiry = null) => new("confirmation", "Synthetic decision", "No external action", level, expiry ?? DateTimeOffset.UtcNow.AddMinutes(5));
    [Fact]
    public async Task HttpRequiresCredentialAndIntegrationCannotAnswer()
    {
        using var app = new AppFactory(); using var anonymous = app.Client(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/notifications")).StatusCode);
        var (source, actor) = app.Integration(); using (source) { var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(actor, Input(), Wire.Secret()); var response = await source.PostAsJsonAsync($"/api/v1/notifications/{n.Id}/response", new SubmitAnswer(new Answer("yes"))); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); }
    }
    [Fact]
    public void SendOnlyTokenCanRetryButCannotRead()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(scopes: ["notify:send"]); var svc = app.Services.GetRequiredService<NotificationService>();
        var input = new CreateNotification("message", "Fixture", "Only send"); var key = Wire.Secret(); var n = svc.Create(source, input, key); Assert.Equal(n.Id, svc.Create(source, input, key).Id); Assert.Throws<Fault>(() => svc.Read(source, n.Id));
    }
    [Fact]
    public void SourceQuotaDoesNotChargeIdempotentRetries()
    {
        using var app = new AppFactory { DailyLimit = 1 }; var (_, source) = app.Integration(); var svc = app.Services.GetRequiredService<NotificationService>(); var input = new CreateNotification("message", "Fixture", "Quota"); var key = Wire.Secret();
        var n = svc.Create(source, input, key); Assert.Equal(n.Id, svc.Create(source, input, key).Id); Assert.Equal(429, Assert.Throws<Fault>(() => svc.Create(source, input, Wire.Secret())).Status);
    }
    [Fact]
    public void HistoryIsBoundedPreviewButDecisionUsesFullContent()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var input = Input("elevated") with { Body = new string('Т', 16000) }; var n = svc.Create(source, input, Wire.Secret());
            using var sync = JsonDocument.Parse(Wire.Encode(svc.Sync(phone, 0))); var preview = sync.RootElement.GetProperty("items")[0]; Assert.True(preview.GetProperty("previewOnly").GetBoolean()); Assert.Equal(1000, preview.GetProperty("content").GetProperty("body").GetString()!.Length);
            Assert.Equal(16000, svc.Read(phone, n.Id).Content.Body.Length); var ch = svc.Challenge(phone, n.Id, new Answer("yes")); using var payload = JsonDocument.Parse(Convert.FromBase64String(ch.Payload)); Assert.Equal(Wire.Hash(Wire.Encode(input)), payload.RootElement.GetProperty("notificationHash").GetString());
            using var reset = JsonDocument.Parse(Wire.Encode(svc.Sync(phone, long.MaxValue))); Assert.True(reset.RootElement.GetProperty("reset").GetBoolean());
        }
    }
    [Fact]
    public async Task HttpIdempotencyConflictAndSourceIsolation()
    {
        using var app = new AppFactory(); var (a, actor) = app.Integration(); var (b, _) = app.Integration(); using (a) using (b)
        {
            var input = Input(); var key = Wire.Secret(); a.DefaultRequestHeaders.Add("Idempotency-Key", key);
            var first = await a.PostAsJsonAsync("/api/v1/notifications", input); first.EnsureSuccessStatusCode(); var n = (await first.Content.ReadFromJsonAsync<Notification>())!;
            var second = await a.PostAsJsonAsync("/api/v1/notifications", input); Assert.Equal(n.Id, (await second.Content.ReadFromJsonAsync<Notification>())!.Id);
            Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/v1/notifications", input with { Body = "Changed action" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/v1/notifications/" + n.Id)).StatusCode);
        }
    }
    [Fact]
    public void ElevatedAnswerIsBoundToDeviceAnswerAndOneUse()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); var (_, other, otherKey) = app.Device(); using (key) using (otherKey)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input("elevated"), Wire.Secret());
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes")))).Status);
            var ch = svc.Challenge(phone, n.Id, new Answer("yes")); var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(ch.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.Respond(other, n.Id, new SubmitAnswer(new Answer("yes"), ch.Id, signature))).Status);
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("no"), ch.Id, signature))).Status);
            var wrongSignature = Convert.ToBase64String(otherKey.SignData(Convert.FromBase64String(ch.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes"), ch.Id, wrongSignature))).Status);
            Assert.Equal("answered", svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes"), ch.Id, signature)).Status);
            Assert.Equal(409, Assert.Throws<Fault>(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes"), ch.Id, signature))).Status);
        }
    }
    [Fact]
    public async Task ConcurrentResponsesOnlyOneWins()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input(), Wire.Secret());
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => { try { svc.Respond(phone, n.Id, new SubmitAnswer(new Answer(i % 2 == 0 ? "yes" : "no"))); return 200; } catch (Fault e) { return e.Status; } })));
            Assert.Single(results, x => x == 200); Assert.Equal(7, results.Count(x => x == 409));
        }
    }
    [Fact]
    public async Task ExpiryAndCancellationNeverApprove()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var expired = svc.Create(source, Input(expiry: DateTimeOffset.UtcNow.AddMilliseconds(100)), Wire.Secret()); await Task.Delay(150);
            Assert.Equal("expired", svc.Read(source, expired.Id).Status); Assert.Throws<Fault>(() => svc.Respond(phone, expired.Id, new SubmitAnswer(new Answer("yes"))));
            var cancelled = svc.Create(source, Input(), Wire.Secret()); Assert.Equal("cancelled", svc.Cancel(source, cancelled.Id).Status); Assert.Throws<Fault>(() => svc.Respond(phone, cancelled.Id, new SubmitAnswer(new Answer("yes"))));
        }
    }
    [Fact]
    public void CustomAnswersValidateAndRevocationIsEffective()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input() with { AllowCustom = false }, Wire.Secret()); Assert.Throws<Fault>(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("custom", "other"))));
            var db = app.Services.GetRequiredService<Database>(); db.Run((c, t) => { var d = Get<Device>(c, t, "device", phone.Id)!; Put(c, t, "device", d.Id, d with { Revoked = true }); return 0; }); Assert.Equal(401, Assert.Throws<Fault>(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes")))).Status);
        }
    }
    [Fact]
    public void BackupAndReopenPreserveStateAndJobs()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var db = app.Services.GetRequiredService<Database>(); db.Run((c, t) => { var d = Get<Device>(c, t, "device", phone.Id)!; Put(c, t, "device", d.Id, d with { FcmToken = "synthetic-fcm-token" }); Exec(c, t, "INSERT INTO agent_commands VALUES($i,$a,$s,'task','{}',$c,$e,'queued',0,NULL,NULL,NULL)", ("$i", Wire.Id()), ("$a", "agent-fixture"), ("$s", source.Id), ("$c", Wire.Now), ("$e", Wire.Now + 60000)); return 0; });
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input(), Wire.Secret()); var backup = Path.Combine(app.Root, "backup.db"); db.Backup(backup);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + backup); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", cmd.ExecuteScalar()); cmd.CommandText = "SELECT count(*) FROM jobs WHERE status='pending'"; Assert.Equal(1L, cmd.ExecuteScalar()); cmd.CommandText = "SELECT count(*) FROM agent_commands WHERE status='queued'"; Assert.Equal(1L, cmd.ExecuteScalar());
            var reopened = new Database(new Settings { DataPath = app.Root, OwnerSub = AppFactory.Owner }); Assert.Equal(n.Id, reopened.Run((cn, t) => Wire.Decode<Notification>(Rows(cn, t, "SELECT data FROM notifications WHERE id=$i", ("$i", n.Id))[0])).Id);
            Assert.Throws<InvalidOperationException>(() => new Database(new Settings { DataPath = app.Root, OwnerSub = Guid.NewGuid().ToString() }));
        }
    }
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("fc00::1")]
    public void WebhooksRejectPrivateNetworks(string value) => Assert.False(WebhookPolicy.IsPublic(IPAddress.Parse(value)));
    [Fact]
    public void WebhookAllowlistAndRedirectUrlValidation()
    {
        var settings = new Settings { WebhookHosts = ["example.com"] }; Assert.Equal("example.com", WebhookPolicy.Validate("https://example.com/events", settings).Host);
        foreach (var url in new[] { "http://example.com", "https://example.com@localhost", "https://example.com:8443", "https://other.example/events", "https://example.com/#fragment" }) Assert.Throws<Fault>(() => WebhookPolicy.Validate(url, settings));
    }
    [Fact]
    public async Task OpenApiAndCapabilitiesAreActual()
    {
        using var app = new AppFactory(); using var client = app.Client(); var spec = await client.GetAsync("/openapi/v1.json"); spec.EnsureSuccessStatusCode(); var text = await spec.Content.ReadAsStringAsync(); Assert.Contains("/api/v1/notifications", text); Assert.Contains("/api/v1/notifications/{id}/form-challenge", text); Assert.Contains("/api/v1/agents", text); Assert.Contains("/api/v1/audio/uploads", text); Assert.Contains("/api/v1/voice-messages", text); Assert.Contains("/api/v1/calls", text); Assert.Contains("/api/v1/calls/{id}/accept", text); Assert.Contains("/api/v1/calls/{id}/challenge", text); Assert.Contains("/api/v1/calls/incoming", text); var config = await client.GetFromJsonAsync<JsonElement>("/api/config"); Assert.True(config.GetProperty("capabilities").GetProperty("calls").GetBoolean()); Assert.True(config.GetProperty("capabilities").GetProperty("forms").GetBoolean()); Assert.True(config.GetProperty("capabilities").GetProperty("agents").GetBoolean()); Assert.True(config.GetProperty("capabilities").GetProperty("voiceMessages").GetBoolean()); Assert.Equal("0.2.0", config.GetProperty("minClientVersion").GetProperty("forms").GetString()); Assert.Equal("0.3.0", config.GetProperty("minClientVersion").GetProperty("voiceMessages").GetString()); Assert.Equal("0.5.0", config.GetProperty("minClientVersion").GetProperty("calls").GetString());
    }
    [Fact]
    public async Task OfficialMcpEndpointAcceptsIntegrationAndRejectsAnonymous()
    {
        using var app = new AppFactory(); using var anonymous = app.Client(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" })).StatusCode);
        var (client, _) = app.Integration(); using (client) { client.DefaultRequestHeaders.Accept.ParseAdd("application/json"); client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream"); var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "notify-spec", version = "1" } } }); response.EnsureSuccessStatusCode(); Assert.Contains("protocolVersion", await response.Content.ReadAsStringAsync()); }
    }
}
