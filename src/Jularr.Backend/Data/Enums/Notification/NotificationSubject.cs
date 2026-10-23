namespace Jularr.Backend.Data.Enums.Notification
{
    // A subject of two parts, such as a title and the reason a download waits; one unread notice is kept for the same pair.
    public static class NotificationSubject
    {
        private const char Separator = '\u001F';

        public static string Join(string title, string reason) => title + Separator + reason;

        public static (string Title, string? Reason) Split(string subject) =>
            subject.IndexOf(Separator) is var index and >= 0 ? (subject[..index], subject[(index + 1)..]) : (subject, null);
    }
}
