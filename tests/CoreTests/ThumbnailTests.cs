using System;
using System.IO;
using System.Linq;
using bamu;

internal static class ThumbnailTests
{
    public static void Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(TestEnvironment.RunRoot, "thumbnail-tests", Guid.NewGuid().ToString("N"));
        var cache = new ThumbnailDiskCache(folder);
        const string url = "https://booth.pximg.net/test/thumbnail.png";
        var data = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        check(cache.Read(url) == null, "thumbnail cache miss does not create files");
        cache.Store(url, data);
        check(new ThumbnailDiskCache(folder).Read(url).SequenceEqual(data), "thumbnail bytes survive cache instance recreation");
        check(cache.Read(url + "?updated=1") == null, "changed thumbnail URL needs a new image");
        check(Path.GetFileName(cache.PathFor(url)).Length == 70, "thumbnail filenames are URL hashes without URL path components");
        cache.Store(url, new byte[] { 1 });
        check(cache.Read(url).SequenceEqual(data) && Directory.GetFiles(folder, "*.tmp").Length == 0,
            "duplicate saves retain the complete existing cache without temporary files");
        File.WriteAllBytes(cache.PathFor(url), Array.Empty<byte>());
        check(cache.Read(url) == null && !File.Exists(cache.PathFor(url)), "empty cache files are removed for retry");
        File.WriteAllBytes(cache.PathFor(url), new byte[ThumbnailDiskCache.MaxBytes + 1]);
        check(cache.Read(url) == null && !File.Exists(cache.PathFor(url)), "oversized cache files are removed for retry");
        var blocked = folder + ".blocked";
        File.WriteAllText(blocked, "occupied");
        var unavailable = new ThumbnailDiskCache(blocked);
        unavailable.Store(url, data);
        check(unavailable.Read(url) == null, "unavailable cache storage does not interrupt thumbnail use");
        cache.Store(url, data);
        cache.Remove(url);
        check(cache.Read(url) == null, "damaged decoded images can be evicted and downloaded again");
    }
}
