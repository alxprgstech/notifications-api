using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class FormTests
{
    private static FormSpec Spec() => new([
        new FormField("env", "Среда", "single_choice", true, "Куда выкатываем", Options: [new("staging", "Staging"), new("prod", "Prod")]),
        new FormField("note", "Комментарий", "text", MaxLength: 50),
        new FormField("count", "Количество", "number", true, Min: 1, Max: 10),
        new FormField("dry", "Dry run", "boolean", true),
        new FormField("tags", "Теги", "multi_choice", Options: [new("a", "A"), new("b", "B")])
    ]);
    private static CreateNotification Input(string level = "standard", FormSpec? spec = null) => new("form", "Synthetic form", "No external action", level, DateTimeOffset.UtcNow.AddMinutes(5), false, Form: spec ?? Spec());
    private static FormValues Values(params (string key, object value)[] pairs)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) values[key] = JsonSerializer.SerializeToElement(value);
        return new FormValues(values);
    }
    private static FormValues Valid() => Values(("env", "prod"), ("note", "Проверено"), ("count", 3), ("dry", true), ("tags", new[] { "a", "b" }));
    private static int Status(Action action) { try { action(); return 200; } catch (Fault e) { return e.Status; } }
    private static string Code(Action action) { try { action(); return "ok"; } catch (Fault e) { return e.Code; } }

    [Fact]
    public void InvalidFormSpecificationsAreRejected()
    {
        var bad = new List<FormSpec>
        {
            new([]),
            new([new("a", "A", "text"), new("a", "B", "text")]),
            new(Enumerable.Range(0, 21).Select(i => new FormField("f" + i, "F", "text")).ToList()),
            new([new("a", "", "text")]),
            new([new("a", new string('x', 161), "text")]),
            new([new("a", "A", "text", MaxLength: 4001)]),
            new([new("a", "A", "text", Options: [new("x", "X")])]),
            new([new("a", "A", "boolean", MaxLength: 3)]),
            new([new("a", "A", "number", Min: 5, Max: 1)]),
            new([new("a", "A", "single_choice")]),
            new([new("a", "A", "single_choice", Options: [])]),
            new([new("a", "A", "single_choice", Options: [new("x", "X"), new("x", "Y")])]),
            new([new("a", "A", "multi_choice", Options: Enumerable.Range(0, 21).Select(i => new FormOption("o" + i, "O")).ToList())]),
            new([new("a", "A", "unknown")]),
            new([new("a", "A", "text", Description: new string('d', 1001))]),
            new([new("bad id", "A", "text")])
        };
        foreach (var spec in bad) Assert.Equal("invalid_form", Code(() => NotificationService.Validate(Input(spec: spec))));
        Assert.Equal("invalid_notification", Code(() => NotificationService.Validate(new CreateNotification("message", "T", "B", Form: Spec()))));
        Assert.Equal("invalid_notification", Code(() => NotificationService.Validate(new CreateNotification("form", "T", "B", ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5), AllowCustom: false))));
        NotificationService.Validate(Input()); NotificationService.Validate(new CreateNotification("message", "T", "B"));
    }

    [Fact]
    public void FormValuesAreTypeCheckedWithoutCoercion()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input(), Wire.Secret());
            var invalid = new[]
            {
                Values(("env", "prod"), ("count", 3), ("dry", "true")),
                Values(("env", "prod"), ("count", 3), ("dry", true), ("unknown", 1)),
                Values(("env", "prod"), ("dry", true)),
                Values(("env", "prod"), ("count", 11), ("dry", true)),
                Values(("env", "prod"), ("count", 0), ("dry", true)),
                Values(("env", "prod"), ("count", "3"), ("dry", true)),
                Values(("env", "other"), ("count", 3), ("dry", true)),
                Values(("env", "prod"), ("count", 3), ("dry", true), ("tags", new[] { "a", "a" })),
                Values(("env", "prod"), ("count", 3), ("dry", true), ("tags", new[] { "c" })),
                Values(("env", "prod"), ("count", 3), ("dry", true), ("note", new string('n', 51)))
            };
            foreach (var values in invalid) Assert.Equal(400, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(Form: values))));
            Assert.Equal("invalid_form_answer", Code(() => svc.Respond(phone, n.Id, new SubmitAnswer(Form: Values(("env", "prod"), ("count", 3), ("dry", "true"))))));
            Assert.Equal("form_required_missing", Code(() => svc.Respond(phone, n.Id, new SubmitAnswer(Form: Values(("env", "prod"), ("dry", true))))));
            Assert.Equal(400, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(new Answer("yes"), Form: Valid()))));
            Assert.Equal("pending", svc.Read(source, n.Id).Status);
            Assert.Equal("answered", svc.Respond(phone, n.Id, new SubmitAnswer(Form: Valid())).Status);
            Assert.Equal(409, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(Form: Valid()))));
        }
    }

    [Fact]
    public void FormAnswerIsFinalAndPreviewHidesValues()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input() with { Body = new string('Т', 16000) }, Wire.Secret());
            var answered = svc.Respond(phone, n.Id, new SubmitAnswer(Form: Valid()));
            Assert.Equal("answered", answered.Status); Assert.Null(answered.Answer); Assert.Equal("prod", answered.FormAnswer!.Values["env"].GetString()); Assert.NotNull(answered.AnsweredAt);
            using var page = JsonDocument.Parse(Wire.Encode(svc.List(source, long.MaxValue, "~", 50))); var preview = page.RootElement.GetProperty("items")[0];
            Assert.True(preview.GetProperty("previewOnly").GetBoolean()); Assert.Equal(1000, preview.GetProperty("content").GetProperty("body").GetString()!.Length);
            Assert.Empty(preview.GetProperty("formAnswer").GetProperty("values").EnumerateObject());
            var full = svc.Read(source, n.Id); Assert.Equal(5, full.FormAnswer!.Values.Count); Assert.Equal(16000, full.Content.Body.Length);
            Assert.Equal(409, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(Form: Valid()))));
            using var sync = JsonDocument.Parse(Wire.Encode(svc.Sync(phone, 0))); var element = sync.RootElement.GetProperty("items")[0];
            Assert.True(element.GetProperty("previewOnly").GetBoolean()); Assert.Empty(element.GetProperty("formAnswer").GetProperty("values").EnumerateObject());
        }
    }

    [Fact]
    public void ExpiredAndCancelledFormsNeverAnswer()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>();
            var expired = svc.Create(source, Input() with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(80) }, Wire.Secret()); System.Threading.Thread.Sleep(120);
            Assert.Equal(409, Status(() => svc.Respond(phone, expired.Id, new SubmitAnswer(Form: Valid())))); Assert.Equal("expired", svc.Read(source, expired.Id).Status);
            var cancelled = svc.Create(source, Input(), Wire.Secret()); Assert.Equal("cancelled", svc.Cancel(source, cancelled.Id).Status);
            Assert.Equal(409, Status(() => svc.Respond(phone, cancelled.Id, new SubmitAnswer(Form: Valid()))));
            Assert.Equal(409, Status(() => svc.ChallengeForm(phone, expired.Id, Valid())));
        }
    }

    [Fact]
    public void ElevatedFormBindsWholeAnswerSetAndSignature()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); var (_, other, otherKey) = app.Device(); using (key) using (otherKey)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input("elevated"), Wire.Secret());
            Assert.Equal(400, Status(() => svc.Challenge(phone, n.Id, new Answer("yes")))); Assert.Equal(400, Status(() => svc.ChallengeForm(phone, n.Id, Values(("env", "prod"), ("count", 11), ("dry", true)))));
            var chosen = Values(("env", "prod"), ("count", 3), ("dry", true));
            var ch = svc.ChallengeForm(phone, n.Id, chosen);
            using var payload = JsonDocument.Parse(Convert.FromBase64String(ch.Payload));
            Assert.Equal("alxprgs.notify.form.v1", payload.RootElement.GetProperty("protocol").GetString()); Assert.Equal("prod", payload.RootElement.GetProperty("values").GetProperty("env").GetString());
            Assert.Equal(403, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(ChallengeId: ch.Id, Form: chosen))));
            var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(ch.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            var changed = Values(("env", "staging"), ("count", 4), ("dry", false));
            Assert.Equal(403, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(ChallengeId: ch.Id, Signature: signature, Form: changed))));
            var otherSignature = Convert.ToBase64String(otherKey.SignData(Convert.FromBase64String(ch.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            Assert.Equal(403, Status(() => svc.Respond(other, n.Id, new SubmitAnswer(ChallengeId: ch.Id, Signature: signature, Form: chosen))));
            Assert.Equal(403, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(ChallengeId: ch.Id, Signature: otherSignature, Form: chosen))));
            var answered = svc.Respond(phone, n.Id, new SubmitAnswer(ChallengeId: ch.Id, Signature: signature, Form: chosen));
            Assert.Equal("answered", answered.Status); Assert.Equal("prod", answered.FormAnswer!.Values["env"].GetString());
            Assert.Equal(409, Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(ChallengeId: ch.Id, Signature: signature, Form: chosen))));
        }
    }

    [Fact]
    public async Task ConcurrentFormResponsesOnlyOneWins()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var n = svc.Create(source, Input(), Wire.Secret());
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => Status(() => svc.Respond(phone, n.Id, new SubmitAnswer(Form: Values(("env", i % 2 == 0 ? "prod" : "staging"), ("count", i + 1), ("dry", true))))))));
            Assert.Single(results, x => x == 200); Assert.Equal(7, results.Count(x => x == 409));
        }
    }

    [Fact]
    public void FormWebhookCarriesAnswer()
    {
        using var app = new AppFactory(); var (_, source) = app.Integration(); var (_, phone, key) = app.Device(); using (key)
        {
            var db = app.Services.GetRequiredService<Database>(); var svc = app.Services.GetRequiredService<NotificationService>();
            db.Run((c, t) => { var s = Get<Source>(c, t, "source", source.Id)!; Put(c, t, "source", s.Id, s with { WebhookUrl = "https://fixture.test/events", ProtectedWebhookSecret = svc.Protect("synthetic-webhook-secret") }); return 0; });
            var n = svc.Create(source, Input(), Wire.Secret()); svc.Respond(phone, n.Id, new SubmitAnswer(Form: Valid()));
            var body = db.Run((c, t) => Rows(c, t, "SELECT body FROM jobs WHERE kind='webhook'").Single());
            using var webhook = JsonDocument.Parse(body); Assert.Equal("notification.answered", webhook.RootElement.GetProperty("type").GetString());
            Assert.Equal("prod", webhook.RootElement.GetProperty("notification").GetProperty("formAnswer").GetProperty("values").GetProperty("env").GetString());
        }
    }

    [Fact]
    public async Task HttpFormShapeIsValidated()
    {
        using var app = new AppFactory(); var (client, _) = app.Integration(); using (client)
        {
            client.DefaultRequestHeaders.Add("Idempotency-Key", Wire.Secret());
            var missing = await client.PostAsJsonAsync("/api/v1/notifications", new { kind = "form", title = "T", body = "B", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), securityLevel = "standard", allowCustom = false });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            var wrong = await client.PostAsJsonAsync("/api/v1/notifications", new { kind = "message", title = "T", body = "B", form = new { fields = new[] { new { id = "a", label = "A", type = "text" } } } });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            var valid = await client.PostAsJsonAsync("/api/v1/notifications", new { kind = "form", title = "T", body = "B", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), securityLevel = "standard", allowCustom = false, form = new { fields = new[] { new { id = "a", label = "A", type = "text" } } } });
            Assert.True(valid.IsSuccessStatusCode, await valid.Content.ReadAsStringAsync());
        }
    }
}
