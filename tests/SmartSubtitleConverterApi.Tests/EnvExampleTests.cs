using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SmartSubtitleConverterApi.Infrastructure;
namespace SmartSubtitleConverterApi.Tests;

/// <summary>.env.example shows defaults uncommented, so it must keep matching the code and docker-compose.yml.</summary>
public sealed class EnvExampleTests
{
    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, name)) && File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return Path.Combine(dir.FullName, name);
        throw new FileNotFoundException(name);
    }

    private static Dictionary<string, string> ActiveSettings()
    {
        var map = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(RepoFile(".env.example")))
        {
            var m = Regex.Match(line, @"^([A-Z][A-Z0-9_]*)=(.*)$");
            if (m.Success) Assert.True(map.TryAdd(m.Groups[1].Value, m.Groups[2].Value), $"{m.Groups[1].Value} appears twice as an active line in .env.example");
        }
        return map;
    }

    private static object? Normalize(object? v) => v switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        string[] a => string.Join("|", a),
        _ => v,
    };

    [Fact]
    public void Active_lines_equal_the_built_in_defaults()
    {
        var env = ActiveSettings();
        var fromFile = ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(env!).Build());
        var defaults = ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());
        // ALLOWED_HOSTS is a Compose default (the service name), not a code default.
        var skip = new HashSet<string> { nameof(ApiOptions.AllowedHosts) };
        foreach (var p in typeof(ApiOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !skip.Contains(p.Name)))
            Assert.True(Equals(Normalize(p.GetValue(defaults)), Normalize(p.GetValue(fromFile))),
                $".env.example sets {p.Name} to {Normalize(p.GetValue(fromFile)) ?? "(empty)"}, but the built-in default is {Normalize(p.GetValue(defaults)) ?? "(empty)"}");
        Assert.Equal("smart-subtitle-converter-api", env["ALLOWED_HOSTS"]);
    }

    [Fact]
    public void Compose_only_defaults_match_docker_compose()
    {
        var env = ActiveSettings();
        var compose = File.ReadAllText(RepoFile("docker-compose.yml"));
        foreach (var key in new[] { "API_IMAGE", "PUBLISH_ADDRESS", "PUBLISH_PORT", "ALLOWED_HOSTS" })
        {
            var m = Regex.Match(compose, @"\$\{" + key + @":-([^}]*)\}");
            Assert.True(m.Success, $"docker-compose.yml has no ${{{key}:-default}}");
            Assert.Equal(m.Groups[1].Value, env[key]);
        }
    }
}
