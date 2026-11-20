using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>/healthz and /readyz revision: the build's git commit, shortened; anything else is "unknown".</summary>
public class RevisionTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456")]
    [InlineData("ABCDEF1", "abcdef1")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("unknown", "unknown")]
    [InlineData("abc12", "unknown")]          // too short to be a commit
    [InlineData("abc123g", "unknown")]        // not hex
    [InlineData("abc1234; rm -rf /", "unknown")]
    public void Short_revision(string? value, string expected) => Assert.Equal(expected, LibseInfo.ShortRevision(value));
}
