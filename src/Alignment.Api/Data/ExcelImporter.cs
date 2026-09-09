using System.Globalization;
using Alignment.Api.Model;
using ClosedXML.Excel;

namespace Alignment.Api.Data;

/// <summary>
/// One-shot import of a sample workbook, to prove the schema against real data.
/// Best-effort: the exported files vary between page versions. If a sheet cannot
/// be parsed it is skipped with a warning — the real schema proof is the
/// page → PUT → tables → GET → page round-trip covered by the @db Playwright tests.
///
/// Expected matrix shape (both link sheets):
///   row 1, cols B.. : objective headers  "PF1: OBJ1"
///   col A, rows 2.. : row headers        "PF1: M2"  (Metric-OBJ)  or  "PF1: OBJ2" (OBJ-OBJ)
///   cells           : strength, or "--" (out of scope) or "0"/blank (no link) — skip both.
/// </summary>
public static class ExcelImporter
{
    public static async Task<int> ImportAsync(IStateRepository repo, string path, string org, CancellationToken ct = default)
    {
        if (!File.Exists(path)) { Console.Error.WriteLine($"import: file not found: {path}"); return 1; }

        using var wb = new XLWorkbook(path);

        var portfolios = new List<string>();
        var metrics = new Dictionary<string, List<MetricDto>>();
        var objectives = new Dictionary<string, List<ObjectiveDto>>();
        var perf = new Dictionary<string, List<PerfLinkDto>>();
        var oo = new Dictionary<string, List<OoLinkDto>>();

        void EnsurePortfolio(string pf)
        {
            if (!portfolios.Contains(pf)) portfolios.Add(pf);
        }
        (string pf, string code)? Head(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || !raw.Contains(':')) return null;
            var parts = raw.Split(':', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }
        static bool Blocked(string v) => v.Trim() == "--";
        static bool NoLink(string v) => string.IsNullOrWhiteSpace(v) || v.Trim() == "0";
        static double? Num(string v) =>
            double.TryParse(v.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

        // ---- Metric-OBJ Align : rows = metrics, cols = objectives ----
        if (wb.Worksheets.TryGetWorksheet("Metric-OBJ Align", out var mo))
        {
            var cols = new Dictionary<int, (string pf, string code, string id)>();
            foreach (var cell in mo.Row(1).CellsUsed().Where(c => c.Address.ColumnNumber > 1))
            {
                var h = Head(cell.GetString());
                if (h is null) continue;
                var id = $"o-{h.Value.pf}-{h.Value.code}".ToLowerInvariant();
                cols[cell.Address.ColumnNumber] = (h.Value.pf, h.Value.code, id);
                EnsurePortfolio(h.Value.pf);
                Add(objectives, h.Value.pf, new ObjectiveDto(id, h.Value.code, true, 0));
            }
            foreach (var row in mo.RowsUsed().Where(r => r.RowNumber() > 1))
            {
                var rh = Head(row.Cell(1).GetString());
                if (rh is null) continue;
                var mid = $"m-{rh.Value.pf}-{rh.Value.code}".ToLowerInvariant();
                EnsurePortfolio(rh.Value.pf);
                Add(metrics, rh.Value.pf, new MetricDto(mid, rh.Value.code, true));
                foreach (var (colNo, col) in cols)
                {
                    var v = row.Cell(colNo).GetString();
                    if (Blocked(v) || NoLink(v)) continue;
                    if (Num(v) is { } strength && strength > 0)
                        Add(perf, rh.Value.pf, new PerfLinkDto(mid, col.id, strength));
                }
            }
        }

        // ---- OBJ-OBJ Align : cross-portfolio objective links ----
        if (wb.Worksheets.TryGetWorksheet("OBJ-OBJ Align", out var so))
        {
            var cols = new Dictionary<int, (string pf, string id)>();
            foreach (var cell in so.Row(1).CellsUsed().Where(c => c.Address.ColumnNumber > 1))
            {
                var h = Head(cell.GetString());
                if (h is null) continue;
                cols[cell.Address.ColumnNumber] = (h.Value.pf, $"o-{h.Value.pf}-{h.Value.code}".ToLowerInvariant());
            }
            foreach (var row in so.RowsUsed().Where(r => r.RowNumber() > 1))
            {
                var rh = Head(row.Cell(1).GetString());
                if (rh is null) continue;
                var sid = $"o-{rh.Value.pf}-{rh.Value.code}".ToLowerInvariant();
                foreach (var (colNo, col) in cols)
                {
                    var v = row.Cell(colNo).GetString();
                    if (Blocked(v) || NoLink(v) || col.pf == rh.Value.pf) continue;
                    if (Num(v) is { } strength && strength > 0)
                        Add(oo, $"{rh.Value.pf}→{col.pf}", new OoLinkDto(sid, col.id, strength));
                }
            }
        }

        if (portfolios.Count == 0)
        {
            Console.Error.WriteLine("import: no recognisable 'PFx: CODE' headers found — schema not proven by this file.");
            return 2;
        }

        var state = new AlignmentState
        {
            RootOrgName = "My Organisation (imported)",
            UI = new UiState
            {
                portfolios = portfolios,
                mode = "performance",
                leftPortfolio = portfolios[0],
                rightPortfolio = portfolios[0]
            },
            OA = new OaState
            {
                metricsByPortfolio = metrics,
                objectivesByPortfolio = objectives,
                perfLinksByPortfolio = perf,
                ooLinksByPair = oo
            }
        };

        await repo.SaveAsync(org, state, ct);
        Console.WriteLine($"import: {portfolios.Count} portfolios, " +
                          $"{metrics.Values.Sum(v => v.Count)} metrics, " +
                          $"{objectives.Values.Sum(v => v.Count)} objectives, " +
                          $"{perf.Values.Sum(v => v.Count)} metric→objective links, " +
                          $"{oo.Values.Sum(v => v.Count)} objective→objective links.");
        return 0;

        static void Add<T>(Dictionary<string, List<T>> m, string k, T v)
        {
            if (!m.TryGetValue(k, out var l)) m[k] = l = new();
            l.Add(v);
        }
    }
}
