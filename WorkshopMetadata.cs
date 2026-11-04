using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamWorkshopManager;

internal sealed record WorkshopMetadata
{
    public ulong CreatorSteamId { get; init; }
    public string CreatorName { get; init; } = string.Empty;
    public long TimeCreated { get; init; }
    public IReadOnlyList<string> PreviewImages { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
    public uint Subscriptions { get; init; }
    public uint Favorites { get; init; }
    public uint Views { get; init; }
    public uint? VotesUp { get; init; }
    public uint? VotesDown { get; init; }
    public bool FullDetails { get; init; }

    public string Difficulty
    {
        get
        {
            foreach (var (key, value) in Attributes)
                if ((key.Equals("difficulty", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("levelDifficulty", StringComparison.OrdinalIgnoreCase) || key == "难度")
                    && !string.IsNullOrWhiteSpace(value))
                    return $"{value}（作者标注）";
            foreach (var tag in Tags)
            {
                if (Regex.IsMatch(tag, @"^(Beginner|Easy|Normal|Medium|Hard|Very Hard|Expert|Insane|Extreme|Impossible)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    return $"{tag}（工坊标签）";
                var match = Regex.Match(tag, @"^(difficulty|难度)\s*[:=：]\s*(.+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (match.Success) return $"{match.Groups[2].Value}（工坊标签）";
            }
            return "未标注";
        }
    }

    public static IReadOnlyDictionary<string, string> ReadAttributes(
        IEnumerable<KeyValuePair<string, string>> tags, string? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in tags.Take(32))
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                result[key[..Math.Min(key.Length, 128)]] = value[..Math.Min(value.Length, 1024)];
        if (string.IsNullOrWhiteSpace(metadata) || metadata.Length > 65536) return result;
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject().Take(32))
                {
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) continue;
                    var value = property.Value.ToString();
                    if (value.Length is > 0 and <= 1024) result.TryAdd(property.Name, value);
                }
            }
        }
        catch (JsonException) { }
        return result;
    }
}
