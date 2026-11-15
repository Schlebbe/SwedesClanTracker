using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SwedesClanTracker.Core;

public sealed record OpenMergeReviewReference(
    int MergePlayerId,
    int? SuggestedPreviousPlayerId,
    string? SuggestedPreviousUsername);

/// <summary>
/// Shared, read-only interpretation of an open rename/merge review.
/// Missing-roster requirements for the suggested previous identity are
/// suppressed while the merge decision remains open, but the identity itself
/// remains available to rename matching and audit projections.
/// </summary>
public static class MergeReviewPolicy
{
    public static async Task<IReadOnlyList<OpenMergeReviewReference>> GetOpenReferencesAsync(
        TrackerDbContext db,
        CancellationToken ct)
    {
        var openMerges = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_ACTION_REQUIRED" && x.Status == "OPEN")
            .ToListAsync(ct);
        var postedMerges = await db.LifecycleEvents
            .Where(x => x.EventType == "MERGE_DISCORD_POSTED")
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        return openMerges
            .Select(merge =>
            {
                var metadata = ReadMetadata(merge.MetadataJson);
                var suggestedUsername = Normalize(Pick(metadata, "SuggestedPrevious", "PreviousPlayer"));
                var suggestedPlayerId = ParseInt(Pick(metadata, "SuggestedPreviousPlayerId", "PreviousPlayerId"));
                if (string.IsNullOrWhiteSpace(suggestedUsername) && !suggestedPlayerId.HasValue)
                {
                    var posted = postedMerges.FirstOrDefault(x => x.PlayerId == merge.PlayerId);
                    if (posted is not null)
                    {
                        var postedMetadata = ReadMetadata(posted.MetadataJson);
                        suggestedUsername = Normalize(Pick(postedMetadata, "SuggestedPrevious", "PreviousPlayer"));
                        suggestedPlayerId = ParseInt(Pick(postedMetadata, "SuggestedPreviousPlayerId", "PreviousPlayerId"));
                    }
                }

                return new OpenMergeReviewReference(
                    merge.PlayerId,
                    suggestedPlayerId,
                    string.IsNullOrWhiteSpace(suggestedUsername) ? null : suggestedUsername);
            })
            .Where(x => x.SuggestedPreviousPlayerId.HasValue || !string.IsNullOrWhiteSpace(x.SuggestedPreviousUsername))
            .ToArray();
    }

    public static bool ControlsPreviousIdentity(
        IEnumerable<OpenMergeReviewReference> references,
        int playerId,
        string? username,
        int? excludingMergePlayerId = null)
    {
        var normalizedUsername = Normalize(username);
        return references.Any(reference =>
            (!excludingMergePlayerId.HasValue || reference.MergePlayerId != excludingMergePlayerId.Value) &&
            ((reference.SuggestedPreviousPlayerId.HasValue && reference.SuggestedPreviousPlayerId.Value == playerId) ||
             (!string.IsNullOrWhiteSpace(normalizedUsername) &&
              string.Equals(reference.SuggestedPreviousUsername, normalizedUsername, StringComparison.OrdinalIgnoreCase))));
    }

    public static string Normalize(string? username) => UsernameRules.NormalizeUsername(username ?? "");

    private static Dictionary<string, string> ReadMetadata(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => property.Value.GetRawText()
                };
            }
        }
        catch
        {
            // Malformed merge metadata must not suppress an unrelated identity.
        }

        return result;
    }

    private static string? Pick(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var result) ? result : null;
}
