using Alignment.Api.Data;
using Alignment.Api.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alignment.Api.Tests;

public sealed class StateRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"align-test-{Guid.NewGuid():N}.db");
    private readonly SqliteStateRepository _repo;

    public StateRepositoryTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Alignment:Sqlite:DbPath"] = _dbPath })
            .Build();
        _repo = new SqliteStateRepository(config, NullLogger<SqliteStateRepository>.Instance);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { /* best effort */ }
    }

    private static AlignmentState Sample() => new()
    {
        RootOrgName = "Acme Health",
        UI = new UiState { portfolios = ["Portfolio A", "Portfolio B"], mode = "objectives", leftPortfolio = "Portfolio A", rightPortfolio = "Portfolio B" },
        OA = new OaState
        {
            metricsByPortfolio = new() { ["Portfolio A"] = [new("m1", "Revenue", true), new("m2", "NPS", false)] },
            objectivesByPortfolio = new()
            {
                ["Portfolio A"] = [new("o1", "Grow", true, 40)],
                ["Portfolio B"] = [new("o9", "Expand", true, 60)],
            },
            perfLinksByPortfolio = new() { ["Portfolio A"] = [new("m1", "o1", 25)] },
            ooLinksByPair = new() { ["Portfolio A→Portfolio B"] = [new("o1", "o9", 15)] },
        },
    };

    [Fact]
    public async Task Save_then_Load_round_trips()
    {
        await _repo.EnsureSchemaAsync();
        await _repo.SaveAsync("demo", Sample());

        var s = await _repo.LoadAsync("demo");

        Assert.NotNull(s);
        Assert.Equal("Acme Health", s!.RootOrgName);
        Assert.Equal(new[] { "Portfolio A", "Portfolio B" }, s.UI.portfolios);
        Assert.Equal("objectives", s.UI.mode);
        Assert.Equal(2, s.OA.metricsByPortfolio["Portfolio A"].Count);
        Assert.False(s.OA.metricsByPortfolio["Portfolio A"].Single(m => m.id == "m2").active);
        Assert.Equal(40, s.OA.objectivesByPortfolio["Portfolio A"].Single().weight);
        Assert.Equal(25, s.OA.perfLinksByPortfolio["Portfolio A"].Single().strength);
        Assert.Equal(15, s.OA.ooLinksByPair["Portfolio A→Portfolio B"].Single().strength);
    }

    [Fact]
    public async Task Load_returns_null_for_an_unknown_org()
    {
        await _repo.EnsureSchemaAsync();
        Assert.Null(await _repo.LoadAsync("demo"));
    }

    [Fact]
    public async Task Save_replaces_links_that_disappeared()
    {
        await _repo.EnsureSchemaAsync();
        await _repo.SaveAsync("demo", Sample());

        var s = await _repo.LoadAsync("demo");
        s!.OA.perfLinksByPortfolio["Portfolio A"].Clear();     // user deleted the link
        s.OA.ooLinksByPair["Portfolio A→Portfolio B"].Clear();
        await _repo.SaveAsync("demo", s);

        var after = await _repo.LoadAsync("demo");
        Assert.Empty(after!.OA.perfLinksByPortfolio.GetValueOrDefault("Portfolio A") ?? []);
        Assert.Empty(after.OA.ooLinksByPair.GetValueOrDefault("Portfolio A→Portfolio B") ?? []);
        Assert.Equal(2, after.OA.metricsByPortfolio["Portfolio A"].Count);   // dimensions kept
    }

    [Fact]
    public async Task Save_drops_zero_strength_and_same_portfolio_links()
    {
        await _repo.EnsureSchemaAsync();
        var s = Sample();
        s.OA.perfLinksByPortfolio["Portfolio A"].Add(new("m2", "o1", 0));          // zero -> dropped
        s.OA.ooLinksByPair["Portfolio A→Portfolio A"] = [new("o1", "o1", 10)];     // self -> dropped

        await _repo.SaveAsync("demo", s);
        var raw = System.Text.Json.JsonSerializer.Serialize(await _repo.LoadAsync("demo"));

        Assert.DoesNotContain("\"strength\":0", raw);
        Assert.Single((await _repo.LoadAsync("demo"))!.OA.perfLinksByPortfolio["Portfolio A"]);
    }

    [Fact]
    public async Task Reset_removes_the_org()
    {
        await _repo.EnsureSchemaAsync();
        await _repo.SaveAsync("demo", Sample());
        await _repo.ResetAsync("demo");
        Assert.Null(await _repo.LoadAsync("demo"));
    }
}
