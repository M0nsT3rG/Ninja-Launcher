using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Infrastructure.Persistence;

/// <summary>SQLite storage. Schema versions are tracked with PRAGMA user_version.</summary>
public sealed class NinjaDatabase
{
    private readonly string _connectionString;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public NinjaDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        Migrate();
    }

    internal SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version"));
        if (version < 1)
        {
            Exec(c, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS instances (id TEXT PRIMARY KEY, name TEXT NOT NULL, data TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS accounts (profile_id TEXT PRIMARY KEY, is_active INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, message TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_activity_ts ON activity(ts);
                PRAGMA user_version=1;
                """);
        }
    }

    internal static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteScalar();
    }

    internal static int Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    internal static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}

public sealed class SqliteInstanceRepository(NinjaDatabase db) : IInstanceRepository
{
    public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        using var cmd = NinjaDatabase.Command(c, "SELECT data FROM instances ORDER BY name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        var list = new List<GameInstance>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<GameInstance>(r.GetString(0), NinjaDatabase.Json)!);
        return (IReadOnlyList<GameInstance>)list;
    }, ct);

    public Task<GameInstance?> GetAsync(Guid id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        var data = NinjaDatabase.Scalar(c, "SELECT data FROM instances WHERE id=$id", ("$id", id.ToString("N"))) as string;
        return data is null ? null : JsonSerializer.Deserialize<GameInstance>(data, NinjaDatabase.Json);
    }, ct);

    public Task UpsertAsync(GameInstance instance, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        NinjaDatabase.Exec(c, "INSERT INTO instances(id,name,data) VALUES($id,$name,$data) ON CONFLICT(id) DO UPDATE SET name=$name, data=$data",
            ("$id", instance.Id.ToString("N")), ("$name", instance.Name), ("$data", JsonSerializer.Serialize(instance, NinjaDatabase.Json)));
    }, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        NinjaDatabase.Exec(c, "DELETE FROM instances WHERE id=$id", ("$id", id.ToString("N")));
    }, ct);
}

public sealed class SqliteAccountRepository(NinjaDatabase db) : IAccountRepository
{
    public Task<IReadOnlyList<MinecraftAccount>> ListAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        using var cmd = NinjaDatabase.Command(c, "SELECT data, is_active FROM accounts");
        using var r = cmd.ExecuteReader();
        var list = new List<MinecraftAccount>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<MinecraftAccount>(r.GetString(0), NinjaDatabase.Json)! with { IsActive = r.GetInt64(1) == 1 });
        return (IReadOnlyList<MinecraftAccount>)list.OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }, ct);

    public Task UpsertAsync(MinecraftAccount account, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        NinjaDatabase.Exec(c, "INSERT INTO accounts(profile_id,is_active,data) VALUES($id,$a,$data) ON CONFLICT(profile_id) DO UPDATE SET data=$data",
            ("$id", account.ProfileId), ("$a", account.IsActive ? 1 : 0), ("$data", JsonSerializer.Serialize(account, NinjaDatabase.Json)));
    }, ct);

    public Task DeleteAsync(string profileId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        NinjaDatabase.Exec(c, "DELETE FROM accounts WHERE profile_id=$id", ("$id", profileId));
    }, ct);

    public Task SetActiveAsync(string profileId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        NinjaDatabase.Exec(c, "UPDATE accounts SET is_active = CASE WHEN profile_id=$id THEN 1 ELSE 0 END", ("$id", profileId));
        tx.Commit();
    }, ct);
}

public sealed class SqliteSettingsStore(NinjaDatabase db) : ISettingsStore
{
    private const string Key = "launcher";

    public Task<LauncherSettings> LoadAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        var json = NinjaDatabase.Scalar(c, "SELECT value FROM settings WHERE key=$k", ("$k", Key)) as string;
        if (json is null) return new LauncherSettings();
        try { return JsonSerializer.Deserialize<LauncherSettings>(json, NinjaDatabase.Json) ?? new LauncherSettings(); }
        catch (JsonException) { return new LauncherSettings(); }
    }, ct);

    public Task SaveAsync(LauncherSettings settings, CancellationToken ct = default) => SetRowAsync(Key, JsonSerializer.Serialize(settings, NinjaDatabase.Json), ct);

    // "value:" prefix keeps launcher-owned values apart from the "launcher" settings row.
    public Task<string?> GetValueAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        return NinjaDatabase.Scalar(c, "SELECT value FROM settings WHERE key=$k", ("$k", "value:" + key)) as string;
    }, ct);

    public Task SetValueAsync(string key, string value, CancellationToken ct = default) => SetRowAsync("value:" + key, value, ct);

    private Task SetRowAsync(string key, string value, CancellationToken ct) => Task.Run(() =>
    {
        using var c = db.Open();
        NinjaDatabase.Exec(c, "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
    }, ct);
}

public sealed class SqliteActivityLog(NinjaDatabase db) : IActivityLog
{
    public Task AddAsync(string message, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        NinjaDatabase.Exec(c, "INSERT INTO activity(ts,message) VALUES($t,$m); DELETE FROM activity WHERE id <= (SELECT MAX(id) - 500 FROM activity)",
            ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$m", message));
    }, ct);

    public Task<IReadOnlyList<ActivityEntry>> RecentAsync(int count, CancellationToken ct = default) => Task.Run(() =>
    {
        using var c = db.Open();
        using var cmd = NinjaDatabase.Command(c, "SELECT ts, message FROM activity ORDER BY id DESC LIMIT $n", ("$n", count));
        using var r = cmd.ExecuteReader();
        var list = new List<ActivityEntry>();
        while (r.Read()) list.Add(new ActivityEntry(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1)));
        return (IReadOnlyList<ActivityEntry>)list;
    }, ct);
}
