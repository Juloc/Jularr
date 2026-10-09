using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Subtitles;

namespace Jularr.Tests;

[TestClass]
public sealed class SubtitleParserTests
{
    [TestMethod]
    public void PresentationMetadataTravelsThroughClientSubtitleCueMapping()
    {
        var authored = new SubtitleCueData(1000, 2000, "Sign",
            new SubtitleCuePresentation(7, 25, 10, 3, "Noto Sans", 24, true, false, "#0099FF"));
        var converted = ClientApiMappings.ToClientEmbeddedSubtitleCues(
            new PlaybackEmbeddedSubtitleCues("stream:3", "ja", [authored]));

        Assert.AreEqual("Sign", converted.Cues.Single().Text);
        Assert.AreEqual(7, converted.Cues.Single().Presentation?.Alignment);
        Assert.AreEqual(25d, converted.Cues.Single().Presentation?.XPercent);
        Assert.AreEqual(3, converted.Cues.Single().Presentation?.Layer);
    }

    [TestMethod]
    public void ParsesSrtAndNormalizesFormatting()
    {
        const string content = """
            1
            00:00:01,250 --> 00:00:03,500
            <i>何してるの？</i>

            2
            00:00:04,000 --> 00:00:05,200
            まだ諦めてない。
            """;

        var cues = SubtitleParser.ParseSrt(content);

        Assert.AreEqual(2, cues.Count);
        Assert.AreEqual(1250, cues[0].StartMs);
        Assert.AreEqual("何してるの？", cues[0].Text);
    }

    [TestMethod]
    public void ParsesAssDialogueUsingDeclaredFormat()
    {
        const string content = """
            [Script Info]
            Title: Example

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.20,0:00:03.40,Default,,0,0,0,,{\i1}学校に行く{\i0}\N今から
            """;

        var cues = SubtitleParser.ParseAss(content);

        Assert.AreEqual(1, cues.Count);
        Assert.AreEqual(1200, cues[0].StartMs);
        Assert.AreEqual("学校に行く 今から", cues[0].Text);
    }

    [TestMethod]
    [DataRow(@"Word\h\hword", "Word\u00A0\u00A0word")]
    [DataRow(@"Hello\hthere\NNext\hline", "Hello\u00A0there Next\u00A0line")]
    [DataRow(@"{\i1}First\hsecond{\i0}", "First\u00A0second")]
    [DataRow("path /home", "path /home")]
    public void ParsesAssHardSpacesWithoutShowingEscapeCodes(string source, string expected)
    {
        var content = $"""
            [Events]
            Format: Start, End, Text
            Dialogue: 0:00:01.00,0:00:02.00,{source}
            """;

        var cue = SubtitleParser.ParseAss(content).Single();

        Assert.AreEqual(expected, cue.Text);
    }

    [TestMethod]
    public void AssHardSpacesWithoutDialogueDoNotCreateCues()
    {
        const string content = """
            [Events]
            Format: Start, End, Text
            Dialogue: 0:00:01.00,0:00:02.00,\h\h
            """;

        Assert.AreEqual(0, SubtitleParser.ParseAss(content).Count);
    }

    [TestMethod]
    public void ParsesSsaV4DialogueThroughFormatDispatch()
    {
        const string content = """
            [Script Info]
            ScriptType: v4.00

            [Events]
            Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: Marked=0,0:01:02.50,0:01:04.00,Default,,0000,0000,0000,,猫が走る
            """;

        var cues = SubtitleParser.ParseFormat("ssa", content);

        Assert.AreEqual(1, cues.Count);
        Assert.AreEqual(62500, cues[0].StartMs);
        Assert.AreEqual(64000, cues[0].EndMs);
        Assert.AreEqual("猫が走る", cues[0].Text);
    }

    [TestMethod]
    public void AssLayoutPreservesSignsPositionAndDialogueStyle()
    {
        const string content = """
            [Script Info]
            PlayResX: 640
            PlayResY: 360

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, Bold, Italic, Alignment
            Style: Signs, Noto Sans, 24, &H00FF9900, -1, 0, 8
            Style: Dialogue, Arial, 28, &H00FFFFFF, 0, 1, 2

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 3,0:00:01.00,0:00:04.00,Signs,,0,0,0,,{\an7\pos(160,36)}駅前
            Dialogue: 0,0:00:02.00,0:00:04.00,Dialogue,,0,0,0,,{\i1}ここで待って
            """;

        var cues = SubtitleParser.ParseAss(content);

        Assert.AreEqual(2, cues.Count);
        var sign = cues[0].Presentation!;
        Assert.AreEqual("駅前", cues[0].Text);
        Assert.AreEqual(7, sign.Alignment);
        Assert.AreEqual(25d, sign.XPercent);
        Assert.AreEqual(10d, sign.YPercent);
        Assert.AreEqual(3, sign.Layer);
        Assert.AreEqual("Noto Sans", sign.FontFamily);
        Assert.AreEqual(24d, sign.FontSize);
        Assert.AreEqual(true, sign.Bold);
        Assert.AreEqual("#0099FF", sign.Color);

        var dialogue = cues[1].Presentation!;
        Assert.AreEqual(2, dialogue.Alignment);
        Assert.IsNull(dialogue.XPercent);
        Assert.AreEqual(0, dialogue.Layer);
        Assert.AreEqual(true, dialogue.Italic);
        Assert.AreEqual("ここで待って", cues[1].Text);
    }

    [DataTestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(3, 3)]
    [DataRow(5, 7)]
    [DataRow(6, 8)]
    [DataRow(7, 9)]
    [DataRow(9, 4)]
    [DataRow(10, 5)]
    [DataRow(11, 6)]
    public void LegacySsaStyleAlignment_MapsToAssNumpadPosition(int legacy, int expected)
    {
        var content = """
            [V4 Styles]
            Format: Name, Alignment
            Style: Signs, {ALIGNMENT}

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Signs,,0,0,0,,Station sign
            """.Replace("{ALIGNMENT}", legacy.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var cue = SubtitleParser.ParseFormat("ssa", content).Single();

        Assert.AreEqual("Station sign", cue.Text);
        Assert.AreEqual(expected, cue.Presentation?.Alignment);
    }

    [DataTestMethod]
    [DataRow(5, 7)]
    [DataRow(6, 8)]
    [DataRow(7, 9)]
    [DataRow(9, 4)]
    [DataRow(10, 5)]
    [DataRow(11, 6)]
    public void LegacySsaAlignmentTag_OverridesModernStyle(int legacy, int expected)
    {
        const string header = """
            [V4+ Styles]
            Format: Name, Alignment
            Style: Signs, 2

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

            """;
        var content = header + $@"Dialogue: 0,0:00:01.00,0:00:02.00,Signs,,0,0,0,,{{\a{legacy}}}Sign";
        var cue = SubtitleParser.ParseAss(content).Single();

        Assert.AreEqual(expected, cue.Presentation?.Alignment);
    }

    [TestMethod]
    public void MixedSsaAndAssAlignmentTags_LastOverrideWins()
    {
        const string content = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Signs,,0,0,0,,{\a10\an9}Top right
            """;

        var cue = SubtitleParser.ParseAss(content).Single();

        Assert.AreEqual("Top right", cue.Text);
        Assert.AreEqual(9, cue.Presentation?.Alignment);
    }

    [TestMethod]
    public void AssPositionWithoutCanvasCoordinatesFallsBackToAlignment()
    {
        const string content = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Signs,,0,0,0,,{\an8\pos(100,10)}Title
            """;
        var cue = SubtitleParser.ParseAss(content).Single();

        Assert.AreEqual("Title", cue.Text);
        Assert.AreEqual(8, cue.Presentation?.Alignment);
        Assert.IsNull(cue.Presentation?.XPercent);
        Assert.IsNull(cue.Presentation?.YPercent);
    }

    [TestMethod]
    public void ParsesVttCuesSkippingMetadataBlocksAndRubyText()
    {
        const string content = """
            WEBVTT - Japanese

            NOTE Generated by a test

            STYLE
            ::cue { color: white; }

            intro
            00:01.250 --> 00:03.500 align:start position:10%
            <v 先生><ruby>学校<rt>がっこう</rt></ruby>に行く</v>
            &lt;今から&gt;

            01:00:04.000 --> 01:00:05.200
            <c.yellow>まだ</c>諦めてない&amp;
            """;

        var cues = SubtitleParser.ParseFormat(".vtt", content);

        Assert.AreEqual(2, cues.Count);
        Assert.AreEqual(1250, cues[0].StartMs);
        Assert.AreEqual(3500, cues[0].EndMs);
        Assert.AreEqual("学校に行く <今から>", cues[0].Text);
        Assert.AreEqual(3604000, cues[1].StartMs);
        Assert.AreEqual(3605200, cues[1].EndMs);
        Assert.AreEqual("まだ諦めてない&", cues[1].Text);
    }
}
