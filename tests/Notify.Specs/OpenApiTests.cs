using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Notify;
using Xunit;

namespace NotifySpecs;

// The generated reference is part of the public contract. These tests keep the
// operation inventory, the session example and a few real error branches honest.
public sealed class OpenApiTests
{
    [Fact]
    public async Task EveryGeneratedOperationIsDocumentedAndUsesDeclaredSecurity()
    {
        using var app = new AppFactory();
        using var client = app.Client();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.EnumerateObject().Count() >= 40);

        var operations = 0;
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                operations++;
                var label = operation.Name.ToUpperInvariant() + " " + path.Name;
                var value = operation.Value;
                Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("summary").GetString()), label + " summary");
                Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("description").GetString()), label + " description");
                Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("operationId").GetString()), label + " operationId");
                Assert.NotEmpty(value.GetProperty("tags").EnumerateArray());
                Assert.True(value.TryGetProperty("security", out _), label + " security");
                var responses = value.GetProperty("responses");
                Assert.NotEmpty(responses.EnumerateObject());
                foreach (var status in responses.EnumerateObject())
                    Assert.False(string.IsNullOrWhiteSpace(status.Value.GetProperty("description").GetString()), label + " response " + status.Name);
            }
        }
        Assert.True(operations >= 45);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.DoesNotContain(schemas.EnumerateObject(), schema => schema.Name.StartsWith("AnonymousType", StringComparison.Ordinal));
        var create = schemas.GetProperty("CreateNotification").GetProperty("properties");
        Assert.Equal("message", create.GetProperty("kind").GetProperty("enum")[0].GetString());
        Assert.Equal(160, create.GetProperty("title").GetProperty("maxLength").GetInt32());
        Assert.True(schemas.GetProperty("Notification").GetProperty("properties").GetProperty("status").GetProperty("enum").GetArrayLength() > 0);
    }

    [Fact]
    public async Task SessionOperationDocumentsCredentialsActorAndCsrf()
    {
        using var app = new AppFactory();
        using var client = app.Client();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var session = document.RootElement.GetProperty("paths").GetProperty("/api/v1/session").GetProperty("get");
        Assert.False(session.TryGetProperty("requestBody", out _));
        Assert.DoesNotContain(session.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("in").GetString() is "path" or "query");
        var security = session.GetProperty("security").EnumerateArray().Select(item => item.EnumerateObject().First().Name).ToArray();
        Assert.Contains("Bearer", security);
        Assert.Contains("WebSession", security);
        var responses = session.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out var ok));
        Assert.True(responses.TryGetProperty("401", out var unauthorized));
        Assert.True(responses.TryGetProperty("429", out _));
        Assert.False(unauthorized.TryGetProperty("content", out _));
        var schema = ok.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("actor", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("csrfToken", out _));
    }

    [Fact]
    public async Task SessionEndpointRejectsAnonymousAndDescribesRealActors()
    {
        using var app = new AppFactory();
        using var anonymous = app.Client();
        var denied = await anonymous.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Empty(await denied.Content.ReadAsByteArrayAsync());

        var (integrationClient, source) = app.Integration(scopes: ["notify:send", "requests:create"]);
        using (integrationClient)
        {
            using var payload = JsonDocument.Parse(await integrationClient.GetStringAsync("/api/v1/session"));
            var actor = payload.RootElement.GetProperty("actor");
            Assert.Equal("integration", actor.GetProperty("kind").GetString());
            Assert.Equal(source.Id, actor.GetProperty("id").GetString());
            Assert.False(actor.GetProperty("isOwner").GetBoolean());
            Assert.False(string.IsNullOrEmpty(payload.RootElement.GetProperty("csrfToken").GetString()));
            Assert.Contains(actor.GetProperty("scopes").EnumerateArray(), scope => scope.GetString() == "notify:send");
        }

        var (deviceClient, device, key) = app.Device();
        using (deviceClient) using (key)
        {
            using var payload = JsonDocument.Parse(await deviceClient.GetStringAsync("/api/v1/session"));
            var actor = payload.RootElement.GetProperty("actor");
            Assert.Equal("device", actor.GetProperty("kind").GetString());
            Assert.Equal(device.Id, actor.GetProperty("id").GetString());
            Assert.True(actor.GetProperty("isOwner").GetBoolean());
            Assert.False(string.IsNullOrEmpty(payload.RootElement.GetProperty("csrfToken").GetString()));
        }
    }

    [Fact]
    public async Task RepresentativeHttpErrorsMatchTheReference()
    {
        using var app = new AppFactory();
        var (client, actor) = app.Integration();
        var (sendOnly, _) = app.Integration(scopes: ["notify:send"]);
        using (client) using (sendOnly)
        {
            var missingKey = await client.PostAsJsonAsync("/api/v1/notifications",
                new CreateNotification("message", "Fixture", "Body"));
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
            Assert.Equal("invalid_idempotency_key", await Code(missingKey));

            client.DefaultRequestHeaders.Add("Idempotency-Key", Wire.Secret());
            var created = await client.PostAsJsonAsync("/api/v1/notifications",
                new CreateNotification("message", "Fixture", "Body"));
            created.EnsureSuccessStatusCode();
            var notification = await created.Content.ReadFromJsonAsync<Notification>();
            Assert.NotNull(notification);

            var conflict = await client.PostAsJsonAsync("/api/v1/notifications",
                new CreateNotification("message", "Fixture", "Changed body"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("idempotency_conflict", await Code(conflict));

            var hidden = await sendOnly.GetAsync("/api/v1/notifications/" + notification!.Id);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal("notification_not_found", await Code(hidden));

            var wait = await client.GetAsync($"/api/v1/notifications/{notification.Id}/wait?seconds=30");
            Assert.Equal(HttpStatusCode.BadRequest, wait.StatusCode);
            Assert.Equal("invalid_wait_seconds", await Code(wait));

            var unknown = await client.PostAsync("/api/v1/notifications",
                new StringContent("""{"kind":"message","title":"t","body":"b","unknown":true}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Equal("invalid_request", await Code(unknown));

            var (phone, _, key) = app.Device(); using (phone) using (key)
            {
                var ordinaryChallenge = await phone.PostAsJsonAsync("/api/v1/calls/challenge",
                    new CallChallengeInput(actor.Id, SecurityLevel: "standard"));
                Assert.Equal(HttpStatusCode.BadRequest, ordinaryChallenge.StatusCode);
                Assert.Equal("call_verification_not_required", await Code(ordinaryChallenge));
            }
        }
    }

    private static async Task<string?> Code(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
