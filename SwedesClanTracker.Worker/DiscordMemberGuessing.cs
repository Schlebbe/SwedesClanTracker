using System.Globalization;
using System.Text;

namespace SwedesClanTracker.Worker;

public static class DiscordMemberGuessing
{
    private const int MinimumPlausibleScore = 70;

    public static DiscordMemberGuessResult Guess(
        IEnumerable<string> playerNames,
        IEnumerable<DiscordMemberLookupCandidate> members,
        int limit = 3)
    {
        var aliases = playerNames
            .SelectMany(BuildNameForms)
            .Where(x => x.Compact.Length >= 2)
            .GroupBy(x => x.Compact, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        if (aliases.Count == 0)
        {
            return new DiscordMemberGuessResult([]);
        }

        var matches = new Dictionary<ulong, DiscordMemberGuessMatch>();
        foreach (var member in members)
        {
            var fields = BuildMemberFields(member);
            DiscordMemberGuessMatch? best = null;
            foreach (var alias in aliases)
            {
                foreach (var field in fields)
                {
                    var scored = Score(alias, field, member);
                    if (scored.Score < MinimumPlausibleScore) continue;
                    if (best is null || scored.Score > best.Score)
                    {
                        best = scored;
                    }
                }
            }

            if (best is null) continue;
            if (!matches.TryGetValue(member.UserId, out var existing) || best.Score > existing.Score)
            {
                matches[member.UserId] = best;
            }
        }

        var ordered = matches.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToList();

        return new DiscordMemberGuessResult(ordered);
    }

    private static DiscordMemberGuessMatch Score(
        NormalizedName alias,
        NormalizedMemberField field,
        DiscordMemberLookupCandidate member)
    {
        if (alias.Compact.Length == 0 || field.Compact.Length == 0)
        {
            return BuildMatch(member, field, alias, 0, DiscordMemberMatchStrength.None);
        }

        if (string.Equals(alias.Compact, field.Compact, StringComparison.Ordinal))
        {
            return BuildMatch(member, field, alias, 100, DiscordMemberMatchStrength.Exact);
        }

        if (string.Equals(alias.LeetFoldedCompact, field.LeetFoldedCompact, StringComparison.Ordinal))
        {
            return BuildMatch(member, field, alias, 94, DiscordMemberMatchStrength.Strong);
        }

        if (alias.Compact.Length >= 4 && field.Compact.Contains(alias.Compact, StringComparison.Ordinal))
        {
            return BuildMatch(member, field, alias, 86, DiscordMemberMatchStrength.Possible);
        }

        if (field.Compact.Length >= 4 && alias.Compact.Contains(field.Compact, StringComparison.Ordinal))
        {
            return BuildMatch(member, field, alias, 82, DiscordMemberMatchStrength.Possible);
        }

        var boundaryOverlapScore = BoundaryOverlapScore(alias.Compact, field.Compact);
        var leetBoundaryOverlapScore = BoundaryOverlapScore(alias.LeetFoldedCompact, field.LeetFoldedCompact);
        var bestBoundaryOverlapScore = Math.Max(boundaryOverlapScore, leetBoundaryOverlapScore);
        if (bestBoundaryOverlapScore >= MinimumPlausibleScore)
        {
            return BuildMatch(member, field, alias, bestBoundaryOverlapScore, DiscordMemberMatchStrength.Possible);
        }

        var directSimilarity = Similarity(alias.Compact, field.Compact);
        var leetSimilarity = Similarity(alias.LeetFoldedCompact, field.LeetFoldedCompact);
        var similarity = Math.Max(directSimilarity, leetSimilarity);
        var score = (int)Math.Round(similarity * 100, MidpointRounding.AwayFromZero);
        var strength = score >= 90
            ? DiscordMemberMatchStrength.Strong
            : score >= MinimumPlausibleScore
                ? DiscordMemberMatchStrength.Possible
                : DiscordMemberMatchStrength.None;

        return BuildMatch(member, field, alias, score, strength);
    }

    private static DiscordMemberGuessMatch BuildMatch(
        DiscordMemberLookupCandidate member,
        NormalizedMemberField field,
        NormalizedName alias,
        int score,
        DiscordMemberMatchStrength strength)
    {
        return new DiscordMemberGuessMatch(
            member.UserId,
            BuildDisplayLabel(member),
            member.Mention,
            score,
            strength,
            field.Label,
            field.Value,
            alias.Original,
            member.FromDiscordSearch);
    }

    private static IReadOnlyList<NormalizedMemberField> BuildMemberFields(DiscordMemberLookupCandidate member)
    {
        var fields = new (string Label, string? Value)[]
        {
            ("nickname", member.Nickname),
            ("display name", member.DisplayName),
            ("global name", member.GlobalName),
            ("username", member.Username)
        };

        return fields
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new NormalizedMemberField(x.Label, x.Value!, NormalizeCompact(x.Value!), NormalizeCompact(FoldLeetspeak(x.Value!))))
            .GroupBy(x => $"{x.Label}:{x.Compact}", StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();
    }

    private static IEnumerable<NormalizedName> BuildNameForms(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        var trimmed = value.Trim();
        yield return new NormalizedName(trimmed, NormalizeCompact(trimmed), NormalizeCompact(FoldLeetspeak(trimmed)));

        var withoutCommonSeparators = trimmed
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        if (!string.Equals(withoutCommonSeparators, trimmed, StringComparison.Ordinal))
        {
            yield return new NormalizedName(trimmed, NormalizeCompact(withoutCommonSeparators), NormalizeCompact(FoldLeetspeak(withoutCommonSeparators)));
        }
    }

    private static string BuildDisplayLabel(DiscordMemberLookupCandidate member)
    {
        if (!string.IsNullOrWhiteSpace(member.Nickname)) return member.Nickname.Trim();
        if (!string.IsNullOrWhiteSpace(member.DisplayName)) return member.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(member.GlobalName)) return member.GlobalName.Trim();
        return member.Username.Trim();
    }

    private static string NormalizeCompact(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;

            var lower = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(lower))
            {
                builder.Append(lower);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string FoldLeetspeak(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.ToLowerInvariant(ch) switch
            {
                '0' => 'o',
                '1' => 'l',
                '!' => 'l',
                '3' => 'e',
                '4' => 'a',
                '@' => 'a',
                '5' => 's',
                '7' => 't',
                '8' => 'b',
                _ => ch
            });
        }

        return builder.ToString();
    }

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (string.Equals(left, right, StringComparison.Ordinal)) return 1;

        var distance = LevenshteinDistance(left, right);
        var maxLength = Math.Max(left.Length, right.Length);
        return 1 - (double)distance / maxLength;
    }

    private static int BoundaryOverlapScore(string left, string right)
    {
        var overlap = Math.Max(
            CommonBoundaryOverlapLength(left, right),
            CommonBoundaryOverlapLength(right, left));

        if (overlap < 5)
        {
            return 0;
        }

        var shorter = Math.Min(left.Length, right.Length);
        var shorterCoverage = (double)overlap / shorter;

        return shorterCoverage >= 0.85
            ? 80
            : 74;
    }

    private static int CommonBoundaryOverlapLength(string suffixSource, string prefixSource)
    {
        var max = Math.Min(suffixSource.Length, prefixSource.Length);
        for (var length = max; length >= 1; length--)
        {
            if (suffixSource.EndsWith(prefixSource[..length], StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record NormalizedName(string Original, string Compact, string LeetFoldedCompact);

    private sealed record NormalizedMemberField(string Label, string Value, string Compact, string LeetFoldedCompact);
}

public sealed record DiscordMemberLookupCandidate(
    ulong UserId,
    string Username,
    string? GlobalName,
    string? Nickname,
    string? DisplayName,
    bool FromDiscordSearch = false)
{
    public string Mention => $"<@{UserId}>";
}

public sealed record DiscordMemberGuessResult(IReadOnlyList<DiscordMemberGuessMatch> Matches)
{
    public DiscordMemberGuessMatch? Best => Matches.FirstOrDefault();
}

public sealed record DiscordMemberGuessMatch(
    ulong UserId,
    string DisplayLabel,
    string Mention,
    int Score,
    DiscordMemberMatchStrength Strength,
    string MatchedField,
    string MatchedValue,
    string PlayerAlias,
    bool FromDiscordSearch);

public enum DiscordMemberMatchStrength
{
    None,
    Possible,
    Strong,
    Exact
}
