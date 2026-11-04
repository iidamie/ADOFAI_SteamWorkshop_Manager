using System.Globalization;

namespace SteamWorkshopManager;

internal static class WorkshopLinkParser
{
    public static bool TryParse(string? value, out ulong id)
    {
        id = 0;
        var input = value?.Trim();
        if (string.IsNullOrEmpty(input))
            return false;
        if (TryId(input, out id))
            return true;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            return uri.Host.Equals("url", StringComparison.OrdinalIgnoreCase)
                && parts.Length == 2 && parts[0].Equals("CommunityFilePage", StringComparison.OrdinalIgnoreCase)
                && TryId(parts[1], out id);
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort
            || !(uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase)))
            return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.Equals("/sharedfiles/filedetails", StringComparison.OrdinalIgnoreCase)
            && !path.Equals("/workshop/filedetails", StringComparison.OrdinalIgnoreCase))
            return false;
        var found = false;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (!Uri.UnescapeDataString(parts[0]).Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;
            if (found || parts.Length != 2 || !TryId(Uri.UnescapeDataString(parts[1]), out id))
            {
                id = 0;
                return false;
            }
            found = true;
        }
        return found;
    }

    private static bool TryId(string value, out ulong id)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}

internal static class ManagerPaging
{
    public const int PageSize = 20;
    public static int Count(int items) => Math.Max(1, (int)(((long)Math.Max(0, items) + PageSize - 1) / PageSize));
    public static int Clamp(int page, int items) => Math.Clamp(page, 1, Count(items));
    public static bool Matches(string query, params string?[] values)
        => string.IsNullOrWhiteSpace(query) || values.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true);
}
