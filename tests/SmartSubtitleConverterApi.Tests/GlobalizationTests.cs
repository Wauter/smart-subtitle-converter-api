using SmartSubtitleConverterApi.Endpoints;

namespace SmartSubtitleConverterApi.Tests;

/// <summary>The probes behind the /readyz "globalization" check must pass wherever the API runs.</summary>
public class GlobalizationTests
{
    [Fact]
    public void Globalization_probes_pass() => Assert.Empty(GlobalizationCheck.Run());
}
