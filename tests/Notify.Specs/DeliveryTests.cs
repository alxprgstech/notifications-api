using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class FixtureForwarder(string destination) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { request.RequestUri = new Uri(destination + "/events"); return base.SendAsync(request, ct); }
}
public sealed class UnusedPush : IPushSender { public Task Send(string token, string id, bool urgent, CancellationToken ct) => throw new InvalidOperationException("No push expected in webhook fixture"); public Task SendCall(string token, string callId, string state, CancellationToken ct) => throw new InvalidOperationException("No call push expected in webhook fixture"); }
public sealed class DeliveryTests
{
    [Fact]
    public void RetryAfterAndQuotaAreRespected()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        var now = Wire.Now; Assert.True(Assert.Throws<DeliveryRetry>(() => DeliveryRetry.Check(response)).Due >= now + 599000);
    }
    [Fact]
    public async Task FailedWebhookRetriesStableEventAndDoesNotLoseAnswer()
    {
        await using var receiver = new FakeSso(); await receiver.Start(); using var app = new AppFactory { WebhookReceiver = receiver.Issuer };
        var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var db = app.Services.GetRequiredService<Database>(); var svc = app.Services.GetRequiredService<NotificationService>(); var secret = "synthetic-webhook-secret";
            db.Run((c, t) => { var s = Get<Source>(c, t, "source", source.Id)!; Put(c, t, "source", s.Id, s with { WebhookUrl = "https://fixture.test/events", ProtectedWebhookSecret = svc.Protect(secret) }); return 0; });
            var n = svc.Create(source, new CreateNotification("confirmation", "Fixture", "No side effect", "standard", DateTimeOffset.UtcNow.AddMinutes(5)), Wire.Secret()); svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes")));
            var worker = new DeliveryWorker(db, svc, new UnusedPush(), app.Services.GetRequiredService<IHttpClientFactory>(), app.Services.GetRequiredService<Settings>(), app.Services.GetRequiredService<ILogger<DeliveryWorker>>());
            Assert.True(await worker.ProcessOne(CancellationToken.None)); Assert.Equal("answered", svc.Read(source, n.Id).Status);
            Assert.Equal("pending", db.Run((c, t) => Rows(c, t, "SELECT status FROM jobs WHERE kind='webhook'")[0]));
            db.Run((c, t) => Exec(c, t, "UPDATE jobs SET due=0 WHERE kind='webhook'")); Assert.True(await worker.ProcessOne(CancellationToken.None));
            Assert.Equal("done", db.Run((c, t) => Rows(c, t, "SELECT status FROM jobs WHERE kind='webhook'")[0])); Assert.Equal(2, receiver.Webhooks.Count);
            var first = receiver.Webhooks[0]; var second = receiver.Webhooks[1]; Assert.Equal(first.body, second.body); Assert.Equal(first.eventId, second.eventId);
            foreach (var delivery in receiver.Webhooks) { var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(delivery.timestamp + "." + delivery.body))); Assert.Equal(expected, delivery.signature); }
        }
    }
    [Fact]
    public async Task WebManagementRequiresCsrfOriginAndPhoneAndShowsTokenOnce()
    {
        using var app = new AppFactory(); var (_, phone, key) = app.Device(); using (key)
        {
            using var web = app.Client(); var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("web");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", AppFactory.Owner), new Claim("notify_sid", Wire.Secret()) }, "web"));
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, "web"); web.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
            var input = new ManagementInput("integration.create", "Fixture", Scopes: ["notify:send"]);
            Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsJsonAsync("/api/v1/management", input)).StatusCode);
            var session = await web.GetFromJsonAsync<JsonElement>("/api/v1/session"); web.DefaultRequestHeaders.Add("X-CSRF-Token", session.GetProperty("csrfToken").GetString()); web.DefaultRequestHeaders.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsJsonAsync("/api/v1/management", input)).StatusCode); web.DefaultRequestHeaders.Remove("Origin"); web.DefaultRequestHeaders.Add("Origin", "https://localhost");
            var response = await web.PostAsJsonAsync("/api/v1/management", input); response.EnsureSuccessStatusCode(); var m = (await response.Content.ReadFromJsonAsync<Management>())!;
            var db = app.Services.GetRequiredService<Database>(); var svc = app.Services.GetRequiredService<NotificationService>(); var n = db.Run((c, t) => Rows(c, t, "SELECT data FROM notifications WHERE source_id='_system'").Select(Wire.Decode<Notification>).Single());
            Assert.Equal("pending", n.Status); var ch = svc.Challenge(phone, n.Id, new Answer("yes")); var sig = Convert.ToBase64String(key.SignData(Convert.FromBase64String(ch.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)); svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes"), ch.Id, sig));
            var result = await web.GetFromJsonAsync<JsonElement>("/api/v1/management/" + m.Id); var token = result.GetProperty("result").GetProperty("token").GetString(); Assert.NotNull(svc.Authenticate(token!));
            Assert.False((await web.GetFromJsonAsync<JsonElement>("/api/v1/management/" + m.Id)).TryGetProperty("result", out _));
        }
    }
}
