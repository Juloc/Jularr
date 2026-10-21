namespace Jularr.Backend.Logic.Reading
{
    // How the recognition of a scan is spread over time and pages: a bounded slice, the open page first, and a rest as long as the work. Whether it runs while Jularr is used is the job registry's to decide.
    internal static class ReadingRecognitionPlan
    {
        // A slice reads at most this many pages, or stops when this much time has gone, whichever comes first.
        public const int SlicePages = 8;
        public static readonly TimeSpan SliceTime = TimeSpan.FromSeconds(20);

        // Rests at most this long between slices, however long a slice took.
        private static readonly TimeSpan LongestRest = TimeSpan.FromMinutes(1);

        // The pages still to read in the order they are read: from the page a reader has open to the end, then from the start to just before it.
        public static IEnumerable<int> Remaining(int pages, int? wanted, IReadOnlySet<int> done)
        {
            var start = Math.Clamp(wanted ?? 1, 1, Math.Max(1, pages));
            return Enumerable.Range(start, Math.Max(0, pages - start + 1)).Concat(Enumerable.Range(1, start - 1)).Where(page => !done.Contains(page));
        }

        // A slice of work is followed by as much rest, so a scan takes at most half of the cores it uses.
        public static TimeSpan Rest(TimeSpan worked) => worked > LongestRest ? LongestRest : worked;
    }
}
