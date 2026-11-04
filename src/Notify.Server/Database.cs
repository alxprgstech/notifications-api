using Microsoft.Data.Sqlite;

namespace Notify;

public sealed class Database
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string FileName { get; }
    public string SqliteVersion { get; private set; } = "";
    public Database(Settings settings)
    {
        Directory.CreateDirectory(settings.DataPath);
        FileName = Path.GetFullPath(Path.Combine(settings.DataPath, "notify.db"));
        connectionString = new SqliteConnectionStringBuilder { DataSource = FileName, Pooling = true, DefaultTimeout = 5 }.ToString();
        Run((c, t) =>
        {
            SqliteVersion = Rows(c, t, "SELECT sqlite_version()")[0];
            if (Version.Parse(SqliteVersion) < new Version(3, 51, 3)) throw new InvalidOperationException("SQLite >=3.51.3 required for WAL-reset fix");
            Exec(c, t, """
            CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS objects(kind TEXT NOT NULL,id TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS credentials(hash TEXT PRIMARY KEY,kind TEXT NOT NULL,id TEXT NOT NULL,expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS changes(seq INTEGER PRIMARY KEY AUTOINCREMENT,notification_id TEXT NOT NULL,at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS notifications(id TEXT PRIMARY KEY,source_id TEXT NOT NULL,created INTEGER NOT NULL,expires INTEGER,status TEXT NOT NULL,data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_notifications_source ON notifications(source_id,created,id);
            CREATE INDEX IF NOT EXISTS ix_notifications_created ON notifications(created,id);
            CREATE INDEX IF NOT EXISTS ix_notifications_expiry ON notifications(status,expires);
            CREATE TABLE IF NOT EXISTS idempotency(source_id TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,notification_id TEXT NOT NULL,PRIMARY KEY(source_id,key));
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,kind TEXT NOT NULL,notification_id TEXT NOT NULL,target_id TEXT NOT NULL,body TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,due INTEGER NOT NULL,lease INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'pending');
            CREATE INDEX IF NOT EXISTS ix_jobs_due ON jobs(status,due,lease);
            CREATE TABLE IF NOT EXISTS agent_commands(id TEXT PRIMARY KEY,agent_id TEXT NOT NULL,source_id TEXT NOT NULL,type TEXT NOT NULL,payload TEXT NOT NULL,created INTEGER NOT NULL,expires INTEGER NOT NULL,status TEXT NOT NULL DEFAULT 'queued',attempts INTEGER NOT NULL DEFAULT 0,acked INTEGER,correlation_id TEXT,session_id TEXT);
            CREATE INDEX IF NOT EXISTS ix_agent_commands_pending ON agent_commands(agent_id,status,created);
            INSERT OR IGNORE INTO schema_migrations VALUES(1,unixepoch()*1000);
            INSERT OR IGNORE INTO schema_migrations VALUES(2,unixepoch()*1000);
            INSERT OR IGNORE INTO schema_migrations VALUES(3,unixepoch()*1000);
            """);
            var agentColumns = Rows(c, t, "SELECT name FROM pragma_table_info('agent_commands')");
            if (!agentColumns.Contains("correlation_id")) Exec(c, t, "ALTER TABLE agent_commands ADD COLUMN correlation_id TEXT");
            if (!agentColumns.Contains("session_id")) Exec(c, t, "ALTER TABLE agent_commands ADD COLUMN session_id TEXT");
            var owner = Get<string>(c, t, "meta", "owner");
            if (owner != null && owner != settings.OwnerSub) throw new InvalidOperationException("Database owner does not match configured SSO subject");
            if (owner == null && !string.IsNullOrEmpty(settings.OwnerSub))
            {
                if (Rows(c, t, "SELECT hash FROM credentials LIMIT 1").Count != 0) throw new InvalidOperationException("Cannot adopt an unbound database containing credentials");
                Put(c, t, "meta", "owner", settings.OwnerSub);
            }
            return 0;
        }, wal: true);
    }
    public T Run<T>(Func<SqliteConnection, SqliteTransaction, T> action, bool wal = false)
    {
        gate.Wait();
        try
        {
            using var c = new SqliteConnection(connectionString); c.Open();
            if (wal) { using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;"; cmd.ExecuteNonQuery(); }
            using var tx = c.BeginTransaction(deferred: false);
            var result = action(c, tx); tx.Commit(); return result;
        }
        finally { gate.Release(); }
    }
    public void Backup(string destination)
    {
        gate.Wait();
        try { using var source = new SqliteConnection(connectionString); source.Open(); using var target = new SqliteConnection($"Data Source={Path.GetFullPath(destination)}"); target.Open(); source.BackupDatabase(target); }
        finally { gate.Release(); }
    }
    public static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction t, string sql, params (string, object?)[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public static int Exec(SqliteConnection c, SqliteTransaction t, string sql, params (string, object?)[] values) { using var cmd = Cmd(c, t, sql, values); return cmd.ExecuteNonQuery(); }
    public static List<string> Rows(SqliteConnection c, SqliteTransaction t, string sql, params (string, object?)[] values)
    {
        using var cmd = Cmd(c, t, sql, values); using var reader = cmd.ExecuteReader(); var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0)); return rows;
    }
    public static T? Get<T>(SqliteConnection c, SqliteTransaction t, string kind, string id)
    {
        var rows = Rows(c, t, "SELECT data FROM objects WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id));
        return rows.Count == 0 ? default : Wire.Decode<T>(rows[0]);
    }
    public static List<T> All<T>(SqliteConnection c, SqliteTransaction t, string kind) => Rows(c, t, "SELECT data FROM objects WHERE kind=$k ORDER BY id", ("$k", kind)).Select(Wire.Decode<T>).ToList();
    public static void Put<T>(SqliteConnection c, SqliteTransaction t, string kind, string id, T value) => Exec(c, t, "INSERT INTO objects VALUES($k,$i,$d) ON CONFLICT(kind,id) DO UPDATE SET data=excluded.data", ("$k", kind), ("$i", id), ("$d", Wire.Encode(value)));
}
