using System.Diagnostics;
using System.Text.Json;
using static Notify.Database;

namespace Notify;

public sealed record CallTraceEvent(int Sequence, string Stage, string Status, long At, double OffsetMs,
    string Source, string? TurnId = null, double? DurationMs = null, string? Code = null,
    long? Frames = null, long? ReceivedFrames = null, long? DroppedFrames = null, long? Tokens = null);
public sealed record CallTrace(string CallId, IReadOnlyList<CallTraceEvent> Events, bool Truncated, long UpdatedAt);

public sealed partial class CallService
{
    private const int MaxTraceEvents = 512;

    private void Trace(Call call, string stage, string status = "completed", string? turnId = null,
        long? started = null, string? code = null, string source = "api", double? durationMs = null,
        long? frames = null, long? receivedFrames = null, long? droppedFrames = null, long? tokens = null)
    {
        lock (call.TraceGate)
        {
            if (call.TraceClosed) return;
            if (call.TraceEvents.Count >= MaxTraceEvents - 1 && stage != "call_ended")
            {
                call.TraceTruncated = true;
                return;
            }
            var now = Stopwatch.GetTimestamp();
            call.TraceEvents.Add(new CallTraceEvent(call.TraceEvents.Count + 1, stage, status, Wire.Now,
                Stopwatch.GetElapsedTime(call.TraceStarted, now).TotalMilliseconds, source, turnId,
                started is { } start ? Stopwatch.GetElapsedTime(start, now).TotalMilliseconds : durationMs,
                code, frames, receivedFrames, droppedFrames, tokens));
            call.TraceClosed = stage == "call_ended";
        }
    }

    private static CallTrace TraceSnapshot(Call call)
    {
        lock (call.TraceGate)
            return new CallTrace(call.Id, call.TraceEvents.ToArray(), call.TraceTruncated, Wire.Now);
    }

    private void SaveTrace(Call call)
    {
        // Serialize writes for this call so a slower checkpoint cannot overwrite the final trace.
        lock (call.TraceGate)
        {
            try { db.Run((c, t) => { Put(c, t, "calltrace", call.Id, TraceSnapshot(call)); return 0; }); }
            catch (Exception error) { log.LogWarning("Call {CallId} trace write failed: {Type}", call.Id, error.GetType().Name); }
        }
    }

    private static void RequireHistoryAccess(Actor actor)
    {
        if (!actor.IsOwner) actor.RequireScope("agents:connect");
    }

    public CallTrace TraceFor(Actor actor, string id)
    {
        RequireHistoryAccess(actor);
        _ = State(actor, id);
        Call? call;
        lock (sync) calls.TryGetValue(id, out call);
        if (call is not null) return TraceSnapshot(call);
        return db.Run((c, t) => Get<CallTrace>(c, t, "calltrace", id)) ?? throw new Fault(404, "trace_not_recorded");
    }

    private void AgentTrace(Call call, JsonElement payload)
    {
        if (call.Mode != "conversation" || call.Conversation is not { } state ||
            !payload.TryGetProperty("sessionId", out _) ||
            !payload.TryGetProperty("turnId", out var turn) || turn.ValueKind != JsonValueKind.String ||
            turn.GetString() is not { Length: 32 } turnId ||
            !payload.TryGetProperty("stage", out var stageValue) || stageValue.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("status", out var statusValue) || statusValue.ValueKind != JsonValueKind.String) return;
        var (stage, status) = (stageValue.GetString(), statusValue.GetString());
        if (stage is not ("agent_processing" or "agent_first_delta") ||
            status is not ("started" or "completed" or "failed" or "cancelled" or "interrupted")) return;
        var duration = payload.TryGetProperty("durationMs", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number &&
            elapsed.TryGetDouble(out var durationMs) && durationMs is >= 0 and <= 600_000 ? durationMs : (double?)null;
        var used = payload.TryGetProperty("tokens", out var count) && count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt64(out var tokens) && tokens is >= 0 and <= 10_000_000 ? tokens : (long?)null;
        lock (state.Gate)
        {
            if (!state.Delivered.Contains(turnId)) return;
            Trace(call, stage, status, turnId, source: "agent", durationMs: duration, tokens: used);
        }
    }

    private void BridgeTrace(Call call, JsonElement payload)
    {
        if (!payload.TryGetProperty("stage", out var name) || name.ValueKind != JsonValueKind.String) return;
        var stage = name.GetString();
        if (stage is not ("sip_ringing" or "sip_answered" or "rtp_ready" or "rtp_first_in" or "rtp_first_out" or "rtp_frames")) return;
        var duration = payload.TryGetProperty("durationMs", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number &&
            elapsed.TryGetDouble(out var ms) && ms is >= 0 and <= 600_000 ? ms : (double?)null;
        string? turnId = null;
        if (stage == "rtp_first_out" && payload.TryGetProperty("turnId", out var turn) &&
            turn.ValueKind == JsonValueKind.String && turn.GetString() is { Length: 32 } id && id.All(char.IsAsciiHexDigitLower))
            turnId = id;
        Trace(call, stage, turnId: turnId, source: "asterisk", durationMs: duration,
            frames: Count("sent"), receivedFrames: Count("received"), droppedFrames: Count("dropped"));

        long? Count(string key) => payload.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt64(out var count) && count is >= 0 and <= 100_000_000 ? count : null;
    }
}
