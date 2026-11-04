using Microsoft.Extensions.DependencyInjection;
using Notify;
using Xunit;
using static Notify.Database;

namespace NotifySpecs;

// Cleanup is the only place that deletes durable history; the boundary is part of contract v1.
public sealed class CleanupTests
{
    private static Notification Insert(AppFactory app, string sourceId, string kind, long created, long? expires, string status)
    {
        var content = new CreateNotification(kind, "Synthetic boundary", "No external action", "standard", expires is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(expires.Value));
        var n = new Notification(Wire.Id(), sourceId, content, created, status);
        app.Services.GetRequiredService<Database>().Run((c, t) =>
        {
            Exec(c, t, "INSERT INTO notifications VALUES($i,$s,$c,$e,$st,$d)", ("$i", n.Id), ("$s", sourceId), ("$c", created), ("$e", expires), ("$st", status), ("$d", Wire.Encode(n)));
            Exec(c, t, "INSERT INTO changes(notification_id,at) VALUES($i,$n)", ("$i", n.Id), ("$n", created));
            return 0;
        });
        return n;
    }
    private static bool Exists(AppFactory app, string id) => app.Services.GetRequiredService<Database>().Run((c, t) => Rows(c, t, "SELECT id FROM notifications WHERE id=$i", ("$i", id)).Count == 1);
    private static string Status(AppFactory app, string id) => app.Services.GetRequiredService<Database>().Run((c, t) => Rows(c, t, "SELECT status FROM notifications WHERE id=$i", ("$i", id)).Single());
    private static long Count(AppFactory app, string sql) => app.Services.GetRequiredService<Database>().Run((c, t) => long.Parse(Rows(c, t, sql)[0], System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void CleanupKeepsThirtyDayBoundaryAndDecisionGrace()
    {
        using var app = new AppFactory(); var now = Wire.Now;
        var oldMessage = Insert(app, "_test", "message", now - 31L * 86400000, null, "active");
        var freshMessage = Insert(app, "_test", "message", now - 29L * 86400000, null, "active");
        // A confirmation or form keeps one extra day after its own deadline so the answer/expiry reaches the sender.
        var nearDeadline = Insert(app, "_test", "confirmation", now - 31L * 86400000, now - 3600000, "expired");
        var formNearDeadline = Insert(app, "_test", "form", now - 31L * 86400000, now - 3600000, "answered");
        var pastGrace = Insert(app, "_test", "confirmation", now - 35L * 86400000, now - 2L * 86400000, "answered");
        var formPastGrace = Insert(app, "_test", "form", now - 35L * 86400000, now - 2L * 86400000, "answered");
        // The delivery loop sweeps pending requests before cleaning; a pending request inside the decision grace survives.
        var pendingWithinGrace = Insert(app, "_test", "confirmation", now - 31L * 86400000, now - 3600000, "pending");
        var pendingPastGrace = Insert(app, "_test", "confirmation", now - 31L * 86400000, now - 2L * 86400000, "pending");
        // A pending delivery must not be deleted before its bounded retry policy ends.
        var withJob = Insert(app, "_test", "message", now - 31L * 86400000, null, "active");
        app.Services.GetRequiredService<Database>().Run((c, t) =>
        {
            Exec(c, t, "INSERT INTO jobs VALUES($i,'webhook',$n,'_test','{}',0,$d,0,'pending')", ("$i", Wire.Id()), ("$n", withJob.Id), ("$d", now));
            Exec(c, t, "INSERT INTO changes(notification_id,at) VALUES($i,$n)", ("$i", "old-cursor-row"), ("$n", now - 40L * 86400000));
            return 0;
        });
        var svc = app.Services.GetRequiredService<NotificationService>();
        svc.Sweep(); svc.Cleanup();
        Assert.False(Exists(app, oldMessage.Id)); Assert.True(Exists(app, freshMessage.Id));
        Assert.True(Exists(app, nearDeadline.Id)); Assert.True(Exists(app, formNearDeadline.Id)); Assert.False(Exists(app, pastGrace.Id)); Assert.False(Exists(app, formPastGrace.Id));
        Assert.True(Exists(app, pendingWithinGrace.Id)); Assert.Equal("expired", Status(app, pendingWithinGrace.Id)); Assert.False(Exists(app, pendingPastGrace.Id));
        // The push/webhook job is bounded to 24h of retries, so the old notification and its job leave together.
        Assert.False(Exists(app, withJob.Id)); Assert.Equal(0, Count(app, "SELECT CAST(count(*) AS TEXT) FROM jobs WHERE notification_id='" + withJob.Id + "'"));
        Assert.Equal(0, Count(app, "SELECT CAST(count(*) AS TEXT) FROM changes WHERE notification_id='old-cursor-row'"));
        Assert.Equal(1, Count(app, "SELECT CAST(count(*) AS TEXT) FROM changes WHERE notification_id='" + pastGrace.Id + "'"));
        Assert.Equal(0, Count(app, "SELECT CAST(count(*) AS TEXT) FROM objects WHERE kind='delivery' AND json_extract(data,'$.notificationId')='" + pastGrace.Id + "'"));
    }
    [Fact]
    public void RecentDeliveryAndAnswerSurviveCleanup()
    {
        using var app = new AppFactory(); var now = Wire.Now;
        var n = Insert(app, "_test", "confirmation", now - 1000, now + 3600000, "answered");
        app.Services.GetRequiredService<Database>().Run((c, t) => { Put(c, t, "delivery", n.Id + ":device", new Delivery(n.Id, "device", "opened", now)); return 0; });
        app.Services.GetRequiredService<NotificationService>().Cleanup();
        Assert.True(Exists(app, n.Id)); Assert.Equal(1, Count(app, "SELECT CAST(count(*) AS TEXT) FROM objects WHERE kind='delivery'"));
    }
}
