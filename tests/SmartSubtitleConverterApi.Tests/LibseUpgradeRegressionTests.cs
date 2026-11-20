using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using SmartSubtitleConverterApi.Conversion;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>
/// Pinned behaviour checked on the libse 5.1 → 5.4 upgrade: encoding detection of legacy ANSI files (5.4's release notes
/// mention Western ANSI files detected as Shift_JIS, fixed only in 5.5) and the SubRip reader rewrite.
/// </summary>
public class LibseUpgradeRegressionTests
{
    public LibseUpgradeRegressionTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string Srt(params string[] lines) =>
        string.Concat(lines.Select((l, i) => $"{i + 1}\r\n00:00:0{i + 1},000 --> 00:00:0{i + 1},900\r\n{l}\r\n\r\n"));

    [Theory]
    [InlineData(1252, "windows-1252", "Ça va très bien, merci.", "Où est la gare, s'il vous plaît?", "Die Straße ist schön und grün.")]
    [InlineData(1252, "windows-1252", "¿Qué pasa aquí, señor?", "Él está en la ciudad, ¿verdad?", "Mañana vamos a la playa.")]
    [InlineData(1253, "windows-1253", "Καλημέρα, τι κάνεις;", "Δεν ξέρω γιατί είναι εδώ.", "Ευχαριστώ πολύ για όλα.")]
    [InlineData(1251, "windows-1251", "Привет, как дела?", "Я не знаю, что это такое.", "Мы были здесь только вчера.")]
    public void Legacy_ansi_text_is_decoded_with_its_own_code_page(int codePage, string expected, string a, string b, string c)
    {
        var srt = Srt(a, b, c);
        var decoded = Encodings.Decode(Encoding.GetEncoding(codePage).GetBytes(srt), null, null);
        Assert.Equal(expected, decoded.EncodingName);
        Assert.Equal(srt, decoded.Text);
    }

    [Fact]
    public void Western_ansi_without_dictionary_words_falls_back_to_a_western_code_page_not_shift_jis()
    {
        // No dictionary words, so the probe can't decide and libse's ANSI heuristic picks (reported as "heuristic").
        var srt = Srt("Xyzzé Qwrtè Plöx", "Brrà Zzük Vvé", "Ççç Ñññ Ååå");
        var decoded = Encodings.Decode(Encoding.GetEncoding(1252).GetBytes(srt), null, null);
        Assert.DoesNotContain("shift", decoded.EncodingName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("932", decoded.EncodingName);
    }

    [Fact]
    public void Czech_text_read_as_greek_pac_doesnt_score_as_greek()
    {
        // libse 5.4 added Greek "το", "σε", "με" to its word list; PAC's Greek table maps Latin "to", "se", "me" onto
        // them, so Czech PAC text decoded as Greek used to tie with the real Czech decode (ambiguous-encoding).
        var greekView = "Κά ξεφίν, γο σε το ταδω δεκε.\nRελξι νι, zε κε το πςαφδα.\nΤο ξεξί νοzξέ, γτωςι ςελω υz τεδβ.";
        var score = TextProbe.Score(new ProbeCandidate("greek", greekView, ["el"], 1));
        Assert.Equal(0, score.Hits);
        var realGreek = TextProbe.Score(new ProbeCandidate("greek", "Καλημέρα, τι κάνεις;\nΔεν ξέρω γιατί είναι εδώ.", ["el"], 1));
        Assert.True(realGreek.Hits >= 2);
    }

    [Fact]
    public void Srt_reader_keeps_well_formed_cues_and_text_exactly()
    {
        var sub = new Subtitle();
        var srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i>\r\nsecond line\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n- Dash\r\n- Dialog\r\n";
        new Nikse.SubtitleEdit.Core.SubtitleFormats.SubRip().LoadSubtitle(sub, srt.Split("\r\n").ToList(), null);
        Assert.Equal(2, sub.Paragraphs.Count);
        Assert.Equal("<i>Hello</i>" + Environment.NewLine + "second line", sub.Paragraphs[0].Text);
        Assert.Equal(2500, sub.Paragraphs[0].EndTime.TotalMilliseconds);
        Assert.Equal("- Dash" + Environment.NewLine + "- Dialog", sub.Paragraphs[1].Text);
    }
}
