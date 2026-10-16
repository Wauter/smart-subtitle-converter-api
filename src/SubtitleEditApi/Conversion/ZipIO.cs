using System.IO.Compression;
using System.Text.Json;
using SubtitleEditApi.Infrastructure;

namespace SubtitleEditApi.Conversion;

public sealed record ZipEntryIn(string Name, byte[] Bytes);

/// <summary>In-memory zip in/out with the guards from design §6.2. Nothing touches disk.</summary>
public static class ZipIO
{
    public static bool IsZip(byte[] b) => b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;

    public static List<ZipEntryIn> Read(byte[] bytes, ApiOptions opts, List<Warning> warnings)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new ApiException(400, ErrorCodes.InvalidRequest, "The zip file is corrupt.", "Re-create the zip archive.", ex);
        }
        using (zip)
        {
            var files = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            if (files.Count > opts.ZipMaxEntries)
            {
                throw new ApiException(413, ErrorCodes.ZipLimitsExceeded, $"Zip has {files.Count} entries; max is {opts.ZipMaxEntries}.",
                    $"Zip has {files.Count} entries; max is {opts.ZipMaxEntries} (ZIP_MAX_ENTRIES).");
            }
            var result = new List<ZipEntryIn>();
            long total = 0;
            foreach (var e in files)
            {
                var name = e.FullName;
                if (name.Contains("..") || name.StartsWith('/') || name.Contains('\\') || (name.Length > 1 && name[1] == ':') || name.Any(char.IsControl))
                {
                    throw new ApiException(422, ErrorCodes.UnsafeZip, "The zip contains an unsafe entry name.",
                        "Entry names must be relative paths without '..', backslashes or drive letters.")
                    { Errors = [new FieldError("/file/" + result.Count, "Unsafe entry name (path traversal).")] };
                }
                if (Path.GetFileName(name).StartsWith("._") || name.StartsWith("__MACOSX/")) continue;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(new("zip-nested-skipped", "A nested zip entry was skipped.")); continue;
                }
                if (name == "manifest.json") continue;

                var limit = Math.Min(opts.MaxUploadBytes, opts.ZipMaxUnpackedBytes - total);
                using var s = e.Open();
                var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > limit)
                        throw new ApiException(413, ErrorCodes.ZipLimitsExceeded, "The zip unpacks to more than the allowed size.",
                            $"Max unpacked size is {opts.ZipMaxUnpackedBytes / 1024 / 1024} MB total (ZIP_MAX_UNPACKED_BYTES) and {opts.MaxUploadBytes / 1024 / 1024} MB per entry.");
                    if (ms.Length > 1_000_000 && ms.Length > Math.Max(1, e.CompressedLength) * opts.ZipMaxRatio)
                        throw new ApiException(413, ErrorCodes.ZipLimitsExceeded, "A zip entry has a suspicious compression ratio (zip bomb).",
                            $"Max compression ratio is {opts.ZipMaxRatio}:1.");
                }
                total += ms.Length;
                result.Add(new ZipEntryIn(name, ms.ToArray()));
            }
            if (result.Count == 0)
                throw new ApiException(400, ErrorCodes.EmptyInput, "The zip contains no files.", "Add subtitle files to the zip.");
            return result;
        }
    }

    public static byte[] Write(IEnumerable<(string Name, byte[] Bytes)> files, object manifest)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
            foreach (var (name, bytes) in files)
            {
                var n = name;
                for (var i = 2; !used.Add(n); i++) n = Path.Combine(Path.GetDirectoryName(name) ?? "", $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}").Replace('\\', '/');
                var entry = zip.CreateEntry(n, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes);
            }
            var m = zip.CreateEntry("manifest.json");
            using var ms2 = m.Open();
            JsonSerializer.Serialize(ms2, manifest, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
        }
        return ms.ToArray();
    }
}
