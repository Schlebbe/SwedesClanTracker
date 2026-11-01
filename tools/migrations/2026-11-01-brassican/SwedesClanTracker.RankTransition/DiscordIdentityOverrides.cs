using System.Globalization;
using System.Text.Json;

namespace SwedesClanTracker.RankTransition;

/// <summary>
/// Explicitly reviewed tracker-player to Discord-user links. This file is a
/// human review input, not an identity guess: callers should pass the fresh
/// guild member IDs to <see cref="Parse"/> so stale or mistyped targets fail
/// before a plan can be applied. Multiple player IDs may intentionally point
/// to one Discord user; the rank planner groups those accounts by highest
/// confirmed floor.
/// </summary>
public sealed record DiscordIdentityOverrides(
    IReadOnlyDictionary<int, string> PlayerToDiscordUserId)
{
    public IReadOnlySet<string> SharedDiscordUserIds =>
        PlayerToDiscordUserId
            .GroupBy(x => x.Value, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

    public static DiscordIdentityOverrides Parse(
        string json,
        IReadOnlySet<string>? knownDiscordUserIds = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Discord identity override JSON is empty.");

        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.ValueKind switch
        {
            JsonValueKind.Object => ParseObject(document.RootElement),
            JsonValueKind.Array => ParseArray(document.RootElement),
            _ => throw new InvalidDataException("Discord identity overrides must be a JSON object or array.")
        };

        if (values.Count == 0) throw new InvalidDataException("Discord identity override JSON contains no mappings.");
        if (knownDiscordUserIds is not null)
        {
            var missing = values.Values
                .Where(x => !knownDiscordUserIds.Contains(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException(
                    "Discord identity override target(s) were not present in the fresh guild member observation: " +
                    string.Join(", ", missing));
            }
        }

        return new DiscordIdentityOverrides(values);
    }

    public static async Task<DiscordIdentityOverrides> LoadAsync(
        string path,
        IReadOnlySet<string>? knownDiscordUserIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An override JSON path is required.", nameof(path));
        var json = await File.ReadAllTextAsync(path, ct);
        return Parse(json, knownDiscordUserIds);
    }

    private static Dictionary<int, string> ParseObject(JsonElement root)
    {
        var result = new Dictionary<int, string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerId))
            {
                throw new InvalidDataException($"Discord identity override key '{property.Name}' is not a positive PlayerId.");
            }

            Add(result, playerId, property.Value, property.Name);
        }

        return result;
    }

    private static Dictionary<int, string> ParseArray(JsonElement root)
    {
        var result = new Dictionary<int, string>();
        var index = 0;
        foreach (var element in root.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(element, "playerId", out var playerIdElement) ||
                !TryGetProperty(element, "discordUserId", out var discordUserIdElement))
            {
                throw new InvalidDataException($"Discord identity override array item {index} must contain playerId and discordUserId.");
            }

            var playerId = ReadPlayerId(playerIdElement, $"array item {index}");
            Add(result, playerId, discordUserIdElement, $"array item {index}");
        }

        return result;
    }

    private static void Add(
        IDictionary<int, string> result,
        int playerId,
        JsonElement discordUserIdElement,
        string source)
    {
        if (playerId <= 0) throw new InvalidDataException($"Discord identity override {source} has a non-positive PlayerId.");
        if (result.ContainsKey(playerId)) throw new InvalidDataException($"Discord identity override contains duplicate PlayerId {playerId}.");
        if (discordUserIdElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Discord identity override {source} must use a string Discord user ID.");
        }

        var discordUserId = discordUserIdElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(discordUserId) || !ulong.TryParse(discordUserId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidDataException($"Discord identity override {source} has an invalid Discord user ID.");
        }

        result.Add(playerId, discordUserId);
    }

    private static int ReadPlayerId(JsonElement value, string source)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text)) return text;
        throw new InvalidDataException($"Discord identity override {source} has an invalid PlayerId.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
