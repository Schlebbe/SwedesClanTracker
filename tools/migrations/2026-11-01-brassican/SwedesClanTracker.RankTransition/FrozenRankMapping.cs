using SwedesClanTracker.Core;

namespace SwedesClanTracker.RankTransition;

/// <summary>
/// The one-time transition mapping. This is deliberately data, rather than a
/// live lookup, so an apply or rollback can never reinterpret a frozen target
/// after the Core rank policy changes.
/// </summary>
public static class FrozenRankMapping
{
    public const string Version = "legacy-rank-transition-v1";

    private static readonly IReadOnlyDictionary<string, string> LegacyToCanonical =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Recruit"] = "Brassican",
            ["Officer"] = "Brassican",
            ["Astral"] = "General",
            ["General"] = "Colonel"
        };

    private static readonly IReadOnlyDictionary<string, string> DiscordRoleRenames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Officer"] = "Brassican",
            ["Astral"] = "General",
            ["General"] = "Colonel"
        };

    public static IReadOnlyDictionary<string, string> LegacyMappings => LegacyToCanonical;

    public static IReadOnlyDictionary<string, string> DiscordRenames => DiscordRoleRenames;

    public static FrozenRankMappingDocument Document() => new(
        Version,
        LegacyToCanonical.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
        DiscordRoleRenames.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));

    public static string Map(string? rank)
    {
        var normalized = Normalize(rank);
        return LegacyToCanonical.TryGetValue(normalized, out var mapped)
            ? mapped
            : normalized;
    }

    public static string MapWomRole(string? role) => Map(role);

    public static bool TryMapDiscordRoleName(string? roleName, out string mapped)
    {
        var normalized = Normalize(roleName);
        if (DiscordRoleRenames.TryGetValue(normalized, out mapped!)) return true;
        mapped = normalized;
        return false;
    }

    public static bool IsSpecialOrUnknownWomRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return true;
        // These are the only legacy normal WOM roles in the frozen
        // transition. New Core policy may intentionally classify their names
        // as unknown after cutover, so the transition must recognize them
        // before consulting the live classifier.
        var normalized = Normalize(role);
        if (normalized.Equals("Officer", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Astral", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Recruit is a special WOM role in the existing policy. Its database
        // value is normalized, but its external WOM role is protected unless
        // a reviewer explicitly handles it.
        return RankRules.IsSpecialWomRole(role);
    }

    public static string Normalize(string? rank) =>
        RankRules.NormalizeRankName(rank ?? "");

    /// <summary>
    /// Returns true only when mapping a source value would collide with a
    /// different, already observed role. Collisions are never auto-resolved.
    /// </summary>
    public static bool HasTargetCollision(
        string targetName,
        IEnumerable<string> observedRoleNames,
        string sourceName)
    {
        var normalizedTarget = Normalize(targetName);
        var normalizedSource = Normalize(sourceName);
        return observedRoleNames.Any(x =>
            !string.Equals(Normalize(x), normalizedSource, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Normalize(x), normalizedTarget, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record FrozenRankMappingDocument(
    string Version,
    IReadOnlyDictionary<string, string> LegacyToCanonical,
    IReadOnlyDictionary<string, string> DiscordRoleRenames);
