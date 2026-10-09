namespace Jularr.Web.Features.Notifications;

public static class NotificationLinks
{
    public static string? LocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(value);
            return decoded.StartsWith('/') && !decoded.StartsWith("//", StringComparison.Ordinal) && !decoded.Contains('\\') && !decoded.Any(char.IsControl) ? value : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
