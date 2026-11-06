using SubtitleEditConverterApi.Infrastructure;

namespace SubtitleEditConverterApi.Storage;

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
            return new("file", raw, uri, Path.GetFileName(raw));
        }
        if (uri.Scheme is not ("https" or "s3") || uri.AbsolutePath is "" or "/" || uri.AbsolutePath.EndsWith('/'))
            throw Rejected("Only file, s3 and https locations naming a complete file are supported.");
        return new(uri.Scheme, value, uri, Path.GetFileName(System.Uri.UnescapeDataString(uri.AbsolutePath)));
    }
    public static ApiException Rejected(string message) => new(422, "storage-location-rejected", message,
        "Check the URI and the server's storage allowlists. Empty allowlists disable that scheme.");
}

public interface IStorage
{
    Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct);
    Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct);
    Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct);
}

public sealed class StorageRouter(FileStorage files, HttpStorage http) : IStorage
{
    private IStorage For(StorageLocation location) => location.Scheme switch
    {
        "file" => files,
        "https" => http,
        _ => throw StorageLocation.Rejected($"The {location.Scheme} storage scheme is not enabled."),
    };
    public Task<byte[]> ReadAsync(StorageLocation location, CancellationToken ct) => For(location).ReadAsync(location, ct);
    public Task WriteAsync(StorageLocation location, byte[] bytes, string contentType, CancellationToken ct) => For(location).WriteAsync(location, bytes, contentType, ct);
    public Task<bool> ExistsAsync(StorageLocation location, CancellationToken ct) => For(location).ExistsAsync(location, ct);
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
