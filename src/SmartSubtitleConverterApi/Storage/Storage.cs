using SmartSubtitleConverterApi.Infrastructure;

namespace SmartSubtitleConverterApi.Storage;

public sealed record StorageLocation(string Scheme, string Value, Uri? Uri, string FileName)
{
    public static StorageLocation Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(char.IsControl) || value.EndsWith('/') || value.EndsWith('\\'))
            throw Rejected("A storage location must name a file, not a directory, and contain no control characters (maximum 16384 characters).");
        if (value.StartsWith('/') && !value.StartsWith("//"))
            return new("file", value, null, Path.GetFileName(value));
        if (!System.Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw Rejected("Use an absolute file path, file:///path/name, s3://bucket/key or https://host/path. Credentials and fragments are not accepted in URIs.");
        if (uri.Scheme == "file")
        {
            if (uri.Host.Length != 0 || uri.Query.Length != 0) throw Rejected("File URIs must be local, without a host or query.");
            // Inspect the original, before Uri can normalise dot segments.
            var raw = System.Uri.UnescapeDataString(value[7..]);
            // Decoded control characters (an encoded NUL, %00, …) are rejected here, before any filesystem call.
            if (raw.Any(char.IsControl)) throw DecodedControl();
            return new("file", raw, uri, Path.GetFileName(raw));
        }
        if (uri.Scheme == "s3" && uri.Query.Length != 0) throw Rejected("S3 locations must not contain query strings.");
        if (uri.Scheme == "s3" && System.Uri.UnescapeDataString(value).Any(char.IsControl)) throw DecodedControl();
        if (uri.Host.Length == 0 || !value.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase) || uri.Scheme is not ("https" or "s3") || uri.AbsolutePath is "" or "/" || uri.AbsolutePath.EndsWith('/'))
            throw Rejected("Only file, s3 and https locations naming a complete file are supported.");
        return new(uri.Scheme, value, uri, Path.GetFileName(System.Uri.UnescapeDataString(uri.AbsolutePath)));
    }
    private static ApiException DecodedControl() => Rejected("The decoded location contains control characters (for example an encoded NUL, %00); file names can't contain them.");
    public static ApiException OutputExists() => new(409, "output-exists", "The storage output already exists or a concurrent conditional write won; it was not replaced.", "Valid ifExists values: error | replace. Choose another output or explicitly replace. A previous job or attempt may have written, or still be writing, this location; inspect it first.");
    public static ApiException Rejected(string message) => new(422, "storage-location-rejected", message,
        "Check the URI and the server's storage allowlists. Empty allowlists disable that scheme.");
}

/// <summary>
/// Hidden-path policy for reads and listings: a path or object key with any '/'-separated component starting with '.' (a dotfile,
/// anything inside a dot-named folder, or anything under an allowed root that is itself hidden) is never read, listed or used as a
/// conversion source. Checked on the decoded, canonical path before any storage call. Ordinary names containing dots (show.en.srt)
/// are unaffected, and writes keep their existing rules.
/// </summary>
public static class HiddenPaths
{
    public static bool IsHidden(string path) => path.Split('/').Any(s => s.Length > 0 && s[0] == '.');
    public static ApiException Rejected() => new(422, ErrorCodes.StorageLocationRejected,
        "Hidden files and folders (a path component starting with '.') can't be read, listed or used as a conversion source.",
        "Use a file or folder whose name and parent folders don't start with '.'. See GET /v1/storage/locations for the browsable locations.");
    public static void Check(string path) { if (IsHidden(path)) throw Rejected(); }
}

public interface IStorage
{
    Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct);
    Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct, bool replace = false);
    Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct);
    /// <summary>Delete the object if it exists. True when something was deleted, false when there was nothing to delete.</summary>
    Task<bool> DeleteAsync(StorageLocation location, CancellationToken ct);
}

public sealed class StorageRouter(FileStorage files, HttpStorage http, S3Storage s3) : IStorage
{
    private IStorage For(StorageLocation location) => location.Scheme switch
    {
        "file" => files,
        "https" => http,
        "s3" => s3,
        _ => throw StorageLocation.Rejected($"The {location.Scheme} storage scheme is not enabled."),
    };
    public Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct) => For(location).ReadAsync(location, ct);
    public Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct, bool replace = false) => For(location).WriteAsync(location, bytes, contentType, ct, replace);
    public Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct) => For(location).ExistsAsync(location, ct);
    public Task<bool> DeleteAsync(StorageLocation location, CancellationToken ct) => For(location).DeleteAsync(location, ct);
    public static async Task<byte[]> ReadCapped(Stream stream, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        for (;;)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return buffer.ToArray();
            if (buffer.Length + n > max) throw new ApiException(413, ErrorCodes.PayloadTooLarge, $"Storage input exceeds {max} bytes.", "Use a smaller subtitle or change MAX_UPLOAD_BYTES.");
            buffer.Write(chunk, 0, n);
        }
    }
}
