using Alignment.Api.Model;

namespace Alignment.Api.Data;

/// <summary>
/// The only type that talks to the alignment database. Async only — there is no
/// synchronous twin (see Docs/ golden rules).
/// </summary>
public interface IStateRepository
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>Rebuild the wire-format state for one organisation, or null if it has no data yet.</summary>
    Task<AlignmentState?> LoadAsync(string organisationId, CancellationToken ct = default);

    /// <summary>Replace one organisation's rows from the wire-format state, in a single transaction.</summary>
    Task SaveAsync(string organisationId, AlignmentState state, CancellationToken ct = default);

    /// <summary>Delete one organisation's rows. Used only by the test-mode reset endpoint.</summary>
    Task ResetAsync(string organisationId, CancellationToken ct = default);
}
