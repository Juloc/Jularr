namespace Jularr.Backend.Logic.Playback
{
    // One running conversion as the budget sees it; the order counts up with every start.
    internal sealed record TranscodeLoad(Guid SessionId, int Weight, bool HasPriority, long Order);

    // The wanted conversion or one of the lighter ones that fits, and the running conversions that make room for it; no index means the server is full.
    internal sealed record TranscodeAdmission(int? CandidateIndex, IReadOnlyList<Guid> Evicted);

    internal static class TranscodeAdmissionLogic
    {
        // The budget counts in quarters of a 1080p conversion, so a smaller picture takes less of it.
        public const int SlotWeight = 4;

        // Only re-encoded video counts; copying the picture, direct play and music conversions cost next to nothing.
        public static int Weight(int? height) => height switch
        {
            null => 4,
            <= 480 => 1,
            <= 720 => 2,
            <= 1080 => 4,
            <= 1440 => 8,
            _ => 16
        };

        public static int Capacity(int maxTranscodes) => maxTranscodes * SlotWeight;

        // Candidates run from the wanted conversion to ever lighter ones. The first that fits the free budget wins. An account with priority first makes room for a candidate by stopping the newest
        // conversions of accounts without priority (never another priority account's), and only then settles for a lighter one.
        public static TranscodeAdmission Decide(IReadOnlyList<TranscodeLoad> running, int capacity, bool hasPriority, IReadOnlyList<int> candidateWeights)
        {
            var free = capacity - running.Sum(load => load.Weight);
            for (var index = 0; index < candidateWeights.Count; index++)
            {
                var weight = candidateWeights[index];
                if (weight == 0 || weight <= free)
                {
                    return new TranscodeAdmission(index, []);
                }

                if (!hasPriority || weight > capacity)
                {
                    continue;
                }

                var evicted = new List<Guid>();
                var room = free;
                foreach (var load in running.Where(load => !load.HasPriority && load.Weight > 0).OrderByDescending(load => load.Order))
                {
                    if (room >= weight)
                    {
                        break;
                    }

                    evicted.Add(load.SessionId);
                    room += load.Weight;
                }

                if (room >= weight)
                {
                    return new TranscodeAdmission(index, evicted);
                }
            }

            return new TranscodeAdmission(null, []);
        }
    }
}
