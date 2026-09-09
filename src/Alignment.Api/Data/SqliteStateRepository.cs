using System.Collections.Concurrent;
using System.Text.Json;
using Alignment.Api.Model;
using Microsoft.Data.Sqlite;

namespace Alignment.Api.Data;

/// <summary>
/// SQLite implementation. Phase-1 save strategy: on every PUT, wipe the
/// organisation's rows and rebuild them from the payload inside one transaction
/// ("replace tables from JSON"). Simple, correct, and still fully normalized —
/// per-field UPDATE statements are a later optimisation.
///
/// Phase-1 simplification: portfolios.id == the portfolio name. Links join on
/// metric_id / objective_id (never on portfolio), so a rename does not break
/// them, and because we rebuild every row per save, a rename is just old rows
/// out / new rows in. Phase 3 (multi-tenant) replaces this with a real slug.
/// </summary>
public sealed class SqliteStateRepository : IStateRepository
{
    private const string Arrow = "→"; // the → used in ooLinksByPair keys
    private readonly string _baseDir;
    private readonly string _baseFile;
    private readonly ILogger<SqliteStateRepository> _log;

    // SQLite allows one writer per file. This singleton serialises writes so
    // concurrent requests (parallel test workers, or two testers in Phase 2)
    // queue instead of hitting "database is locked".
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Org files whose schema has already been created this process. Keeps the
    // full CREATE TABLE script off the hot path for every GET/PUT after the first.
    private readonly ConcurrentDictionary<string, byte> _ready = new();

    public SqliteStateRepository(IConfiguration config, ILogger<SqliteStateRepository> log)
    {
        _log = log;
        var path = config["Alignment:Sqlite:DbPath"] ?? "Data/alignment.db";
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);
        _baseDir = Path.GetDirectoryName(path)!;
        _baseFile = Path.GetFileName(path);
        Directory.CreateDirectory(_baseDir);
        _log.LogInformation("SQLite database directory {Dir}", _baseDir);
    }

    /// <summary>
    /// One file per organisation. "demo" is the base file (alignment.db); every
    /// other org (Phase-1 tests, Phase-3 tenants) gets its own alignment-{org}.db.
    /// This keeps tenants fully isolated and sidesteps cross-tenant lock contention.
    /// </summary>
    private string PathFor(string org)
    {
        if (org == "demo") return Path.Combine(_baseDir, _baseFile);
        var stem = Path.GetFileNameWithoutExtension(_baseFile);
        var ext = Path.GetExtension(_baseFile);
        var safe = string.Concat(org.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(_baseDir, $"{stem}-{safe}{ext}");
    }

    private async Task<SqliteConnection> OpenAsync(string org, CancellationToken ct)
    {
        var path = PathFor(org);
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString());
        await con.OpenAsync(ct);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = _ready.ContainsKey(path)
            ? "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;"
            : "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;\n" + SqliteSchema.Sql;
        await cmd.ExecuteNonQueryAsync(ct);
        _ready.TryAdd(path, 1);
        return con;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var _ = await OpenAsync("demo", ct);
    }

    // ---------------------------------------------------------------- LOAD
    public async Task<AlignmentState?> LoadAsync(string org, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(org, ct);

        var orgName = await ScalarAsync(con, "SELECT name FROM organisations WHERE id = $o;", ct, ("$o", org));
        if (orgName is null) return null;

        var portfolios = new List<string>();
        await ReadAsync(con, "SELECT name FROM portfolios WHERE organisation_id = $o ORDER BY sort_order, name;", ct,
            r => portfolios.Add(r.GetString(0)), ("$o", org));

        var metrics = new Dictionary<string, List<MetricDto>>();
        await ReadAsync(con,
            """
            SELECT p.name, m.id, m.label, m.active
            FROM metrics m JOIN portfolios p ON p.id = m.portfolio_id
            WHERE m.organisation_id = $o
            ORDER BY m.sort_order, m.label;
            """, ct,
            r => Add(metrics, r.GetString(0), new MetricDto(r.GetString(1), r.GetString(2), r.GetInt64(3) != 0)),
            ("$o", org));

        var objectives = new Dictionary<string, List<ObjectiveDto>>();
        await ReadAsync(con,
            """
            SELECT p.name, o.id, o.label, o.active, o.weight
            FROM objectives o JOIN portfolios p ON p.id = o.portfolio_id
            WHERE o.organisation_id = $o
            ORDER BY o.sort_order, o.label;
            """, ct,
            r => Add(objectives, r.GetString(0), new ObjectiveDto(r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4))),
            ("$o", org));

        var perf = new Dictionary<string, List<PerfLinkDto>>();
        await ReadAsync(con,
            """
            SELECT p.name, l.metric_id, l.objective_id, l.strength
            FROM metric_objective_links l
            JOIN metrics m ON m.id = l.metric_id
            JOIN portfolios p ON p.id = m.portfolio_id
            WHERE l.organisation_id = $o;
            """, ct,
            r => Add(perf, r.GetString(0), new PerfLinkDto(r.GetString(1), r.GetString(2), r.GetDouble(3))),
            ("$o", org));

        var oo = new Dictionary<string, List<OoLinkDto>>();
        await ReadAsync(con,
            """
            SELECT sp.name AS src_pf, tp.name AS tgt_pf, l.source_objective_id, l.target_objective_id, l.strength
            FROM objective_objective_links l
            JOIN objectives so ON so.id = l.source_objective_id JOIN portfolios sp ON sp.id = so.portfolio_id
            JOIN objectives tobj ON tobj.id = l.target_objective_id JOIN portfolios tp ON tp.id = tobj.portfolio_id
            WHERE l.organisation_id = $o;
            """, ct,
            r => Add(oo, r.GetString(0) + Arrow + r.GetString(1),
                     new OoLinkDto(r.GetString(2), r.GetString(3), r.GetDouble(4))),
            ("$o", org));

        var mode = "performance";
        string? left = null, right = null;
        await ReadAsync(con, "SELECT mode, left_portfolio_id, right_portfolio_id FROM ui_state WHERE organisation_id = $o;", ct,
            r => { mode = r.GetString(0); left = r.IsDBNull(1) ? null : r.GetString(1); right = r.IsDBNull(2) ? null : r.GetString(2); },
            ("$o", org));

        JsonElement? colorScale = null;
        var csRaw = await ScalarAsync(con, "SELECT value FROM app_meta WHERE organisation_id = $o AND key = 'colorScale';", ct, ("$o", org));
        if (csRaw is not null)
        {
            try { colorScale = JsonSerializer.Deserialize<JsonElement>(csRaw); } catch { /* ignore corrupt cache */ }
        }

        return new AlignmentState
        {
            UI = new UiState
            {
                portfolios = portfolios,
                mode = mode,
                leftPortfolio = left ?? (portfolios.Count > 0 ? portfolios[0] : ""),
                rightPortfolio = right ?? (portfolios.Count > 0 ? portfolios[0] : "")
            },
            OA = new OaState
            {
                metricsByPortfolio = metrics,
                objectivesByPortfolio = objectives,
                perfLinksByPortfolio = perf,
                ooLinksByPair = oo
            },
            RootOrgName = orgName,
            ColorScale = colorScale
        };
    }

    // ---------------------------------------------------------------- SAVE
    public async Task SaveAsync(string org, AlignmentState s, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try { await SaveCoreAsync(org, s, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task SaveCoreAsync(string org, AlignmentState s, CancellationToken ct)
    {
        await using var con = await OpenAsync(org, ct);
        await using var tx = (SqliteTransaction)await con.BeginTransactionAsync(ct);

        async Task Exec(string sql, params (string, object?)[] ps)
        {
            await using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await WipeAsync(Exec);

        var now = DateTime.UtcNow.ToString("o");
        await Exec("INSERT INTO organisations(id, name, created_utc) VALUES($o, $n, $t) " +
                   "ON CONFLICT(id) DO UPDATE SET name = $n;",
            ("$o", org), ("$n", s.RootOrgName ?? "My Organisation"), ("$t", now));

        var known = new HashSet<string>(s.UI.portfolios);
        for (var i = 0; i < s.UI.portfolios.Count; i++)
        {
            var name = s.UI.portfolios[i];
            await Exec("INSERT INTO portfolios(id, organisation_id, name, sort_order) VALUES($id, $o, $n, $so);",
                ("$id", name), ("$o", org), ("$n", name), ("$so", i));
        }

        var metricIds = new HashSet<string>();
        foreach (var (pf, list) in s.OA.metricsByPortfolio)
        {
            if (!known.Contains(pf)) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var m = list[i];
                await Exec("INSERT OR IGNORE INTO metrics(id, organisation_id, portfolio_id, label, active, sort_order) " +
                           "VALUES($id, $o, $p, $l, $a, $so);",
                    ("$id", m.id), ("$o", org), ("$p", pf), ("$l", m.label), ("$a", m.active ? 1 : 0), ("$so", i));
                metricIds.Add(m.id);
            }
        }

        var objectiveIds = new HashSet<string>();
        foreach (var (pf, list) in s.OA.objectivesByPortfolio)
        {
            if (!known.Contains(pf)) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var o = list[i];
                await Exec("INSERT OR IGNORE INTO objectives(id, organisation_id, portfolio_id, label, active, weight, sort_order) " +
                           "VALUES($id, $o, $p, $l, $a, $w, $so);",
                    ("$id", o.id), ("$o", org), ("$p", pf), ("$l", o.label), ("$a", o.active ? 1 : 0), ("$w", o.weight), ("$so", i));
                objectiveIds.Add(o.id);
            }
        }

        foreach (var list in s.OA.perfLinksByPortfolio.Values)
            foreach (var l in list)
                if (l.strength > 0 && metricIds.Contains(l.metricId) && objectiveIds.Contains(l.objectiveId))
                    await Exec("INSERT OR IGNORE INTO metric_objective_links(organisation_id, metric_id, objective_id, strength) " +
                               "VALUES($o, $m, $ob, $s);",
                        ("$o", org), ("$m", l.metricId), ("$ob", l.objectiveId), ("$s", l.strength));

        foreach (var list in s.OA.ooLinksByPair.Values)
            foreach (var l in list)
                if (l.strength > 0 && l.leftObjectiveId != l.rightObjectiveId
                    && objectiveIds.Contains(l.leftObjectiveId) && objectiveIds.Contains(l.rightObjectiveId))
                    await Exec("INSERT OR IGNORE INTO objective_objective_links(organisation_id, source_objective_id, target_objective_id, strength) " +
                               "VALUES($o, $s, $t, $st);",
                        ("$o", org), ("$s", l.leftObjectiveId), ("$t", l.rightObjectiveId), ("$st", l.strength));

        await Exec("INSERT INTO ui_state(organisation_id, mode, left_portfolio_id, right_portfolio_id) VALUES($o, $m, $l, $r);",
            ("$o", org), ("$m", string.IsNullOrEmpty(s.UI.mode) ? "performance" : s.UI.mode),
            ("$l", NullIfEmpty(s.UI.leftPortfolio)), ("$r", NullIfEmpty(s.UI.rightPortfolio)));

        if (s.ColorScale is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } cs)
            await Exec("INSERT INTO app_meta(organisation_id, key, value) VALUES($o, 'colorScale', $v);",
                ("$o", org), ("$v", cs.GetRawText()));

        await tx.CommitAsync(ct);
    }

    public async Task ResetAsync(string org, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try { await ResetCoreAsync(org, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task ResetCoreAsync(string org, CancellationToken ct)
    {
        await using var con = await OpenAsync(org, ct);
        await using var tx = (SqliteTransaction)await con.BeginTransactionAsync(ct);
        async Task Exec(string sql, params (string, object?)[] ps)
        {
            await using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await WipeAsync(Exec);
        await Exec("DELETE FROM organisations;");
        await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- helpers
    // One organisation per file (see PathFor), so a wipe clears every row — no
    // organisation_id filter. This also self-heals a file left polluted by an
    // older build that shared one database across organisations.
    private static async Task WipeAsync(Func<string, (string, object?)[], Task> exec)
    {
        foreach (var t in new[]
        {
            "metric_objective_links", "objective_objective_links",
            "metrics", "objectives", "portfolios", "ui_state", "app_meta"
        })
            await exec($"DELETE FROM {t};", System.Array.Empty<(string, object?)>());
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
        list.Add(value);
    }

    private static object? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static async Task<string?> ScalarAsync(SqliteConnection con, string sql, CancellationToken ct, params (string, object?)[] ps)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static async Task ReadAsync(SqliteConnection con, string sql, CancellationToken ct,
        Action<SqliteDataReader> onRow, params (string, object?)[] ps)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) onRow(reader);
    }
}
