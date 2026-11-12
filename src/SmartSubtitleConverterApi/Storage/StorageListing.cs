using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

/// <summary>
/// Browse and read by location (STORAGE_LIST_ENABLED): folder parsing, URIs, cursors and the gate.
/// Scope is the existing storage allowlists (STORAGE_FILE_ROOTS, S3_ALLOWED_BUCKETS, S3_ALLOWED_PREFIXES); HTTPS is never listable.
/// </summary>
public static class StorageListing
{
    public const int PageSize = 200;
    /// <summary>Most entries a listed filesystem folder (after the name prefix) may have; more is 422 folder-too-large (H6).</summary>
    public const int MaxEntries = 5000;
    /// <summary>Most names read from one filesystem folder per page, whatever the prefix; bounds the cost of a huge folder.</summary>
    public const int MaxScan = 100_000;

    public static ApiException FolderTooLarge(string directory, int limit, bool prefixed) => new(422, ErrorCodes.FolderTooLarge,
        prefixed ? $"More than {limit} entries in this folder start with that prefix, so it can't be listed."
                 : $"This folder has more than {limit} entries, so it can't be listed.",
        $"Folders are listed only up to {MaxEntries} matching entries (and {MaxScan} names in total). Narrow the listing with a longer `prefix` (the start of the names you want), "
        + "or organise the files into subfolders.") { Extensions = { ["limit"] = limit } };

    public sealed record Entry(string Name, bool Folder, long? Size, DateTimeOffset? Modified);

    /// <summary>A parsed folder location: scheme, bucket (s3) and the folder (absolute path or key prefix) ending in '/' (or "" for a bucket root).</summary>
    public sealed record Folder(string Scheme, string? Bucket, string Directory)
    {
        public string Uri => Scheme == "file" ? FileUri(Directory) : S3Uri(Bucket!, Directory);
    }

    public static ApiException Disabled() => new(403, ErrorCodes.StorageListDisabled,
        "Browsing storage and reading files by location are disabled on this API.",
        "The operator can set STORAGE_LIST_ENABLED=true on the API (and restart it) to enable GET /v1/storage/locations, /v1/storage/list and /v1/storage/file. Reading a job's stored files (GET /v1/jobs/{id}/input|output) works either way.");

    public static void Require(ApiOptions opts) { if (!opts.StorageListEnabled) throw Disabled(); }

    public static ApiException NotListable(string scheme) => new(422, ErrorCodes.StorageNotListable,
        scheme == "https" ? "HTTPS locations can't be listed or read by location: HTTPS has no folders to list." : $"{scheme} locations can't be listed or read by location.",
        "Browse and read file (STORAGE_FILE_ROOTS) and S3 (S3_ALLOWED_BUCKETS) locations; see GET /v1/storage/locations. HTTPS URLs still work as convert input and output.");

    public static ApiException CursorInvalid(string why) => new(400, ErrorCodes.StorageCursorInvalid, $"The listing cursor is not valid here: {why}.",
        "Pass `next` from the previous page unchanged, with the same location and prefix, or start again without a cursor.")
        { Errors = [new("/query/cursor", why)] };

    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>UTF-8 byte order, the order S3 lists keys in.</summary>
    public static int Utf8Compare(string a, string b) => Utf8.GetBytes(a).AsSpan().SequenceCompareTo(Utf8.GetBytes(b));
    /// <summary>The sort key for <see cref="Utf8Compare"/>: compute once per name, compare with SequenceCompareTo.</summary>
    public static byte[] Utf8Key(string s) => Utf8.GetBytes(s);
    public static readonly IComparer<string> Utf8Order = Comparer<string>.Create(Utf8Compare);

    private static string Escape(string path) => string.Join('/', path.Split('/').Select(System.Uri.EscapeDataString));
    public static string FileUri(string path) => "file://" + Escape(path);
    public static string S3Uri(string bucket, string key) => $"s3://{bucket}/" + Escape(key);

    /// <summary>
    /// Parses a folder: /abs/dir, file:///abs/dir/, s3://bucket, s3://bucket/ or s3://bucket/key/prefix/. A trailing '/' is implied.
    /// No credentials, query, fragment, port, dot segments, backslashes or control characters.
    /// </summary>
    public static Folder ParseFolder(string? value)
    {
        static ApiException Bad(string why) => new(422, ErrorCodes.StorageLocationRejected, why, "Use a folder from GET /v1/storage/locations: file:///root/folder/ or s3://bucket/prefix/.")
            { Errors = [new("/query/location", why)] };
        if (string.IsNullOrWhiteSpace(value)) throw new ApiException(400, ErrorCodes.InvalidRequest, "`location` is required.", "Pass a folder from GET /v1/storage/locations.") { Errors = [new("/query/location", "Required.")] };
        if (value.Length > 4096 || value.Any(char.IsControl) || value.Contains('\\')) throw Bad("A folder location must be at most 4096 characters, without control characters or backslashes.");
        string rest;
        if (value.StartsWith('/') && !value.StartsWith("//")) rest = value;
        else if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) || value.Contains('?') || value.Contains('#')) throw Bad("File URIs must be local (file:///path/), without a host, query or fragment.");
            rest = Unescape(value[7..]);
        }
        else if (value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains('?') || value.Contains('#') || value.Contains('@')) throw Bad("S3 locations must not contain credentials, a query or a fragment.");
            var slash = value.IndexOf('/', 5);
            var bucket = slash < 0 ? value[5..] : value[5..slash];
            var key = slash < 0 ? "" : Unescape(value[(slash + 1)..]);
            if (key.Length > 0 && !key.EndsWith('/')) key += "/";
            if (key.StartsWith('/') || key.Contains("//") || key.Split('/').Any(s => s is "." or "..")) throw Bad("S3 folder prefixes must not contain empty or dot segments.");
            return new("s3", bucket, key);
        }
        else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw NotListable("https");
        else throw Bad("Use an absolute folder path, file:///folder/ or s3://bucket/prefix/.");
        if (!rest.EndsWith('/')) rest += "/";
        if (rest.Contains("//") || rest.Split('/').Any(s => s is "." or "..") || rest.Any(char.IsControl) || rest.Contains('\\')) throw Bad("Filesystem folders must be absolute, without empty or dot segments.");
        return new("file", null, rest);
    }

    private static string Unescape(string s)
    {
        try { return System.Uri.UnescapeDataString(s); } catch (UriFormatException) { throw new ApiException(422, ErrorCodes.StorageLocationRejected, "The location has invalid percent-encoding.", "Encode the folder as a URI."); }
    }

    /// <summary>Validates `prefix` (a name prefix; '/' descends into sub-folders) and splits it into the listed folder and the name filter.</summary>
    public static (string Directory, string NamePrefix) Split(Folder folder, string? prefix)
    {
        prefix ??= "";
        if (prefix.Length > 1024 || prefix.StartsWith('/') || prefix.Contains('\\') || prefix.Contains("//") || prefix.Any(char.IsControl) || prefix.Split('/').SkipLast(1).Any(s => s is "." or ".."))
            throw new ApiException(400, ErrorCodes.InvalidRequest, "`prefix` must be a relative name prefix (at most 1024 characters) without a leading '/', empty or dot segments, backslashes or control characters.",
                "Pass the start of a file or folder name, e.g. show_ or season1/ep; no globs.") { Errors = [new("/query/prefix", "Invalid prefix.")] };
        var cut = prefix.LastIndexOf('/') + 1;
        return (folder.Directory + prefix[..cut], prefix[cut..]);
    }

    // Cursor: base64url JSON {v:1, b: binding of scheme+bucket+folder+prefix, a: last name (file) | t: continuation token (s3)}.
    private sealed record CursorBody(int V, string B, string? A, string? T);
    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web);

    public static string Binding(Folder folder, string? prefix) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{folder.Scheme}\n{folder.Bucket}\n{folder.Directory}\n{prefix ?? ""}")).AsSpan(0, 12)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string EncodeCursor(string binding, string? after, string? token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new CursorBody(1, binding, after, token), CursorJson);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The name or continuation token to continue after; every malformed or foreign cursor is storage-cursor-invalid.</summary>
    public static (string? After, string? Token) DecodeCursor(string? cursor, string binding, string scheme)
    {
        if (cursor == null) return (null, null);
        if (cursor.Length is 0 or > 4096 || cursor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw CursorInvalid("it is not a cursor returned by this API");
        CursorBody? body;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            body = JsonSerializer.Deserialize<CursorBody>(Convert.FromBase64String(b64), CursorJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw CursorInvalid("it is not a cursor returned by this API"); }
        if (body is not { V: 1 }) throw CursorInvalid("it is not a cursor returned by this API");
        if (body.B != binding) throw CursorInvalid("it was returned for another location or prefix");
        if (scheme == "file" ? string.IsNullOrEmpty(body.A) || body.T != null : string.IsNullOrEmpty(body.T) || body.A != null) throw CursorInvalid("it is not a cursor returned by this API");
        return (body.A, body.T);
    }
}
