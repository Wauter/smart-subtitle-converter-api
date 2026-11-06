using System.Text;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Tests;

/// <summary>Direct probe-shape checks (InternalsVisibleTo) for ambiguous-encoding candidates/samples.</summary>
public class EncodingProbeShapeTests
{
    public EncodingProbeShapeTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void Distinctive_script_drops_latin_rival_for_hebrew_bytes()
    {
        const string hebrew =
            "1\r\n00:00:01,000 --> 00:00:02,500\r\nזה לא מה שאני רוצה\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nאבל יש לי את כל הזמן\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nגם היא לא כאן\r\n";
        var bytes = Encoding.GetEncoding(1255).GetBytes(hebrew);
        var decoded = Encodings.ProbeAnsi(bytes);
        Assert.NotNull(decoded);
        Assert.Equal("windows-1255", decoded!.EncodingName);
        Assert.Equal("probed", decoded.Confidence);
    }

    [Fact]
    public void Cue_sample_skips_indices_and_arrows()
    {
        var sample = TextProbe.CueSample("1\n00:00:01,000 --> 00:00:02,000\nHello there\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond cue\n");
        Assert.Contains("Hello there", sample);
        Assert.Contains("Second cue", sample);
        Assert.DoesNotContain("-->", sample);
    }

    [Fact]
    public void Probe_conflict_candidates_include_samples()
    {
        // Two strong Latin candidates with different text → Conflict with object candidates.
        var a = new ProbeCandidate("windows-1252",
            "1\n00:00:01,000 --> 00:00:02,000\nOù êtes-vous? Je ne sais pas, c'est très bien.\n\n2\n00:00:03,000 --> 00:00:04,000\nWhat are you doing here now?\n",
            ["en", "fr", "es", "de", "it", "nl", "pt"], 0);
        var b = new ProbeCandidate("windows-1250",
            "1\n00:00:01,000 --> 00:00:02,000\nJá nevím, co se to tady děje.\n\n2\n00:00:03,000 --> 00:00:04,000\nŘekni mi, že je to pravda teď.\n",
            ["cs", "sk", "pl", "hu"], 1);
        var scores = new List<ProbeScore> { TextProbe.Score(a), TextProbe.Score(b) };
        Assert.True(scores[0].Hits >= 1 && scores[1].Hits >= 1, string.Join(", ", scores.Select(s => $"{s.Name}:{s.Hits}")));
        var outcome = TextProbe.Decide(scores, minHits: 1);
        Assert.Equal(ProbeVerdict.Conflict, outcome.Verdict);

        // Mirror Encodings.ProbeAnsi conflict payload construction.
        var details = outcome.Matches.Select(m => new Dictionary<string, object?>
        {
            ["encoding"] = m.Name,
            ["language"] = m.Language,
            ["hits"] = m.Hits,
            ["sample"] = TextProbe.CueSample(m.Candidate.Text),
        }).ToList();
        Assert.Equal(2, details.Count);
        Assert.All(details, d => Assert.False(string.IsNullOrEmpty((string?)d["sample"])));
    }

    [Fact]
    public void ProbeAnsi_throws_ambiguous_encoding_with_object_candidates_on_forced_conflict()
    {
        // Build a payload whose 1253 and 1256 decodes both form distinctive-script words with different text:
        // concatenate a Greek SRT and an Arabic SRT in their native code pages.
        const string greek =
            "1\r\n00:00:01,000 --> 00:00:02,500\r\nΚαλημέρα κόσμε, τι κάνεις;\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nΔεν είναι εδώ τώρα.\r\nΓιατί όχι;\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nΑυτό είναι για μένα και για σένα.\r\n";
        const string arabic =
            "1\r\n00:00:01,000 --> 00:00:02,500\r\nهل أنت هنا؟ لا أعرف ما هذا\r\n\r\n2\r\n00:00:03,000 --> 00:00:05,000\r\nأنا في البيت مع كل الناس\r\n\r\n3\r\n00:00:06,000 --> 00:00:08,000\r\nلقد كان هذا من أجلك\r\n";
        var g = Encoding.GetEncoding(1253).GetBytes(greek);
        var a = Encoding.GetEncoding(1256).GetBytes(arabic);
        var mixed = new byte[g.Length + a.Length];
        Buffer.BlockCopy(g, 0, mixed, 0, g.Length);
        Buffer.BlockCopy(a, 0, mixed, g.Length, a.Length);

        var ex = Assert.Throws<ApiException>(() => Encodings.ProbeAnsi(mixed));
        Assert.Equal(422, ex.Status);
        Assert.Equal(ErrorCodes.AmbiguousEncoding, ex.Code);
        Assert.Equal("text-encoding", ex.Extensions["probe"]);
        var candidates = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ex.Extensions["candidates"]);
        var list = candidates.Cast<object>().ToList();
        Assert.True(list.Count >= 2, $"candidates={list.Count}");
        foreach (var item in list)
        {
            var d = Assert.IsType<Dictionary<string, object?>>(item);
            Assert.False(string.IsNullOrEmpty(d["encoding"] as string));
            Assert.False(string.IsNullOrEmpty(d["sample"] as string));
            Assert.True(d["hits"] is int and >= 1);
        }
        Assert.Contains(ex.Errors, e => e.Pointer == "/encoding/input");
        Assert.Contains(ex.Errors, e => e.Pointer == "/encoding/language");
    }
}
