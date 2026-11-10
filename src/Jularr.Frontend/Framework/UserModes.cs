using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // The five modes of the user area: each is a world of its own with the media types (scope slugs) that belong to it; every page of the user area shows only the active one.
    // The mode is kept per profile and device in a cookie (`?mode=` switches it), so it needs no database.
    public sealed record UserMode(string Key, string LabelKey, string Icon, IReadOnlyList<string> Slugs);

    public static class UserModes
    {
        public const string Parameter = "mode";

        public static IReadOnlyList<UserMode> All { get; } =
        [
            new("watch", "mode.watch", "play", ["movies", "series"]),
            new("animanga", "mode.animanga", "spark", ["anime", "manga", "lightnovels"]),
            new("read", "mode.read", "book", ["books"]),
            new("hear", "mode.hear", "headphones", ["music"]),
            new("play", "mode.play", "gamepad", ["games"])
        ];

        public static UserMode Default => All[0];

        public static UserMode? Find(string? key) => All.FirstOrDefault(mode => mode.Key == key);

        public static string CookieName(long profileId) => "jularr-mode-" + profileId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The mode of this request: the one the address names (kept in the cookie), else the one the address's media types belong to (a link to anime opens the anime mode),
        // else the cookie's, else the first.
        public static UserMode Resolve(HttpContext context, long? profileId)
        {
            var named = Find(context.Request.Query[Parameter].ToString()) ?? OfTypes(context.Request.Query["type"]);
            if (profileId is not { } profile) return named ?? Default;
            var cookie = CookieName(profile);
            if (named is not null)
            {
                context.Response.Cookies.Append(cookie, named.Key, new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps });
                return named;
            }

            return Find(context.Request.Cookies[cookie]) ?? Default;
        }

        // The mode all the named media types belong to; none when there are none or they belong to several.
        private static UserMode? OfTypes(IEnumerable<string?> values)
        {
            var slugs = values.SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(slug => All.Any(mode => mode.Slugs.Contains(slug))).Distinct().ToList();
            var modes = slugs.Select(Of).Distinct().ToList();
            return modes.Count == 1 ? modes[0] : null;
        }

        // The mode a scope slug (movies, manga, ...) belongs to.
        public static UserMode Of(string slug) => All.FirstOrDefault(mode => mode.Slugs.Contains(slug)) ?? Default;
    }
}
