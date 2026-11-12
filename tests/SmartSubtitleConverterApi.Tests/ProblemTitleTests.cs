using System.Net.Http.Json;
using System.Reflection;
using SmartSubtitleConverterApi.Infrastructure;
namespace SmartSubtitleConverterApi.Tests;

/// QA p7/p8: every problem code has its own title; only internal-error (and unknown codes) say "Internal error".
public sealed class ProblemTitleTests
{
    [Theory]
    [InlineData("job-not-found", "Job not found")]
    [InlineData("job-input-not-stored", "Job input not stored")]
    [InlineData("job-output-not-stored", "Job output not stored")]
    [InlineData("job-not-ready", "Job not ready")]
    [InlineData("job-file-gone", "Job file gone")]
    [InlineData("job-location-not-replayable", "Job location not replayable")]
    [InlineData("storage-location-rejected", "Storage location rejected")]
    [InlineData("storage-not-found", "Storage file not found")]
    [InlineData("storage-io-failed", "Storage I/O failed")]
    [InlineData("output-exists", "Output exists")]
    [InlineData("internal-error", "Internal error")]
    public void Job_and_storage_codes_have_their_own_titles(string code, string title) => Assert.Equal(title, ErrorCodes.Title(code));

    [Fact]
    public void Every_declared_code_except_internal_error_has_a_specific_title()
    {
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(codes);
        foreach (var code in codes.Where(c => c != ErrorCodes.InternalError))
            Assert.True(ErrorCodes.Title(code) != "Internal error", $"{code} has no title of its own");
    }

    [Fact]
    public async Task Missing_storage_input_is_storage_not_found_with_its_title()
    {
        var root = Path.Combine(Path.GetTempPath(), "titles-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            using var f = ApiFactory.With(new() { ["STORAGE_FILE_ROOTS"] = root });
            var r = await f.CreateClient().PostAsJsonAsync("/v1/convert/json", new { input = Path.Combine(root, "missing.srt"), target = "vtt" });
            Assert.Equal(404, (int)r.StatusCode);
            var p = await r.Json();
            Assert.Equal("storage-not-found", (string)p["code"]!); Assert.Equal("Storage file not found", (string)p["title"]!);
        }
        finally { Directory.Delete(root, true); }
    }
}
