using System.Text.Json;

namespace SwedesClanTracker.Core;

public static class LifecycleMetadataMatcher
{
    public static bool HasIntProperty(string json, string property, int expected)
        => TryGetIntProperty(json, property, out var value) && value == expected;

    public static bool HasUlongProperty(string json, string property, ulong expected)
        => TryGetUlongProperty(json, property, out var value) && value == expected;

    public static bool HasStringProperty(string json, string property, string expected, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        return TryGetStringProperty(json, property, out var value) &&
               !string.IsNullOrWhiteSpace(value) &&
               string.Equals(value, expected, comparison);
    }

    public static bool TryGetIntProperty(string json, string property, out int value)
    {
        value = 0;
        if (!TryGetProperty(json, property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
        {
            value = n;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    public static bool TryGetUlongProperty(string json, string property, out ulong value)
    {
        value = 0;
        if (!TryGetProperty(json, property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt64(out var n))
        {
            value = n;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String && ulong.TryParse(prop.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    public static bool TryGetStringProperty(string json, string property, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(json, property, out var prop)) return false;
        value = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => prop.ToString()
        };
        return true;
    }

    private static bool TryGetProperty(string json, string property, out JsonElement prop)
    {
        prop = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(property, out var found)) return false;
            prop = found.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
