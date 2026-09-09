using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alignment.Api.Model;

/// <summary>
/// The wire format for GET/PUT /api/state. It matches the shape the v11 page's
/// #jsonView block shows and applyLoadedState() consumes — NOT the older
/// ROOT.pf1.ObjectiveAlignment shape from the draft chat. See Docs/ Appendix F.
/// </summary>
public sealed record AlignmentState
{
    [JsonPropertyName("UI")] public UiState UI { get; init; } = new();
    [JsonPropertyName("OA")] public OaState OA { get; init; } = new();

    [JsonPropertyName("Root Org Name")] public string? RootOrgName { get; init; }

    [JsonPropertyName("colorScale")] public JsonElement? ColorScale { get; init; }
}

public sealed record UiState
{
    public List<string> portfolios { get; init; } = new();
    public string mode { get; init; } = "performance";
    public string leftPortfolio { get; init; } = "";
    public string rightPortfolio { get; init; } = "";
}

public sealed record OaState
{
    public Dictionary<string, List<MetricDto>> metricsByPortfolio { get; init; } = new();
    public Dictionary<string, List<ObjectiveDto>> objectivesByPortfolio { get; init; } = new();
    public Dictionary<string, List<PerfLinkDto>> perfLinksByPortfolio { get; init; } = new();

    /// <summary>Keyed by "Source Portfolio→Target Portfolio".</summary>
    public Dictionary<string, List<OoLinkDto>> ooLinksByPair { get; init; } = new();
}

public sealed record MetricDto(string id, string label, bool active = true);
public sealed record ObjectiveDto(string id, string label, bool active = true, double weight = 0);
public sealed record PerfLinkDto(string metricId, string objectiveId, double strength);
public sealed record OoLinkDto(string leftObjectiveId, string rightObjectiveId, double strength);
