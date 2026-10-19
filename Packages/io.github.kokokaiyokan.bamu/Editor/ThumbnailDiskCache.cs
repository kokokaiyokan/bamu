using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace bamu
{
    // Shared between Unity projects; never put purchased-product images in Assets.
    internal sealed class ThumbnailDiskCache
    {
        internal const int MaxBytes = 8 * 1024 * 1024;
        private readonly string root, legacyRoot;

        public ThumbnailDiskCache(string root, string legacyRoot = null) { this.root = root; this.legacyRoot = legacyRoot; }

        internal string PathFor(string url)
        {
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(root, hash + ".image");
            }
        }

        public byte[] Read(string url)
        {
            try
            {
                var path = PathFor(url);
                if (!File.Exists(path) && legacyRoot != null)
                    BamuMigration.AdoptFile(Path.Combine(legacyRoot, Path.GetFileName(path)), path);
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (info.Length == 0 || info.Length > MaxBytes) { Remove(url); return null; }
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        public void Store(string url, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxBytes) return;
            string temporary = null;
            try
            {
                Directory.CreateDirectory(root);
                var path = PathFor(url);
                if (File.Exists(path)) return;
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, data);
                // A second Editor may have saved the same URL in the meantime.
                File.Move(temporary, path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            finally
            {
                try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        public void Remove(string url)
        {
            try { File.Delete(PathFor(url)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
