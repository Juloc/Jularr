namespace Jularr.Integrations.Data.Media
{
    internal sealed class TranscodeRequest
    {
        public TranscodeRequest(string inputPath, string outputDirectory, int startSegment, int segmentSeconds, int? videoStreamIndex, int? audioStreamIndex, bool copyVideo,
            bool copyAudio, IReadOnlyList<string> videoEncoderArguments, IReadOnlyList<string> hardwareAccelerationArguments)
        {
            InputPath = inputPath;
            OutputDirectory = outputDirectory;
            StartSegment = startSegment;
            SegmentSeconds = segmentSeconds;
            VideoStreamIndex = videoStreamIndex;
            AudioStreamIndex = audioStreamIndex;
            CopyVideo = copyVideo;
            CopyAudio = copyAudio;
            VideoEncoderArguments = videoEncoderArguments;
            HardwareAccelerationArguments = hardwareAccelerationArguments;
        }

        public string InputPath { get; }
        public string OutputDirectory { get; }
        public int StartSegment { get; }
        public int SegmentSeconds { get; }
        public int? VideoStreamIndex { get; }
        public int? AudioStreamIndex { get; }
        public bool CopyVideo { get; }
        public bool CopyAudio { get; }
        public IReadOnlyList<string> VideoEncoderArguments { get; }
        public IReadOnlyList<string> HardwareAccelerationArguments { get; }

        // Converted audio's rate in kilobits a second.
        public int AudioKilobits { get; init; } = 192;

        // Converted video's largest height and rate in kilobits a second; none is no cap.
        public int? MaxHeight { get; init; }
        public int? VideoKilobits { get; init; }
    }
}
