using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

public sealed class NativeCodexContractFactAttribute : FactAttribute
{
    public NativeCodexContractFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOTIFY_CODEX_CONTRACT_PEER")))
            Skip = "Set NOTIFY_CODEX_CONTRACT_PEER to the isolated Codex connector test fixture.";
    }
}

public sealed class NativeHarnessContractFactAttribute : FactAttribute
{
    public NativeHarnessContractFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOTIFY_HARNESS_CONTRACT_PEER")))
            Skip = "Set NOTIFY_HARNESS_CONTRACT_PEER to the isolated Harness connector test fixture.";
    }
}

public sealed partial class CallTests
{
    [NativeCodexContractFact]
    public Task NativeCodexConnectorAnswersTrustedSipThroughTheServerProtocol() =>
        RunNativeConnectorContract("NOTIFY_CODEX_CONTRACT_PEER", "8260", "Ответ нативного помощника");

    [NativeHarnessContractFact]
    public Task NativeHarnessConnectorAnswersTrustedSipThroughTheServerProtocol() =>
        RunNativeConnectorContract("NOTIFY_HARNESS_CONTRACT_PEER", "8261", "Ответ выделенного чата Harness");

    private async Task RunNativeConnectorContract(string peerVariable, string extension, string expectedReply)
    {
        var peerPath = Environment.GetEnvironmentVariable(peerVariable)!;
        Assert.True(Path.IsPathFullyQualified(peerPath) && File.Exists(peerPath));
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bridgeId = Wire.Id();
        var peerId = Wire.Id();
        var otherId = Wire.Id();
        var otherExtension = extension == "8260" ? "8261" : "8260";
        using var app = new AppFactory
        {
            UseSpeechStub = true,
            AgentHeartbeatSeconds = 1,
            AgentOfflineSeconds = 60,
            PhoneAssistantBridgeSourceId = bridgeId,
            PhoneAssistantRoutes = [new(extension, peerId), new(otherExtension, otherId)]
        };
        using var bridgeHttp = app.Integration(bridgeId, ["telephony:bridge"]).Item1;
        var (peerHttp, peerSource) = app.Integration(scopes: ["agents:connect"]);
        var (otherHttp, otherSource) = app.Integration(scopes: ["agents:connect"]);
        using var peerAuth = peerHttp;
        using var otherAuth = otherHttp;
        var otherAgent = new Agent(otherId, otherSource.Id, "Other assistant fixture", ["realtime_audio"], Wire.Now);
        app.Services.GetRequiredService<Database>().Run((c, t) =>
        {
            Put(c, t, "agent", peerId, new Agent(peerId, peerSource.Id, "Native assistant fixture", ["realtime_audio"], Wire.Now));
            Put(c, t, "agent", otherId, otherAgent);
            return 0;
        });
        using var bridge = await Ws(app, Token(bridgeHttp)).ConnectAsync(
            new Uri("ws://localhost/api/v1/telephony/connect"), time.Token);
        using var peerSocket = await Ws(app, Token(peerAuth)).ConnectAsync(
            new Uri("ws://localhost/api/v1/agents/connect"), time.Token);
        using var otherSocket = await ConnectAgentSessions(app, Token(otherAuth), otherAgent, ["notify-phone-assistant"]);
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(peerPath);
        using var peer = Process.Start(start) ?? throw new InvalidOperationException("Node test peer could not start");
        peer.StandardInput.AutoFlush = true;
        await peer.StandardInput.WriteLineAsync(Wire.Encode(new { agentId = peerId, sourceId = peerSource.Id }));
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toServer = RelayToServerAsync();
        var fromServer = RelayFromServerAsync();
        try
        {
            await heartbeat.Task.WaitAsync(time.Token);
            var route = await peerAuth.GetFromJsonAsync<PhoneAssistantRouteResponse>("/api/v1/telephony/assistant", time.Token);
            Assert.Equal(extension, route!.Extension);
            Assert.True(route.SessionReady);
            var otherRoute = await otherAuth.GetFromJsonAsync<PhoneAssistantRouteResponse>("/api/v1/telephony/assistant", time.Token);
            Assert.Equal(otherExtension, otherRoute!.Extension);
            Assert.True(otherRoute.SessionReady);
            var requestId = Wire.Id();
            await SendText(bridge, new { type = "incoming", requestId, extension });
            var invite = await ReceiveJson(bridge).WaitAsync(time.Token);
            Assert.Equal(requestId, invite.GetProperty("requestId").GetString());
            Assert.True(invite.GetProperty("ok").GetBoolean());
            var callId = invite.GetProperty("callId").GetString()!;
            var inviteId = invite.GetProperty("inviteId").GetString()!;
            using var media = await Ws(app, Token(bridgeHttp)).ConnectAsync(
                new Uri($"ws://localhost/api/v1/telephony/calls/{callId}/audio?inviteId={inviteId}"), time.Token);
            Assert.Equal("active", (await ReceiveJson(media).WaitAsync(time.Token)).GetProperty("status").GetString());
            while (true)
            {
                var (type, data) = await ReceiveAny(media, time.Token);
                Assert.NotEqual(WebSocketMessageType.Close, type);
                if (type != WebSocketMessageType.Text) continue;
                using var frame = JsonDocument.Parse(data);
                if (frame.RootElement.TryGetProperty("status", out var status) && status.GetString() == "idle") break;
            }

            for (uint index = 0; index < 60; index++)
                await SendBinary(media, Frame(index, index * 20, index < 20 ? (byte)0x33 : (byte)0));
            var gotReply = false;
            while (!gotReply)
            {
                var (type, data) = await ReceiveAny(media, time.Token);
                Assert.NotEqual(WebSocketMessageType.Close, type);
                if (type != WebSocketMessageType.Text) continue;
                using var frame = JsonDocument.Parse(data);
                gotReply = frame.RootElement.TryGetProperty("turn", out var turn) &&
                    turn.TryGetProperty("text", out var text) && text.GetString() == expectedReply;
            }

            await SendText(bridge, new { type = "ended", callId, reason = "ended" });
            await WaitStatus(peerAuth, callId, "ended");
            await fromServer.WaitAsync(time.Token);
            peer.StandardInput.Close();
            await toServer.WaitAsync(time.Token);
            await peerSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", time.Token);
            await peer.WaitForExitAsync(time.Token);
            Assert.Equal(0, peer.ExitCode);
            await WaitUntil(() => !app.Services.GetRequiredService<AgentHub>().IsOnline(peerId));
            Assert.True(app.Services.GetRequiredService<AgentHub>().IsOnline(otherId));
            await otherSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", time.Token);
            await bridge.CloseAsync(WebSocketCloseStatus.NormalClosure, "", time.Token);
        }
        finally
        {
            await time.CancelAsync();
            if (!peer.HasExited) peer.Kill(entireProcessTree: true);
            await peer.WaitForExitAsync();
            try { await Task.WhenAll(toServer, fromServer); }
            catch (OperationCanceledException) when (time.IsCancellationRequested) { }
        }

        async Task RelayToServerAsync()
        {
            while (await peer.StandardOutput.ReadLineAsync(time.Token) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                await peerSocket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, time.Token);
                if (message.RootElement.GetProperty("type").GetString() == "heartbeat") heartbeat.TrySetResult();
            }
        }

        async Task RelayFromServerAsync()
        {
            while (true)
            {
                var (type, bytes) = await ReceiveAny(peerSocket, time.Token);
                if (type == WebSocketMessageType.Close) return;
                Assert.Equal(WebSocketMessageType.Text, type);
                using var frame = JsonDocument.Parse(bytes);
                await peer.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(bytes));
                if (frame.RootElement.GetProperty("type").GetString() == "call.ended") return;
            }
        }
    }
}
