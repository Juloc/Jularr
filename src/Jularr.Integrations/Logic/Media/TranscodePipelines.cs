using Jularr.Integrations.Data.Media;

namespace Jularr.Integrations.Logic.Media
{
    // The FFmpeg arguments of every way to convert video. Hardware pipelines keep decoded pictures in the device's memory and scale there.
    internal static class TranscodePipelines
    {
        public const string SoftwareScale = "scale=-2:min(ih\\,{height})";

        public static TranscodeEncoding Software { get; } = new([], ["-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p"], SoftwareScale);

        // The encoder FFmpeg lists for a pipeline and the hardware acceleration it decodes with.
        public static (string Encoder, string? Acceleration) Names(TranscodePipeline pipeline) => pipeline switch
        {
            TranscodePipeline.Vaapi => ("h264_vaapi", "vaapi"),
            TranscodePipeline.Qsv => ("h264_qsv", "qsv"),
            TranscodePipeline.Nvenc => ("h264_nvenc", "cuda"),
            TranscodePipeline.VideoToolbox => ("h264_videotoolbox", "videotoolbox"),
            _ => ("libx264", null)
        };

        // A device is the render node of VAAPI and Quick Sync.
        public static TranscodeEncoding For(TranscodePipeline pipeline, string? device) => pipeline switch
        {
            TranscodePipeline.Vaapi => new(["-hwaccel", "vaapi", "-hwaccel_device", device ?? "/dev/dri/renderD128", "-hwaccel_output_format", "vaapi"],
                ["-c:v", "h264_vaapi", "-qp", "23"], "scale_vaapi=w=-2:h=min(ih\\,{height}):format=nv12"),
            TranscodePipeline.Qsv => new(["-hwaccel", "qsv", "-qsv_device", device ?? "/dev/dri/renderD128", "-hwaccel_output_format", "qsv"],
                ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23"], "vpp_qsv=w=-2:h=min(ih\\,{height})"),
            TranscodePipeline.Nvenc => new(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"], ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23"], "scale_cuda=-2:min(ih\\,{height})"),
            TranscodePipeline.VideoToolbox => new(["-hwaccel", "videotoolbox"], ["-c:v", "h264_videotoolbox", "-q:v", "60", "-pix_fmt", "yuv420p"], SoftwareScale),
            _ => Software
        };
    }
}
