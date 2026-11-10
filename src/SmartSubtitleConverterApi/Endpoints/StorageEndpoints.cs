using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Storage;

namespace SmartSubtitleConverterApi.Endpoints;

/// <summary>One allowed storage location. `uri` is a folder; `prefix`, when set, is the name prefix the allowlist requires inside it.</summary>
public sealed record StorageLocationInfo(string Uri, string Scheme, bool Listable, bool Readable, string? Prefix = null, string? Note = null);
public sealed record StorageLocationsResponse(List<StorageLocationInfo> Locations);
/// <summary>`name` is relative to the listed folder; folders end in '/'. `size` and `modified` are set for files.</summary>
public sealed record StorageItem(string Name, string Uri, string Type, long? Size = null, DateTimeOffset? Modified = null);
public sealed record StorageListResponse(string Location, string Prefix, List<StorageItem> Items, int Returned, bool Truncated, string? Next = null);

public static class StorageEndpoints
{
    private const string Gate = " Requires STORAGE_LIST_ENABLED=true; otherwise 403 storage-list-disabled.";

    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapGet("/storage/locations", (HttpContext ctx, ApiOptions opts) =>
        {
            StorageListing.Require(opts);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new StorageLocationsResponse(Locations(opts)), Json.Options);
        }).WithName("listStorageLocations").WithTags("storage")
          .WithSummary("List the allowed storage locations (file roots, S3 buckets and prefixes, HTTPS hosts).")
          .WithDescription("The existing allowlists, as folders to pass to GET /v1/storage/list. HTTPS hosts are reported as not listable and not readable by location (convert input/output only). Never includes credentials or query strings." + Gate)
          .Produces<StorageLocationsResponse>().ProducesProblem(403);

        v1.MapGet("/storage/list", (HttpContext ctx, ApiOptions opts, FileStorage files, S3Storage s3, BrowseQueue browse, string? location, string? prefix, string? cursor) =>
        {
            StorageListing.Require(opts);
            return browse.RunAsync(() => ListAsync(ctx, opts, files, s3, location, prefix, cursor), ctx.RequestAborted);
        }).WithName("listStorage").WithTags("storage")
          .WithSummary("List one folder of an allowed file or S3 location, a page at a time.")
          .WithDescription("`location` is a folder (file:///root/folder/, /root/folder/ or s3://bucket/prefix/), `prefix` an optional name prefix ('/' descends into sub-folders; no globs). "
              + $"Lists one level in backend name order (UTF-8 byte order), at most {StorageListing.PageSize} items per page: files with a subtitle extension (GET /v1/limits storage.extensions) with size and modified time, and folders (ending in '/'). "
              + "When `truncated` is true pass `next` as `cursor` with the same location and prefix. Stateless: items that exist for the whole walk appear exactly once; items created or deleted during the walk may or may not appear. "
              + $"No total count, time sort or offsets. A filesystem folder with more than {StorageListing.MaxEntries} matching entries is 422 folder-too-large (narrow it with `prefix`). "
              + "Hidden entries (names starting with '.') are skipped; a hidden folder or a prefix starting with '.' is 422 hidden-path. Filesystem listings also skip symlinks and special files. HTTPS is not listable (422 storage-not-listable). "
              + "Shares the browse queue with reads by location and job file reads (503 browse-busy with Retry-After when it is full)." + Gate)
          .Produces<StorageListResponse>().ProducesProblem(403).ProducesProblem(404).ProducesProblem(422).ProducesProblem(502).ProducesProblem(503);

        v1.MapGet("/storage/file", (HttpContext ctx, ApiOptions opts, StorageRouter storage, Formats.FormatRegistry formats, BrowseQueue browse, string? location) =>
        {
            StorageListing.Require(opts);
            ctx.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(location)) throw new ApiException(400, ErrorCodes.InvalidRequest, "`location` is required.", "Pass a file uri from GET /v1/storage/list.") { Errors = [new("/query/location", "Required.")] };
            var parsed = StorageLocation.Parse(location);
            if (parsed.Scheme is not ("file" or "s3")) throw StorageListing.NotListable(parsed.Scheme);
            return browse.RunAsync(async () =>
            {
                var (bytes, tag) = await storage.ReadIfChangedAsync(parsed, ctx.Request.Headers.IfNoneMatch.ToString(), ctx.RequestAborted);
                if (tag != null) ctx.Response.Headers.ETag = tag;
                if (bytes == null) return Results.StatusCode(304);
                var format = formats.Find(Path.GetExtension(parsed.FileName));
                return ConvertEndpoints.File(ctx, bytes, format?.MediaType ?? "application/octet-stream", parsed.FileName);
            }, ctx.RequestAborted);
        }).WithName("getStorageFile").WithTags("storage")
          .WithSummary("Read one file from an allowed file or S3 location (current contents, unconverted).")
          .WithDescription("The same allowlists, symlink, traversal, hidden-file, extension and size checks as convert input: outside the allowed locations is 422 storage-location-rejected, a path or key with a component starting with '.' is 422 hidden-path, "
              + "a name without a subtitle extension is 422 extension-not-allowed. HTTPS is not readable here (422 storage-not-listable); use it as convert input. Shares the browse queue (503 browse-busy). "
              + "The response carries an ETag (file: size, modification time and inode; S3: the object ETag); send it back as If-None-Match to get 304 Not Modified without the content being read (a cheap change check)." + Gate)
          .Produces(200, contentType: "application/octet-stream").Produces(304).ProducesProblem(403).ProducesProblem(404).ProducesProblem(413).ProducesProblem(422).ProducesProblem(502).ProducesProblem(503);
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, ApiOptions opts, FileStorage files, S3Storage s3, string? location, string? prefix, string? cursor)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var folder = StorageListing.ParseFolder(location);
        var (directory, namePrefix) = StorageListing.Split(folder, prefix);
        var binding = StorageListing.Binding(folder, prefix);
        var (after, token) = StorageListing.DecodeCursor(cursor, binding, folder.Scheme);
        List<StorageListing.Entry> entries; bool more; string? next = null;
        if (folder.Scheme == "file")
        {
            (entries, more) = files.List(directory, namePrefix, after, StorageListing.PageSize, ctx.RequestAborted);
            if (more) next = StorageListing.EncodeCursor(binding, entries[^1].Name.TrimEnd('/'), null);
        }
        else
        {
            if (opts.S3AllowedBuckets.Length == 0) throw StorageLocation.Rejected("S3 storage is disabled (S3_ALLOWED_BUCKETS is empty).");
            string? nextToken;
            (entries, more, nextToken) = await s3.ListAsync(folder.Bucket!, directory, namePrefix, token, StorageListing.PageSize, ctx.RequestAborted);
            if (more) next = StorageListing.EncodeCursor(binding, null, nextToken);
        }
        var items = entries.Select(e => new StorageItem(e.Name,
            folder.Scheme == "file" ? StorageListing.FileUri(directory + e.Name) : StorageListing.S3Uri(folder.Bucket!, directory + e.Name),
            e.Folder ? "folder" : "file", e.Size, e.Modified)).ToList();
        return Results.Json(new StorageListResponse(folder.Uri, prefix ?? "", items, items.Count, more, next), Json.Options);
    }

    /// <summary>
    /// File roots (Linux only), S3 buckets × allowed prefixes, then HTTPS hosts (not listable). Roots and prefixes with a hidden
    /// component (see <see cref="HiddenPaths"/>) are left out: nothing in them can be read or listed, and their names stay unrevealed.
    /// </summary>
    public static List<StorageLocationInfo> Locations(ApiOptions opts)
    {
        var list = new List<StorageLocationInfo>();
        if (OperatingSystem.IsLinux())
            foreach (var root in opts.StorageFileRoots.Where(r => !HiddenPaths.IsHidden(Path.GetFullPath(r))))
                list.Add(new(StorageListing.FileUri(Path.GetFullPath(root).TrimEnd('/') + "/"), "file", true, true));
        foreach (var bucket in opts.S3AllowedBuckets)
        {
            if (opts.S3AllowedPrefixes.Length == 0) { list.Add(new(StorageListing.S3Uri(bucket, ""), "s3", true, true)); continue; }
            foreach (var p in opts.S3AllowedPrefixes.Where(p => !HiddenPaths.IsHidden(p)))
            {
                var cut = p.LastIndexOf('/') + 1;
                list.Add(new(StorageListing.S3Uri(bucket, p[..cut]), "s3", true, true, cut < p.Length ? p[cut..] : null));
            }
        }
        foreach (var host in opts.HttpAllowedHosts)
            list.Add(new($"https://{host}/", "https", false, false, Note: "Convert input/output only: HTTPS has no folders to list and is not read by location."));
        return list;
    }
}
