using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class AudioTests
{
    private static byte[] Wav(int milliseconds = 1000, short channels = 1, int rate = 16000, short bits = 16, short format = 1)
    {
        var block = channels * (bits / 8); var data = rate * block * milliseconds / 1000;
        var bytes = new byte[44 + data];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0); BitConverter.GetBytes(36 + data).CopyTo(bytes, 4); Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12); BitConverter.GetBytes(16).CopyTo(bytes, 16); BitConverter.GetBytes(format).CopyTo(bytes, 20);
        BitConverter.GetBytes(channels).CopyTo(bytes, 22); BitConverter.GetBytes(rate).CopyTo(bytes, 24); BitConverter.GetBytes(rate * block).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)block).CopyTo(bytes, 32); BitConverter.GetBytes(bits).CopyTo(bytes, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36); BitConverter.GetBytes(data).CopyTo(bytes, 40);
        return bytes;
    }
    private static async Task<HttpResponseMessage> UploadResponse(HttpClient client, byte[] bytes)
    {
        using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PostAsync("/api/v1/audio/uploads", content);
    }
    private static async Task<JsonElement> Upload(HttpClient client, byte[] bytes)
    {
        using var response = await UploadResponse(client, bytes); var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, text);
        using var document = JsonDocument.Parse(text); return document.RootElement.Clone();
    }
    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;
    private static WebSocketClient SocketClient(AppFactory app, string token) { var client = app.Server.CreateWebSocketClient(); client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token; return client; }
    private static Task Send(WebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Receive(WebSocket socket)
    {
        var buffer = new byte[65536]; using var stream = new MemoryStream(); WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed"); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray()); return document.RootElement.Clone();
    }
    private static Agent RegisterAgent(AppFactory app, string sourceId)
    {
        var agent = new Agent(Wire.Id(), sourceId, "Audio agent", ["text_tasks"], Wire.Now);
        app.Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "agent", agent.Id, agent); return 0; });
        return agent;
    }

    [Fact]
    public async Task TextVoiceUsesServerSileroAndExplicitWavRequiresTranscript()
    {
        using var app = new AppFactory { UseSpeechStub = true };
        var (source, _) = app.Integration(scopes: ["notify:send"]);
        using (source)
        {
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications")
            {
                Content = JsonContent.Create(new { kind = "voice", title = "Text voice", body = "", expiresAt = expires, voiceText = "Проверка синтеза" }),
            };
            request.Headers.Add("Idempotency-Key", "text-voice-1");
            using var response = await source.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            using var document = JsonDocument.Parse(text);
            var voice = document.RootElement.GetProperty("content").GetProperty("voice");
            Assert.Equal("Проверка синтеза", voice.GetProperty("transcript").GetString());
            Assert.Contains("Проверка синтеза", app.Speech.Synthesized);
            Assert.True((await source.GetAsync("/api/v1/audio/" + voice.GetProperty("audioId").GetString())).IsSuccessStatusCode);

            var upload = await Upload(source, Wav(300));
            using var missing = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications")
            {
                Content = JsonContent.Create(new { kind = "voice", title = "No transcript", body = "", expiresAt = expires, voiceUpload = new { uploadId = upload.GetProperty("uploadId").GetString() } }),
            };
            missing.Headers.Add("Idempotency-Key", "wav-no-transcript");
            Assert.Equal(HttpStatusCode.BadRequest, (await source.SendAsync(missing)).StatusCode);

            var second = await Upload(source, Wav(300));
            using var withText = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications")
            {
                Content = JsonContent.Create(new { kind = "voice", title = "With transcript", body = "", expiresAt = expires, voiceUpload = new { uploadId = second.GetProperty("uploadId").GetString() }, voiceTranscript = "Точный текст" }),
            };
            withText.Headers.Add("Idempotency-Key", "wav-with-transcript");
            using var accepted = await source.SendAsync(withText);
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task DeviceVoiceMessageGetsLocalTranscript()
    {
        using var app = new AppFactory { UseSpeechStub = true };
        var (sourceClient, source) = app.Integration(scopes: ["notify:send", "requests:read", "agents:connect"]);
        var (phoneClient, phone, key) = app.Device();
        using (sourceClient) using (phoneClient) using (key)
        {
            RegisterAgent(app, source.Id);
            var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            using var notificationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications")
            {
                Content = JsonContent.Create(new { kind = "voice", title = "Voice target", body = "", expiresAt = expires, voiceText = "Целевое сообщение" }),
            };
            notificationRequest.Headers.Add("Idempotency-Key", "voice-target-1");
            using var notificationResponse = await sourceClient.SendAsync(notificationRequest);
            notificationResponse.EnsureSuccessStatusCode();
            using var notification = JsonDocument.Parse(await notificationResponse.Content.ReadAsStringAsync());
            var target = notification.RootElement.GetProperty("id").GetString();
            var upload = await Upload(phoneClient, Wav(300));
            using var sent = await phoneClient.PostAsJsonAsync("/api/v1/voice-messages", new { uploadId = upload.GetProperty("uploadId").GetString(), notificationId = target });
            var body = await sent.Content.ReadAsStringAsync();
            Assert.True(sent.IsSuccessStatusCode, body);
            using var created = JsonDocument.Parse(body);
            var status = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/voice-messages/" + created.RootElement.GetProperty("id").GetString())).RootElement;
            Assert.Equal("Проверь сборку", status.GetProperty("transcript").GetString());
            Assert.Equal("complete", status.GetProperty("transcriptionStatus").GetString());
        }
    }

    [Fact]
    public async Task WavValidationRejectsWrongParametersAndAcceptsPcm16Mono16Khz()
    {
        using var app = new AppFactory(); var (source, _) = app.Integration();
        Assert.Equal(400, (int)(await UploadResponse(source, Array.Empty<byte>())).StatusCode);
        Assert.Equal(400, (int)(await UploadResponse(source, Wav(channels: 2))).StatusCode);
        Assert.Equal(400, (int)(await UploadResponse(source, Wav(rate: 8000))).StatusCode);
        Assert.Equal(400, (int)(await UploadResponse(source, Wav(bits: 8))).StatusCode);
        Assert.Equal(400, (int)(await UploadResponse(source, Wav(format: 3, bits: 32))).StatusCode);
        var corrupt = Wav(); corrupt[4] += 1; Assert.Equal(400, (int)(await UploadResponse(source, corrupt)).StatusCode);
        Assert.Equal(400, (int)(await UploadResponse(source, Wav()[..40])).StatusCode);
        var ok = await Upload(source, Wav(1500)); Assert.Equal(1500, ok.GetProperty("durationMs").GetInt32()); Assert.Equal(48044, ok.GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public async Task DeviceVoiceMessageQueuesThenDeliversAndAgentCanDownloadOwnedAudio()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(scopes: ["notify:send", "requests:create", "requests:read", "agents:connect"]); var (phoneClient, phone, key) = app.Device(); using (sourceClient) using (phoneClient) using (key)
        {
            var agent = RegisterAgent(app, source.Id); var svc = app.Services.GetRequiredService<NotificationService>(); var target = svc.Create(source, new CreateNotification("message", "Agent question", "Reply by voice"), Wire.Secret());
            var wav = Wav(1200); var upload = await Upload(phoneClient, wav);
            var sent = await phoneClient.PostAsJsonAsync("/api/v1/voice-messages", new { uploadId = upload.GetProperty("uploadId").GetString(), notificationId = target.Id }); sent.EnsureSuccessStatusCode();
            using var sentJson = JsonDocument.Parse(await sent.Content.ReadAsStringAsync()); var messageId = sentJson.RootElement.GetProperty("id").GetString()!;
            using var queued = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/voice-messages/" + messageId)); Assert.Equal("queued", queued.RootElement.GetProperty("status").GetString()); Assert.True(queued.RootElement.GetProperty("audioAvailable").GetBoolean());
            using var socket = await SocketClient(app, Token(sourceClient)).ConnectAsync(new Uri("ws://localhost/api/v1/agents/connect"), CancellationToken.None);
            await Send(socket, new { type = "hello", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), agentId = agent.Id, name = "Audio runtime", capabilities = new[] { "voice_messages" }, sessions = Array.Empty<object>() });
            _ = await Receive(socket);
            using var command = JsonDocument.Parse(Wire.Encode(await Receive(socket))); Assert.Equal("voice_message", command.RootElement.GetProperty("command").GetString()); Assert.Equal(messageId, command.RootElement.GetProperty("eventId").GetString()); Assert.Equal(target.Id, command.RootElement.GetProperty("payload").GetProperty("inReplyTo").GetString());
            var audioId = command.RootElement.GetProperty("payload").GetProperty("audioId").GetString()!;
            using var downloaded = await sourceClient.GetAsync("/api/v1/audio/" + audioId); downloaded.EnsureSuccessStatusCode(); Assert.Equal(wav, await downloaded.Content.ReadAsByteArrayAsync());
            await Send(socket, new { type = "ack", protocolVersion = AgentHub.Protocol, eventId = Wire.Id(), commandEventId = messageId, status = "done" });
            await Task.Delay(150);
            using var delivered = JsonDocument.Parse(await phoneClient.GetStringAsync("/api/v1/voice-messages/" + messageId)); Assert.Equal("delivered", delivered.RootElement.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await phoneClient.DeleteAsync("/api/v1/voice-messages/" + messageId)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await phoneClient.GetAsync("/api/v1/voice-messages/" + messageId)).StatusCode);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    [Fact]
    public async Task VoiceNotificationAudioIsPrivateAndElevatedNeedsActionBoundGrant()
    {
        using var app = new AppFactory(); var (source, sourceActor) = app.Integration(); var (otherSource, _) = app.Integration(); var (phoneClient, phone, key) = app.Device(); var (otherClient, other, otherKey) = app.Device(); using (source) using (otherSource) using (phoneClient) using (otherClient) using (key) using (otherKey)
        {
            var svc = app.Services.GetRequiredService<NotificationService>();
            var standardWav = Wav(1000); var standardUpload = await Upload(source, standardWav);
            var standard = svc.Create(sourceActor, new CreateNotification("voice", "Voice note", "Listen", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), VoiceUpload: new VoiceInput(standardUpload.GetProperty("uploadId").GetString()!), VoiceTranscript: "Standard voice transcript"), Wire.Secret());
            Assert.NotNull(standard.Content.Voice); Assert.Equal(standard.Content.ExpiresAt!.Value.ToUnixTimeMilliseconds(), standard.Content.Voice!.ExpiresAt);
            var audioId = standard.Content.Voice.AudioId;
            using var bytes = await phoneClient.GetAsync("/api/v1/audio/" + audioId); bytes.EnsureSuccessStatusCode(); Assert.Equal(standardWav, await bytes.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await otherSource.GetAsync("/api/v1/audio/" + audioId)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await phoneClient.GetAsync("/api/v1/audio/not-an-id")).StatusCode);
            var elevatedWav = Wav(1000); var elevatedUpload = await Upload(source, elevatedWav);
            var elevated = svc.Create(sourceActor, new CreateNotification("voice", "Secret note", "Listen privately", "elevated", DateTimeOffset.UtcNow.AddHours(1), VoiceUpload: new VoiceInput(elevatedUpload.GetProperty("uploadId").GetString()!), VoiceTranscript: "Elevated voice transcript"), Wire.Secret());
            var secretId = elevated.Content.Voice!.AudioId;
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.GetAsync("/api/v1/audio/" + secretId)).StatusCode);
            var challenge = svc.AudioChallenge(phone, elevated.Id);
            using var payload = JsonDocument.Parse(Convert.FromBase64String(challenge.Payload)); Assert.Equal("alxprgs.notify.audio.v1", payload.RootElement.GetProperty("protocol").GetString()); Assert.Equal(secretId, payload.RootElement.GetProperty("audioId").GetString());
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.GrantAudio(other, elevated.Id, new AudioGrantInput(challenge.Id, Convert.ToBase64String(otherKey.SignData(Convert.FromBase64String(challenge.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))))).Status);
            var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(challenge.Payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            using var granted = JsonDocument.Parse(Wire.Encode(svc.GrantAudio(phone, elevated.Id, new AudioGrantInput(challenge.Id, signature)))); var grant = granted.RootElement.GetProperty("grant").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audio/" + secretId); request.Headers.Add("X-Notify-Audio-Grant", grant);
            using var grantedBytes = await phoneClient.SendAsync(request); grantedBytes.EnsureSuccessStatusCode(); Assert.Equal(elevatedWav, await grantedBytes.Content.ReadAsByteArrayAsync());
            using var replay = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audio/" + secretId); replay.Headers.Add("X-Notify-Audio-Grant", grant);
            Assert.Equal(HttpStatusCode.Forbidden, (await phoneClient.SendAsync(replay)).StatusCode);
        }
    }

    [Fact]
    public async Task QuotaAbandonedUploadsExpiryAndOwnershipAreEnforced()
    {
        using var app = new AppFactory { AudioQuotaBytes = 1024 * 1024 }; var (source, sourceActor) = app.Integration(); var (phoneClient, phone, key) = app.Device(); using (source) using (phoneClient) using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var audio = app.Services.GetRequiredService<AudioService>(); var settings = app.Services.GetRequiredService<Settings>();
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadResponse(source, Wav(40000))).StatusCode);
            var upload = await Upload(source, Wav(1000)); var uploadId = upload.GetProperty("uploadId").GetString()!;
            app.Services.GetRequiredService<Database>().Run((c, t) => { var asset = Get<AudioAsset>(c, t, "audio", uploadId)!; Put(c, t, "audio", uploadId, asset with { ExpiresAt = Wire.Now - 1 }); return 0; });
            svc.Cleanup(); Assert.Null(audio.Asset(uploadId)); Assert.False(File.Exists(AudioService.PartPath(settings, uploadId)));
            var standardUpload = await Upload(source, Wav(1000));
            var note = svc.Create(sourceActor, new CreateNotification("voice", "Expiring", "Short", ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(1.2), VoiceUpload: new VoiceInput(standardUpload.GetProperty("uploadId").GetString()!), VoiceTranscript: "Standard voice transcript"), Wire.Secret());
            var durableUpload = await Upload(source, Wav(1000));
            var durable = svc.Create(sourceActor, new CreateNotification("voice", "Durable", "Short", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), VoiceUpload: new VoiceInput(durableUpload.GetProperty("uploadId").GetString()!), VoiceTranscript: "Durable voice transcript"), Wire.Secret());
            Assert.Equal(403, Assert.Throws<Fault>(() => svc.AudioFile(new Actor("web", AppFactory.Owner, []), durable.Content.Voice!.AudioId, null)).Status);
            await Task.Delay(1400); svc.Cleanup();
            Assert.Null(audio.Asset(note.Content.Voice!.AudioId)); Assert.Equal(404, Assert.Throws<Fault>(() => svc.AudioFile(phone, note.Content.Voice.AudioId, null)).Status);
        }
    }

    [Fact]
    public async Task VoiceMessageNeedsRegisteredAgentAndCannotReuseUpload()
    {
        using var app = new AppFactory(); var (sourceClient, source) = app.Integration(); var (phoneClient, phone, key) = app.Device(); using (sourceClient) using (phoneClient) using (key)
        {
            var svc = app.Services.GetRequiredService<NotificationService>(); var target = svc.Create(source, new CreateNotification("message", "No agent", "Nothing"), Wire.Secret());
            var upload = await Upload(phoneClient, Wav(1000)); var uploadId = upload.GetProperty("uploadId").GetString()!;
            Assert.Equal(HttpStatusCode.Conflict, (await phoneClient.PostAsJsonAsync("/api/v1/voice-messages", new { uploadId, notificationId = target.Id })).StatusCode);
            RegisterAgent(app, source.Id);
            var sent = await phoneClient.PostAsJsonAsync("/api/v1/voice-messages", new { uploadId, notificationId = target.Id }); sent.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Gone, (await phoneClient.PostAsJsonAsync("/api/v1/voice-messages", new { uploadId, notificationId = target.Id })).StatusCode);
        }
    }
}

