using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;

namespace Jularr.Backend.Logic.Playback
{
    // The best way to convert and how many conversions a benchmark proposes.
    internal sealed record TranscodeProposal(TranscodeAcceleration Acceleration, int MaxTranscodes);

    internal static class TranscodeLogic
    {
        // Heights the ladder of lower pictures chooses from.
        public static readonly IReadOnlyList<int> LadderChoices = [2160, 1440, 1080, 720, 480, 360];

        // A single 1080p conversion must run this much faster than the picture plays, so the rest of the server keeps its room.
        private const decimal Headroom = 1.5m;
        private const int HardwareMaximum = 8;

        // The cap a ladder height stands for; the rates follow the High and Saver qualities.
        public static PlaybackQualityTarget LadderStep(int height) => height switch
        {
            >= 2160 => new PlaybackQualityTarget(height, 16000, 256),
            >= 1440 => new PlaybackQualityTarget(height, 10000, 256),
            >= 1080 => new PlaybackQualityTarget(height, 8000, 256),
            >= 720 => new PlaybackQualityTarget(height, 4000, 192),
            >= 480 => new PlaybackQualityTarget(height, 1500, 128),
            _ => new PlaybackQualityTarget(height, 800, 128)
        };

        public static TranscodeAcceleration Acceleration(TranscodePipeline pipeline) => Enum.Parse<TranscodeAcceleration>(pipeline.ToString());

        public static TranscodePipeline Pipeline(TranscodeAcceleration acceleration) => Enum.Parse<TranscodePipeline>(acceleration.ToString());

        // The conversion the player asked for, then one per ladder height below its picture that takes less of the budget.
        public static List<(PlaybackConversion Plan, int Weight, bool IsReduced)> Candidates(PlaybackConversion wanted, IReadOnlyList<PlaybackTrackData> tracks, IReadOnlySet<string> containers,
            IReadOnlySet<string> videoCodecs, IReadOnlySet<string> audioCodecs, int? audioStreamIndex, PlaybackQuality quality, long? bitRate, IReadOnlyList<int> ladderHeights)
        {
            var weight = PlaybackLogic.ConversionWeight(tracks, wanted);
            var candidates = new List<(PlaybackConversion Plan, int Weight, bool IsReduced)> { (wanted, weight, false) };
            if (weight == 0)
            {
                return candidates;
            }

            var height = PlaybackLogic.ConversionHeight(tracks, wanted) ?? 1080;
            foreach (var step in ladderHeights.Where(step => step < height).OrderDescending())
            {
                var target = PlaybackLogic.QualityTarget(quality, LadderStep(step));
                if (PlaybackLogic.PlanConversion(tracks, containers, videoCodecs, audioCodecs, audioStreamIndex, null, target, bitRate) is { } plan
                    && PlaybackLogic.ConversionWeight(tracks, plan) is var lighter && lighter > 0 && lighter < candidates[^1].Weight)
                {
                    candidates.Add((plan, lighter, true));
                }
            }

            return candidates;
        }

        // The chosen acceleration, else the benchmark's best, else what the configuration names, else software.
        public static TranscodeEncoding ResolveEncoding(TranscodeAcceleration chosen, TranscodeAcceleration? best, IReadOnlyList<TranscodeHardwareData> hardware,
            IReadOnlyList<string> configuredHardwareArguments, IReadOnlyList<string> configuredVideoArguments)
        {
            var acceleration = chosen == TranscodeAcceleration.Auto ? best : chosen;
            if (acceleration is not { } pipelineChoice)
            {
                return configuredVideoArguments.Count > 0 ? new TranscodeEncoding(configuredHardwareArguments, configuredVideoArguments, null) : TranscodePipelines.Software;
            }

            var pipeline = Pipeline(pipelineChoice);
            return TranscodePipelines.For(pipeline, hardware.FirstOrDefault(item => item.Pipeline == pipeline)?.Device);
        }

        // The fastest pipeline for 1080p, software when it is nearly as fast, and how many such conversions fit with headroom.
        public static TranscodeProposal? Propose(IReadOnlyList<TranscodeSpeedData> results, int cpuCores)
        {
            var speeds = results.Where(item => item.Height == 1080 && item.SpeedFactor is > 0).ToDictionary(item => item.Pipeline, item => item.SpeedFactor!.Value);
            if (speeds.Count == 0)
            {
                return null;
            }

            var best = speeds.MaxBy(item => item.Value);
            if (speeds.TryGetValue(TranscodePipeline.Software, out var software) && software >= best.Value * 0.9m)
            {
                best = new KeyValuePair<TranscodePipeline, decimal>(TranscodePipeline.Software, software);
            }

            // Software encoding shares the processor with everything else.
            var limit = best.Key == TranscodePipeline.Software ? Math.Max(1, cpuCores / 2) : HardwareMaximum;
            return new TranscodeProposal(Acceleration(best.Key), Math.Clamp((int)(best.Value / Headroom), 1, limit));
        }

        public static string ResultsJson(IEnumerable<TranscodeSpeedData> results) =>
            JsonSerializer.Serialize(results.Select(item => new { Acceleration = (int)Acceleration(item.Pipeline), item.Height, item.SpeedFactor }));

        public static async Task UpdateTranscodeSettingsAsync(this LogicContext ctx, TranscodeSettingsUpdateData data, CancellationToken ct)
        {
            // Only known ladder heights.
            if (data.LadderHeights.Any(height => !LadderChoices.Contains(height)))
            {
                throw new DataValidationException(nameof(data.LadderHeights), "out_of_range");
            }

            // Reject concurrent edits.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "TranscodeSettings" AS settings
                SET
                    "TranscodeAccelerationTypeId" = @TranscodeAccelerationTypeId,
                    "MaxTranscodes" = @MaxTranscodes,
                    "LadderHeights" = @LadderHeights,
                    "Version" = settings."Version" + 1
                WHERE
                    settings."Id" = 1
                    AND settings."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }

        public static async Task SaveTranscodeBenchmarkAsync(this LogicContext ctx, TranscodeBenchmarkSaveData data, CancellationToken ct)
        {
            // A new run replaces the last one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "TranscodeBenchmarks" ("Id", "RanAt", "CpuCores", "BestTranscodeAccelerationTypeId", "RecommendedMaxTranscodes")
                VALUES
                    (1, now(), @CpuCores, @BestTranscodeAccelerationTypeId, @RecommendedMaxTranscodes)
                ON CONFLICT ("Id") DO UPDATE
                SET
                    "RanAt" = EXCLUDED."RanAt",
                    "CpuCores" = EXCLUDED."CpuCores",
                    "BestTranscodeAccelerationTypeId" = EXCLUDED."BestTranscodeAccelerationTypeId",
                    "RecommendedMaxTranscodes" = EXCLUDED."RecommendedMaxTranscodes";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "TranscodeBenchmarkResults";
                """;
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "TranscodeBenchmarkResults" ("TranscodeBenchmarkId", "TranscodeAccelerationTypeId", "Height", "SpeedFactor")
                SELECT
                    1,
                    item."Acceleration",
                    item."Height",
                    item."SpeedFactor"
                FROM
                    jsonb_to_recordset(@Results::jsonb) AS item ("Acceleration" smallint, "Height" integer, "SpeedFactor" numeric);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
