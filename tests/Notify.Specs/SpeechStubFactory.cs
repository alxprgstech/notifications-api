using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Notify;

namespace NotifySpecs;

/// <summary>Deterministic server-side speech fixture: no model, network, or user audio.</summary>
public sealed class SpeechStubFactory : IHttpClientFactory
{
    public List<string> Synthesized { get; } = [];
    public string RecognizedText { get; set; } = "Проверь сборку";
    public int SynthesizedDurationMs { get; set; } = 120;

    public HttpClient CreateClient(string name) => name switch
    {
        "silero" => new HttpClient(new StubHandler(this, silero: true), disposeHandler: false) { BaseAddress = new Uri("http://silero:8080/") },
        "stt" => new HttpClient(new StubHandler(this, silero: false), disposeHandler: false) { BaseAddress = new Uri("http://stt:8080/") },
        _ => throw new InvalidOperationException("Unexpected HTTP client: " + name),
    };

    private sealed class StubHandler(SpeechStubFactory owner, bool silero) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (silero && path == "/synthesize" && request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var text = document.RootElement.GetProperty("text").GetString() ?? "";
                lock (owner.Synthesized) owner.Synthesized.Add(text);
                var pcm = new byte[owner.SynthesizedDurationMs * 32];
                for (var i = 0; i + 1 < pcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), 1000);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav(pcm)) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                return response;
            }
            if (!silero && path == "/recognize" && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                if (body.Length < 44) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { text = owner.RecognizedText }), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static byte[] Wav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }
}
