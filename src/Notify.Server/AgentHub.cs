using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Notify.Database;

namespace Notify;

public sealed class AgentProtocolException(string message) : Exception(message);

// A single-process registry for outbound agent WebSocket sessions. Audio/call media never enters this queue.
public sealed class AgentHub(Database db, Settings settings, ILogger<AgentHub> log)
{
    public const string Protocol = "alxprgs.notify.agent.v1";
    public static readonly string[] KnownCapabilities = ["text_tasks", "voice_messages", "realtime_audio"];
    private const int MaxFrameBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, Connection> connections = new();
    public Func<Agent, JsonElement, Task>? ControlEvent { get; set; }
    public Func<Agent, Task>? DisconnectedEvent { get; set; }

    private sealed class Connection(Agent agent, WebSocket socket)
    {
        public Agent Agent { get; } = agent;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource Closing { get; } = new();
        public AgentSession[] Sessions { get; set; } = [];
        public string[] Capabilities { get; set; } = agent.Capabilities;
        public string[] CallProtocols { get; set; } = agent.CallProtocols ?? [];
        public string? CallProtocol { get; set; } = agent.CallProtocol;
        public long LastSeen { get; set; } = Wire.Now;
    }

    public bool IsOnline(string agentId) => connections.ContainsKey(agentId);
    public (Agent Agent, string[] Capabilities, AgentSession[] Sessions, string[] CallProtocols, string? CallProtocol)? Online(string agentId) =>
        connections.TryGetValue(agentId, out var connection) ? (connection.Agent, connection.Capabilities, connection.Sessions, connection.CallProtocols, connection.CallProtocol) : null;
    public async Task<bool> SendControlAsync(string agentId, string type, object payload, CancellationToken ct = default)
    {
        if (!connections.TryGetValue(agentId, out var connection)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await SendJsonAsync(connection, new { eventId = Wire.Id(), protocolVersion = Protocol, type, serverTime = Wire.Now, payload }, timeout.Token).WaitAsync(timeout.Token);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or AgentProtocolException or TimeoutException)
        {
            try { connection.Closing.Cancel(); } catch { }
            log.LogInformation("Agent control send failed for {AgentId}: {Type}", agentId, e.GetType().Name); return false;
        }
    }
    public Agent? ForSource(string sourceId) => db.Run((c, t) => All<Agent>(c, t, "agent").FirstOrDefault(a => a.SourceId == sourceId && !a.Revoked));
    public Agent? Get(string agentId) => db.Run((c, t) => Get<Agent>(c, t, "agent", agentId));
    public List<object> List(Actor actor)
    {
        actor.RequireOwner();
        return db.Run((c, t) => All<Agent>(c, t, "agent").Select(agent =>
        {
            var live = !agent.Revoked && connections.TryGetValue(agent.Id, out var found) ? found : null;
            return (object)new { agent.Id, agent.SourceId, agent.Name, agent.Capabilities, CallProtocols = live?.CallProtocols ?? agent.CallProtocols ?? [], CallProtocol = live?.CallProtocol ?? agent.CallProtocol, agent.Revoked, online = live != null, lastSeenAt = agent.LastSeenAt, sessions = live?.Sessions ?? Array.Empty<AgentSession>() };
        }).ToList());
    }

    public async Task<string> EnqueueAsync(Agent agent, string type, object payload, long ttlMs, string? correlationId = null, string? sessionId = null, CancellationToken ct = default)
    {
        var commandId = Wire.Id();
        var expires = Wire.Now + Math.Clamp(ttlMs, 1000, 30L * 86400000);
        db.Run((c, t) => { Enqueue(c, t, agent, type, payload, expires, correlationId, sessionId, commandId); return 0; });
        if (IsOnline(agent.Id))
        {
            try { await SendPendingAsync(agent.Id, ct); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { log.LogInformation("Agent {AgentId} command push deferred: {Type}", agent.Id, e.GetType().Name); }
        }
        return commandId;
    }
    public static void Enqueue(SqliteConnection c, SqliteTransaction t, Agent agent, string type, object payload, long expiresAt, string? correlationId = null, string? sessionId = null, string? id = null)
    {
        if (!Id(type)) throw new Fault(400, "invalid_agent_command");
        if (correlationId?.Length > 128 || correlationId?.Any(char.IsControl) == true || sessionId?.Length > 64 || sessionId?.Any(char.IsControl) == true) throw new Fault(400, "invalid_agent_command");
        Exec(c, t, "INSERT INTO agent_commands(id,agent_id,source_id,type,payload,created,expires,status,attempts,correlation_id,session_id) VALUES($i,$a,$s,$t,$p,$c,$e,'queued',0,$co,$se)",
            ("$i", id ?? Wire.Id()), ("$a", agent.Id), ("$s", agent.SourceId), ("$t", type), ("$p", Wire.Encode(payload)), ("$c", Wire.Now), ("$e", expiresAt), ("$co", correlationId), ("$se", sessionId));
    }
    public async Task PushAsync(string agentId, CancellationToken ct = default)
    {
        if (!IsOnline(agentId)) return;
        try { await SendPendingAsync(agentId, ct); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { log.LogInformation("Agent {AgentId} command push deferred: {Type}", agentId, e.GetType().Name); }
    }

    public void Disconnect(string agentId, string reason)
    {
        if (connections.TryGetValue(agentId, out var connection)) { log.LogInformation("Closing agent {AgentId}: {Reason}", agentId, reason); connection.Closing.Cancel(); }
    }
    public void DisconnectSource(string sourceId)
    {
        foreach (var pair in connections) if (pair.Value.Agent.SourceId == sourceId) pair.Value.Closing.Cancel();
    }
    public void MarkRevoked(string sourceId)
    {
        db.Run((c, t) =>
        {
            foreach (var agent in All<Agent>(c, t, "agent").Where(a => a.SourceId == sourceId && !a.Revoked))
            {
                Put(c, t, "agent", agent.Id, agent with { Revoked = true });
                if (connections.TryGetValue(agent.Id, out var connection)) connection.Closing.Cancel();
            }
            return 0;
        });
    }
    public void Sweep()
    {
        db.Run((c, t) =>
        {
            Exec(c, t, "UPDATE agent_commands SET status='expired' WHERE status IN ('queued','sent') AND expires<=$n", ("$n", Wire.Now));
            Exec(c, t, "DELETE FROM agent_commands WHERE created<$n", ("$n", Wire.Now - 30L * 86400000));
            return 0;
        });
    }

    public async Task Run(Agent agent, WebSocket socket, CancellationToken ct)
    {
        var connection = new Connection(agent, socket);
        var previous = connections.AddOrUpdate(agent.Id, connection, (_, existing) => { existing.Closing.Cancel(); return connection; });
        if (!ReferenceEquals(previous, connection)) previous.Closing.Cancel();
        WebSocketCloseStatus? closeStatus = null; string? closeReason = null;
        try
        {
            db.Run((c, t) => { Put(c, t, "agent", agent.Id, agent with { LastSeenAt = Wire.Now }); Exec(c, t, "UPDATE agent_commands SET status='queued' WHERE agent_id=$a AND status='sent'", ("$a", agent.Id)); return 0; });
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closing.Token);
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = await ReceiveFrameAsync(connection, helloTimeout.Token);
            ApplyHello(connection, hello);
            await SendJsonAsync(connection, new
            {
                eventId = Wire.Id(), protocolVersion = Protocol, type = "hello_ack", serverTime = Wire.Now,
                heartbeatSeconds = settings.AgentHeartbeatSeconds, offlineSeconds = settings.AgentOfflineSeconds,
                agent = new { agent.Id, agent.Name }, callProtocol = connection.CallProtocol,
                supportedCallProtocols = CallService.SupportedProtocols, pendingCommands = CountPending(agent.Id)
            }, lifetime.Token);
            await SendPendingAsync(connection, lifetime.Token);
            await ReadLoopAsync(connection, lifetime.Token);
        }
        catch (AgentProtocolException e)
        {
            closeStatus = socket.CloseStatus ?? WebSocketCloseStatus.PolicyViolation;
            closeReason = socket.CloseStatusDescription ?? e.Message;
        }
        catch (OperationCanceledException e)
        {
            log.LogInformation("Agent {AgentId} session canceled: {Type}", agent.Id, e.GetType().Name);
        }
        catch (WebSocketException e) { log.LogInformation("Agent {AgentId} socket closed: {Status}", agent.Id, e.WebSocketErrorCode); }
        finally
        {
            connection.Closing.Cancel();
            // Compare and remove atomically: a replacement must survive this connection's cleanup.
            connections.TryRemove(new KeyValuePair<string, Connection>(agent.Id, connection));
            db.Run((c, t) => { var stored = Get<Agent>(c, t, "agent", agent.Id); if (stored != null) Put(c, t, "agent", stored.Id, stored with { LastSeenAt = Wire.Now }); return 0; });
            if (DisconnectedEvent is { } disconnected) { try { await disconnected(agent); } catch { } }
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(closeStatus ?? WebSocketCloseStatus.NormalClosure, closeReason is { Length: > 0 } ? closeReason[..Math.Min(100, closeReason.Length)] : "closed", closeTimeout.Token);
            }
            catch { try { socket.Abort(); } catch { } }
            socket.Dispose();
        }
    }

    private void ApplyHello(Connection connection, JsonElement hello)
    {
        if (Text(hello, "type", 32) != "hello") throw new AgentProtocolException("hello_required");
        if (Text(hello, "protocolVersion", 64) != Protocol) throw new AgentProtocolException("unsupported_protocol");
        if (Text(hello, "agentId", 64) != connection.Agent.Id) throw new AgentProtocolException("agent_mismatch");
        _ = Text(hello, "eventId", 64);
        var name = Text(hello, "name", 80);
        if (name.Any(char.IsControl)) throw new AgentProtocolException("invalid_hello");
        var capabilities = Strings(hello, "capabilities");
        if (capabilities.Length > KnownCapabilities.Length || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length || capabilities.Any(c => !KnownCapabilities.Contains(c, StringComparer.Ordinal))) throw new AgentProtocolException("invalid_capabilities");
        var callProtocols = OptionalStrings(hello, "callProtocols", 8);
        var sessions = Sessions(hello);
        connection.Capabilities = capabilities; connection.Sessions = sessions; connection.CallProtocols = callProtocols;
        connection.CallProtocol = callProtocols.FirstOrDefault(protocol => CallService.SupportedProtocols.Contains(protocol, StringComparer.Ordinal));
        db.Run((c, t) => { var stored = Get<Agent>(c, t, "agent", connection.Agent.Id); if (stored != null) Put(c, t, "agent", stored.Id, stored with { Name = name, Capabilities = capabilities, CallProtocols = callProtocols, CallProtocol = connection.CallProtocol, LastSeenAt = Wire.Now }); return 0; });
    }

    private async Task ReadLoopAsync(Connection connection, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closing.Token);
        var receive = ReceiveFrameAsync(connection, linked.Token);
        while (!linked.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            var delay = Task.Delay(1000, linked.Token);
            if (await Task.WhenAny(receive, delay) != receive)
            {
                if (Wire.Now - connection.LastSeen > settings.AgentOfflineSeconds * 1000L) throw new AgentProtocolException("heartbeat_timeout");
                continue;
            }
            var frame = await receive; connection.LastSeen = Wire.Now;
            switch (Text(frame, "type", 32))
            {
                case "heartbeat":
                    if (frame.TryGetProperty("sessions", out _)) connection.Sessions = Sessions(frame);
                    await SendJsonAsync(connection, new { eventId = Wire.Id(), protocolVersion = Protocol, type = "heartbeat_ack", serverTime = Wire.Now }, linked.Token);
                    break;
                case "sessions":
                    connection.Sessions = Sessions(frame);
                    break;
                case "ack":
                    Ack(connection, Text(frame, "commandEventId", 64));
                    break;
                case "call.dial" or "call.accept" or "call.reject" or "call.end" or "call.interrupt" or "call.say":
                    if (ControlEvent is { } control) await control(connection.Agent, frame);
                    break;
                default:
                    throw new AgentProtocolException("unsupported_event");
            }
            receive = ReceiveFrameAsync(connection, linked.Token);
        }
    }

    private async Task SendPendingAsync(string agentId, CancellationToken ct)
    {
        if (!connections.TryGetValue(agentId, out var connection)) return;
        await SendPendingAsync(connection, ct);
    }
    private async Task SendPendingAsync(Connection connection, CancellationToken ct)
    {
        var agentId = connection.Agent.Id;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closing.Token);
        var ids = db.Run((c, t) => Rows(c, t, "SELECT id FROM agent_commands WHERE agent_id=$a AND status IN ('queued','sent') AND expires>$n ORDER BY created,id LIMIT 50", ("$a", agentId), ("$n", Wire.Now)));
        foreach (var id in ids)
        {
            var command = db.Run((c, t) => CommandRow(c, t, id));
            if (command == null || command.AgentId != agentId) continue;
            if (command.ExpiresAt <= Wire.Now) { db.Run((c, t) => Exec(c, t, "UPDATE agent_commands SET status='expired' WHERE id=$i", ("$i", id))); continue; }
            using var payload = JsonDocument.Parse(command.Payload);
            await SendJsonAsync(connection, new
            {
                eventId = command.Id, protocolVersion = Protocol, type = "command", command = command.Type,
                correlationId = command.CorrelationId, sessionId = command.SessionId,
                createdAt = command.CreatedAt, expiresAt = command.ExpiresAt, payload = payload.RootElement
            }, lifetime.Token);
            db.Run((c, t) => Exec(c, t, "UPDATE agent_commands SET status='sent',attempts=attempts+1 WHERE id=$i AND status IN ('queued','sent')", ("$i", id)));
        }
    }
    private void Ack(Connection connection, string commandId)
    {
        if (commandId.Length != 32) return;
        db.Run((c, t) => Exec(c, t, "UPDATE agent_commands SET status='acked',acked=$n WHERE id=$i AND agent_id=$a AND status!='acked'", ("$n", Wire.Now), ("$i", commandId), ("$a", connection.Agent.Id)));
    }
    private long CountPending(string agentId) => db.Run((c, t) => long.Parse(Rows(c, t, "SELECT CAST(count(*) AS TEXT) FROM agent_commands WHERE agent_id=$a AND status IN ('queued','sent') AND expires>$n", ("$a", agentId), ("$n", Wire.Now))[0], System.Globalization.CultureInfo.InvariantCulture));

    private static AgentCommand? CommandRow(SqliteConnection c, SqliteTransaction t, string id)
    {
        using var cmd = Cmd(c, t, "SELECT id,agent_id,source_id,type,payload,created,expires,status,attempts,acked,correlation_id,session_id FROM agent_commands WHERE id=$i", ("$i", id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new AgentCommand(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));
    }
    private static async Task<JsonElement> ReceiveFrameAsync(Connection connection, CancellationToken ct)
    {
        var buffer = new byte[MaxFrameBytes + 1];
        var result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.MessageType == WebSocketMessageType.Close) throw new AgentProtocolException("client_closed");
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage || result.Count > MaxFrameBytes) throw new AgentProtocolException("invalid_frame");
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }
    private async Task SendJsonAsync(Connection connection, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(Wire.Encode(payload));
        if (bytes.Length > MaxFrameBytes) throw new AgentProtocolException("frame_too_large");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closing.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.AgentSendTimeoutSeconds));
        try { await connection.SendLock.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { connection.Closing.Cancel(); } catch { } throw; }
        try
        {
            try { await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { connection.Closing.Cancel(); } catch { } throw; }
        }
        finally { connection.SendLock.Release(); }
    }
    private static string Text(JsonElement frame, string name, int max)
    {
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new AgentProtocolException("invalid_field_" + name);
        var text = value.GetString()!;
        if (text.Length is < 1 || text.Length > max) throw new AgentProtocolException("invalid_field_" + name);
        return text;
    }
    private static string[] Strings(JsonElement frame, string name)
    {
        if (!frame.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) throw new AgentProtocolException("invalid_field_" + name);
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: >= 1 and <= 64 } text ? text : throw new AgentProtocolException("invalid_field_" + name)).ToArray();
    }
    private static string[] OptionalStrings(JsonElement frame, string name, int max)
    {
        if (!frame.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max) throw new AgentProtocolException("invalid_field_" + name);
        var values = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: >= 1 and <= 64 } text ? text : throw new AgentProtocolException("invalid_field_" + name)).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new AgentProtocolException("invalid_field_" + name);
        return values;
    }
    private static AgentSession[] Sessions(JsonElement frame)
    {
        if (!frame.TryGetProperty("sessions", out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new AgentProtocolException("invalid_sessions");
        if (value.GetArrayLength() > 50) throw new AgentProtocolException("too_many_sessions");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object) throw new AgentProtocolException("invalid_sessions");
            var id = Text(item, "id", 64); var label = Text(item, "name", 160);
            if (label.Any(char.IsControl) || !seen.Add(id)) throw new AgentProtocolException("invalid_sessions");
            return new AgentSession(id, label);
        }).ToArray();
    }
    private static bool Id(string value) => value.Length is >= 1 and <= 64 && char.IsAsciiLetterOrDigit(value[0]) && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
}
