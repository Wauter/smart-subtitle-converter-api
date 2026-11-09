using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>One startup diagnostic line. Warnings are counted in the closing <c>storage.check done</c> summary.</summary>
public sealed record StorageFinding(bool Warning, string Line);

/// <summary>
/// Pure, offline checks of the storage-related environment: an effective-configuration summary plus warnings for
/// missing or malformed values. Every warning names the variable, the value seen (secrets masked), the problem and a
/// concrete fix. Secrets (AWS key id, secret key, session token) are only ever printed through <see cref="Mask"/>.
/// </summary>
public static partial class StorageDiagnostics
{
    public static readonly string[] SecretVars = ["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"];
    public const string PermissionsDoc = "docs/deployment.md#s3-permissions";

    /// <summary>First 3 chars + <c>***</c> + last 4; values shorter than 10 chars become <c>***</c>. Null stays unset.</summary>
    public static string Mask(string? secret) => secret == null ? "<unset>"
        : secret.Length < 10 ? "***" : secret[..3] + "***" + secret[^4..];

    /// <summary>Quotes a value for a log line and makes whitespace, control characters and quotes visible.</summary>
    public static string Q(string? value)
    {
        if (value == null) return "<unset>";
        var sb = new StringBuilder("\"");
        foreach (var ch in value.Length > 300 ? value[..300] + "…" : value)
            sb.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", _ when char.IsControl(ch) => $"\\u{(int)ch:x4}", _ => ch.ToString() });
        return sb.Append('"').ToString();
    }

    /// <summary>Replaces any occurrence of a configured secret with its mask and flattens to one line (for SDK messages).</summary>
    public static string Scrub(string text, Func<string, string?> env)
    {
        foreach (var name in SecretVars)
            if (env(name) is { Length: > 0 } secret)
            {
                text = text.Replace(secret, Mask(secret), StringComparison.Ordinal);
                if (secret.Trim() is { Length: > 0 } trimmed && trimmed != secret) text = text.Replace(trimmed, Mask(trimmed), StringComparison.Ordinal);
            }
        text = Regex.Replace(text, @"\s*[\r\n]+\s*", "; ").Trim(' ', ';');
        return text.Length > 500 ? text[..500] + "…" : text;
    }

    public static string Warn(string prefix, string code, string var, string shown, string problem, string fix)
        => $"{prefix} warning={code} var={var} value={shown} problem={Q(problem)} fix={Q(fix)}";

    /// <summary>AWS general-purpose bucket naming rule; returns null when valid, otherwise the specific reason.</summary>
    public static string? BucketNameProblem(string name)
    {
        if (name.Length is < 3 or > 63) return $"bucket names are 3-63 characters long (this one has {name.Length})";
        if (name.Any(char.IsAsciiLetterUpper)) return "bucket names are lowercase only (the API matches names exactly; S3 URIs are lowercased)";
        if (!name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-'))
            return "bucket names may contain only lowercase letters, digits, '.' and '-'";
        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1])) return "bucket names must start and end with a letter or digit";
        if (name.Contains("..")) return "bucket names must not contain two adjacent periods";
        if (IPAddress.TryParse(name, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return "bucket names must not be formatted as an IP address";
        return null;
    }

    [GeneratedRegex("^(AKIA|ASIA)[A-Z0-9]{16}$")] private static partial Regex AwsKeyId();
    [GeneratedRegex("^[a-z]{2}(-[a-z]+)+-[0-9]+$")] private static partial Regex RegionShape();

    /// <summary>Builds the effective-configuration summary and all offline warnings.</summary>
    public static List<StorageFinding> Inspect(ApiOptions opts, Func<string, string?> env)
    {
        var f = new List<StorageFinding>();
        void Info(string line) => f.Add(new(false, line));
        void W(string code, string var, string shown, string problem, string fix) => f.Add(new(true, Warn("storage.config", code, var, shown, problem, fix)));
        bool s3 = opts.S3AllowedBuckets.Length > 0, aws = s3 && string.IsNullOrWhiteSpace(opts.S3Endpoint);

        // S3 allowlists.
        var bucketsRaw = env("S3_ALLOWED_BUCKETS");
        Info($"storage.config var=S3_ALLOWED_BUCKETS value={Q(bucketsRaw)} effective={(s3 ? Q(string.Join(',', opts.S3AllowedBuckets)) : "\"(s3 off)\"")}");
        foreach (var b in opts.S3AllowedBuckets)
        {
            var bare = b.Trim('"', '\'');
            if (b.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            {
                var rest = b[5..]; var slash = rest.IndexOf('/');
                W("bucket-has-scheme", "S3_ALLOWED_BUCKETS", Q(b), "entries must be bare bucket names, not s3:// URIs",
                    $"use S3_ALLOWED_BUCKETS={(slash < 0 ? rest : rest[..slash])}" + (slash >= 0 && slash < rest.Length - 1 ? $" and S3_ALLOWED_PREFIXES={rest[(slash + 1)..].TrimEnd('/')}/" : ""));
            }
            else if (b.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
                W("bucket-is-arn", "S3_ALLOWED_BUCKETS", Q(b), "entries must be bucket names, not ARNs", $"use S3_ALLOWED_BUCKETS={b.Split(':').Last().Split('/')[0]}");
            else if (bare.Contains('/'))
                W("bucket-has-path", "S3_ALLOWED_BUCKETS", Q(b), "entries must be bucket names only; a key path does not belong here",
                    $"use S3_ALLOWED_BUCKETS={bare.Split('/')[0]} and put the path in S3_ALLOWED_PREFIXES={bare[(bare.IndexOf('/') + 1)..].TrimEnd('/')}/");
            else if (BucketNameProblem(b) is { } why)
                W("invalid-bucket-name", "S3_ALLOWED_BUCKETS", Q(b), why + "; requests for this bucket are rejected",
                    "use the exact bucket name as shown in the S3 console (3-63 chars, lowercase letters, digits, '.' and '-', starting and ending with a letter or digit)");
        }
        var prefixesRaw = env("S3_ALLOWED_PREFIXES");
        Info($"storage.config var=S3_ALLOWED_PREFIXES value={Q(prefixesRaw)} effective={(opts.S3AllowedPrefixes.Length == 0 ? "\"(whole bucket)\"" : Q(string.Join(',', opts.S3AllowedPrefixes)))}");
        if (!s3 && opts.S3AllowedPrefixes.Length > 0)
            W("prefixes-without-buckets", "S3_ALLOWED_PREFIXES", Q(prefixesRaw), "prefixes are set but S3_ALLOWED_BUCKETS is empty, so S3 is off", "set S3_ALLOWED_BUCKETS=<bucket> or unset S3_ALLOWED_PREFIXES");
        foreach (var p in opts.S3AllowedPrefixes)
        {
            if (p.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
                W("prefix-has-scheme", "S3_ALLOWED_PREFIXES", Q(p), "prefixes are object-key prefixes, not s3:// URIs", "use only the key part, e.g. S3_ALLOWED_PREFIXES=in/,out/");
            else if (p.StartsWith('/'))
                W("prefix-leading-slash", "S3_ALLOWED_PREFIXES", Q(p), "S3 keys never start with '/', so this prefix matches nothing", $"use S3_ALLOWED_PREFIXES={p.TrimStart('/')}");
            else if (p.Contains('*'))
                W("prefix-wildcard", "S3_ALLOWED_PREFIXES", Q(p), "prefixes are literal; '*' is not a wildcard", $"use the literal prefix, e.g. {p[..p.IndexOf('*')]}");
        }

        // Region and endpoint.
        var regionRaw = env("S3_REGION");
        var awsRegion = env("AWS_REGION") ?? env("AWS_DEFAULT_REGION");
        Info($"storage.config var=S3_REGION value={Q(regionRaw)} effective={Q(opts.S3Region)}{(regionRaw == null ? " (default)" : "")}");
        if (aws && regionRaw == null)
            W("region-missing", "S3_REGION", "<unset>", "S3_REGION is not set, so requests are signed for and sent to us-east-1; a bucket in another region answers 301 PermanentRedirect",
                awsRegion != null ? $"AWS_REGION/AWS_DEFAULT_REGION={Q(awsRegion)} is set, but this API reads S3_REGION: set S3_REGION={awsRegion.Trim()}"
                    : "set S3_REGION to the bucket's region (S3 console > bucket > Properties, or: aws s3api get-bucket-location --bucket <bucket>); set S3_REGION=us-east-1 explicitly to silence this");
        else if (aws && !RegionEndpoint.EnumerableAllRegions.Any(r => r.SystemName == opts.S3Region))
            W("region-unknown", "S3_REGION", Q(regionRaw), RegionShape().IsMatch(opts.S3Region.Trim()) ? "not a region this SDK knows (fine only if it is a brand-new AWS region)" : "does not look like an AWS region code (expected e.g. eu-west-1)",
                "set S3_REGION to the bucket's region code, e.g. S3_REGION=eu-west-1 (not the display name such as 'Europe (Ireland)')");
        else if (aws && awsRegion != null && awsRegion.Trim() != opts.S3Region)
            f.Add(new(false, $"storage.config note=region-differs var=S3_REGION value={Q(regionRaw)} detail={Q($"AWS_REGION/AWS_DEFAULT_REGION is {awsRegion.Trim()}; this API uses S3_REGION for S3")}"));

        var endpoint = env("S3_ENDPOINT");
        Info($"storage.config var=S3_ENDPOINT value={Q(endpoint)} effective={(string.IsNullOrWhiteSpace(endpoint) ? Q($"AWS S3 regional endpoint for {opts.S3Region}") : Q(endpoint))}");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var e = endpoint.Trim().Trim('"', '\'');
            if (!e.Contains("://"))
                W("endpoint-no-scheme", "S3_ENDPOINT", Q(endpoint), "the endpoint has no scheme", $"include http:// or https://, e.g. S3_ENDPOINT=http://{e.TrimEnd('/')}");
            else if (!Uri.TryCreate(e, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0)
                W("endpoint-invalid", "S3_ENDPOINT", Q(endpoint), "not a valid http(s) URL without query, fragment or user info", "use scheme://host[:port], e.g. S3_ENDPOINT=http://minio:9000");
            else if (u.AbsolutePath != "/")
            {
                var first = u.AbsolutePath.Trim('/').Split('/')[0];
                W("endpoint-has-path", "S3_ENDPOINT", Q(endpoint), "the endpoint has a path" + (opts.S3AllowedBuckets.Contains(first) ? $" ('{first}' is a bucket)" : "") + "; the SDK adds bucket and key itself",
                    $"use only scheme://host[:port]: S3_ENDPOINT={u.GetLeftPart(UriPartial.Authority)}" + (opts.S3AllowedBuckets.Contains(first) ? $" and keep '{first}' in S3_ALLOWED_BUCKETS" : ""));
            }
            else if (u.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
                f.Add(new(false, $"storage.config note=endpoint-aws var=S3_ENDPOINT value={Q(endpoint)} detail={Q("not needed for AWS S3: unset S3_ENDPOINT and set S3_REGION instead")}"));
            else if (!opts.S3ForcePathStyle && s3)
                f.Add(new(false, $"storage.config note=path-style-off var=S3_FORCE_PATH_STYLE value={Q(env("S3_FORCE_PATH_STYLE"))} detail={Q("MinIO and most S3-compatible services need S3_FORCE_PATH_STYLE=true; without it the bucket becomes part of the host name")}"));
        }
        var pathStyleRaw = env("S3_FORCE_PATH_STYLE");
        Info($"storage.config var=S3_FORCE_PATH_STYLE value={Q(pathStyleRaw)} effective={(opts.S3ForcePathStyle ? "true" : "false")}");
        if (pathStyleRaw != null && !bool.TryParse(pathStyleRaw.Trim(), out _))
            W("invalid-bool", "S3_FORCE_PATH_STYLE", Q(pathStyleRaw), "must be true or false", "set S3_FORCE_PATH_STYLE=true (MinIO/path-style) or false (AWS)");

        // Timeouts.
        foreach (var (name, value, min, max) in new[] { ("IO_TIMEOUT_SECONDS", opts.IoTimeoutSeconds, 1, 300), ("IO_RETRIES", opts.IoRetries, 0, 10) })
        {
            var raw = env(name);
            Info($"storage.config var={name} value={Q(raw)} effective={value}");
            if (raw == null) continue;
            if (!int.TryParse(raw.Trim(), out var n))
                W("invalid-integer", name, Q(raw), "must be a whole number", $"set {name} to a whole number between {min} and {max}");
            else if (n < min || n > max)
                W("clamped", name, Q(raw), $"outside {min}..{max}; using {value}", $"set {name} between {min} and {max}");
        }

        // Filesystem and HTTPS allowlists.
        Info($"storage.config var=STORAGE_FILE_ROOTS value={Q(env("STORAGE_FILE_ROOTS"))} effective={(opts.StorageFileRoots.Length == 0 ? "\"(file off)\"" : Q(string.Join(':', opts.StorageFileRoots)))}");
        foreach (var r in opts.StorageFileRoots.Where(r => r.Contains(',')))
            W("root-has-comma", "STORAGE_FILE_ROOTS", Q(r), "roots are separated by ':' (or given as a JSON array), not ','",
                $"use STORAGE_FILE_ROOTS={string.Join(':', r.Split(',', StringSplitOptions.TrimEntries))} or [\"{string.Join("\",\"", r.Split(',', StringSplitOptions.TrimEntries))}\"]");
        Info($"storage.config var=HTTP_ALLOWED_HOSTS value={Q(env("HTTP_ALLOWED_HOSTS"))} effective={(opts.HttpAllowedHosts.Length == 0 ? "\"(https off)\"" : Q(string.Join(',', opts.HttpAllowedHosts)))}");
        foreach (var h in opts.HttpAllowedHosts)
        {
            if (h.Contains("://") || h.Contains('/'))
            {
                var host = h.Contains("://") ? h[(h.IndexOf("://") + 3)..] : h; host = host.Split('/')[0];
                W("host-not-bare", "HTTP_ALLOWED_HOSTS", Q(h), "entries are exact hostnames, without scheme or path", $"use HTTP_ALLOWED_HOSTS={host.Split(':')[0]}");
            }
            else if (h.Contains('*'))
                W("host-wildcard", "HTTP_ALLOWED_HOSTS", Q(h), "wildcards are not supported; hostnames match exactly", "list each hostname, comma-separated");
            else if (h.Contains(':'))
                W("host-has-port", "HTTP_ALLOWED_HOSTS", Q(h), "entries are hostnames only; a port never matches", $"use HTTP_ALLOWED_HOSTS={h.Split(':')[0]}");
        }

        // AWS credential variables (read directly by the AWS SDK).
        var keyId = env("AWS_ACCESS_KEY_ID"); var secret = env("AWS_SECRET_ACCESS_KEY"); var token = env("AWS_SESSION_TOKEN");
        Info($"storage.config var=AWS_ACCESS_KEY_ID value={(keyId == "" ? "\"\"" : Mask(keyId))}");
        Info($"storage.config var=AWS_SECRET_ACCESS_KEY value={(secret == "" ? "\"\"" : Mask(secret))}");
        Info($"storage.config var=AWS_SESSION_TOKEN value={(token == "" ? "\"\"" : Mask(token))}");
        foreach (var name in new[] { "AWS_PROFILE", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE" }) Info($"storage.config var={name} value={Q(env(name))}");
        if (!s3 && (keyId ?? secret ?? env("AWS_PROFILE")) != null)
            f.Add(new(false, "storage.config note=s3-off detail=\"AWS credentials are set but S3_ALLOWED_BUCKETS is empty, so S3 is off\""));
        if (!string.IsNullOrEmpty(keyId) && string.IsNullOrEmpty(secret))
            W("credentials-incomplete", "AWS_SECRET_ACCESS_KEY", "<unset>", "AWS_ACCESS_KEY_ID is set but AWS_SECRET_ACCESS_KEY is not, so the SDK ignores both", "set AWS_SECRET_ACCESS_KEY, or unset AWS_ACCESS_KEY_ID to use a profile or role");
        if (string.IsNullOrEmpty(keyId) && !string.IsNullOrEmpty(secret))
            W("credentials-incomplete", "AWS_ACCESS_KEY_ID", "<unset>", "AWS_SECRET_ACCESS_KEY is set but AWS_ACCESS_KEY_ID is not, so the SDK ignores both", "set AWS_ACCESS_KEY_ID, or unset AWS_SECRET_ACCESS_KEY to use a profile or role");
        var id = keyId?.Trim().Trim('"', '\'');
        if (!string.IsNullOrEmpty(id) && id.StartsWith("ASIA", StringComparison.Ordinal) && string.IsNullOrEmpty(token))
            W("session-token-missing", "AWS_SESSION_TOKEN", "<unset>", "AWS_ACCESS_KEY_ID starts with ASIA (temporary credentials) but no session token is set; S3 answers 403 InvalidAccessKeyId/InvalidToken",
                "set AWS_SESSION_TOKEN from the same STS response, or use long-term (AKIA...) keys or a role");
        if (!string.IsNullOrEmpty(id) && id.StartsWith("AKIA", StringComparison.Ordinal) && !string.IsNullOrEmpty(token))
            W("session-token-unexpected", "AWS_SESSION_TOKEN", Mask(token), "AWS_ACCESS_KEY_ID starts with AKIA (long-term key) but a session token is set; S3 rejects the request with 400/403 InvalidToken", "unset AWS_SESSION_TOKEN");
        if (aws && !string.IsNullOrEmpty(id) && !AwsKeyId().IsMatch(id))
            f.Add(new(false, $"storage.config note=access-key-id-unusual var=AWS_ACCESS_KEY_ID value={Mask(keyId)} detail=\"AWS access key ids are 20 uppercase characters starting with AKIA or ASIA; check that this is not a MinIO/other provider key\""));
        foreach (var name in new[] { "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE" })
            if (env(name) is { Length: > 0 } path)
            {
                var p = path.Trim().Trim('"', '\'');
                if (!Path.IsPathFullyQualified(p))
                    W("file-relative", name, Q(path), "relative paths resolve against the process working directory", $"use an absolute container path, e.g. {name}=/run/secrets/aws_credentials");
                else if (!File.Exists(p))
                    W("file-missing", name, Q(path), "the file does not exist inside the container", "mount the file at that path (check the volume and its read permission for the container user) or unset the variable");
            }
        // Formatting mistakes that survive parsing: quotes, surrounding whitespace, CRLF line endings.
        string[] all = ["S3_ALLOWED_BUCKETS", "S3_ALLOWED_PREFIXES", "S3_REGION", "S3_ENDPOINT", "S3_FORCE_PATH_STYLE", "STORAGE_FILE_ROOTS",
            "HTTP_ALLOWED_HOSTS", "IO_TIMEOUT_SECONDS", "IO_RETRIES", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN",
            "AWS_PROFILE", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE"];
        foreach (var name in all)
        {
            if (env(name) is not { Length: > 0 } raw) continue;
            var shown = SecretVars.Contains(name) ? Mask(raw) : Q(raw);
            var t = raw.Trim();
            if (raw.Contains('\r'))
                W("crlf", name, shown, "value ends with a carriage return (\\r), usually a Windows (CRLF) env file", $"convert the env file to LF line endings (e.g. dos2unix) and restart");
            else if (t.Length > 0 && (t[0] is '"' or '\'' || t[^1] is '"' or '\''))
                W("quoted", name, shown, "value contains surrounding quotes, which are passed through literally (docker --env-file and Kubernetes do not strip them)",
                    $"remove the quotes: {name}={(SecretVars.Contains(name) ? "<value>" : t.Trim('"', '\''))}");
            else if (raw != t && (SecretVars.Contains(name) || name is "S3_REGION" or "S3_ENDPOINT" or "AWS_PROFILE" or "AWS_SHARED_CREDENTIALS_FILE" or "AWS_CONFIG_FILE"))
                W("whitespace", name, shown, "value has leading or trailing whitespace, which is not trimmed for this variable", $"remove the spaces around the value of {name}");
        }

        return f;
    }

    /// <summary>Which provider of the SDK's default chain produced these credentials.</summary>
    public static string Classify(AWSCredentials? creds) => creds switch
    {
        null => "none",
        AnonymousAWSCredentials => "anonymous",
        EnvironmentVariablesAWSCredentials => "env",
        AssumeRoleWithWebIdentityCredentials => "web-identity",
        GenericContainerCredentials => "container-role",
        InstanceProfileAWSCredentials => "instance-role",
        _ when creds.GetType().Name.Contains("InstanceProfile", StringComparison.Ordinal) => "instance-role",
        _ when creds.GetType().Name.Contains("ECS", StringComparison.OrdinalIgnoreCase) => "container-role",
        _ => "profile", // Basic/Session (static keys in a profile), AssumeRole, SSO and process credentials
    };
}
