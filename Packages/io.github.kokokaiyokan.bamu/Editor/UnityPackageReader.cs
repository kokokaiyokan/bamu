using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace bamu
{
    public static class UnityPackageReader
    {
        private const long MaxExpandedBytes = 32L * 1024 * 1024 * 1024;
        private static readonly byte[] Buffer = new byte[65536]; // Editor main thread only.

        public static string Hash(string file, Action<long> progress = null)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create())
            {
                int read;
                while ((read = stream.Read(Buffer, 0, Buffer.Length)) > 0)
                {
                    sha.TransformBlock(Buffer, 0, read, null, 0);
                    progress?.Invoke(stream.Position);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public static string HashIfFile(string path) => File.Exists(path) ? Hash(path) : "";
        public static bool IsGuid(string value) => value != null && value.Length == 32 && value.All(c =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

        public static bool ValidAssetPath(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) &&
            !path.Contains("\\") && !path.Contains(":") && !path.Any(char.IsControl) &&
            path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

        public static List<PackageAsset> Read(string file, Action<long> progress = null, bool includeContentHashes = false)
        {
            var entries = new List<PackageAsset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var input = File.OpenRead(file))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                var header = new byte[512];
                long total = 0;
                while (true)
                {
                    ReadExactly(gzip, header, header.Length);
                    if (header.All(b => b == 0)) break;
                    var storedChecksum = Octal(header, 148, 8);
                    long checksum = 0;
                    for (var i = 0; i < 512; i++) checksum += i >= 148 && i < 156 ? 32 : header[i];
                    if (storedChecksum != checksum) throw new InvalidDataException("パッケージのTARチェックサムが不正です。");
                    var name = Text(header, 0, 100);
                    var prefix = Text(header, 345, 155);
                    if (prefix.Length > 0) name = prefix + "/" + name;
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    var size = Octal(header, 124, 12);
                    total = checked(total + size + 512);
                    if (size < 0 || total > MaxExpandedBytes) throw new InvalidDataException("パッケージの展開サイズが上限を超えています。");
                    var parts = name.Split('/');
                    if (parts.Length == 2 && IsGuid(parts[0]) && parts[1] == "pathname")
                    {
                        if (header[156] != 0 && header[156] != (byte)'0') throw new InvalidDataException("pathnameが通常ファイルではありません。");
                        if (size > 32768) throw new InvalidDataException("パッケージのパス情報が長すぎます。");
                        var data = new byte[(int)size];
                        ReadExactly(gzip, data, data.Length);
                        var path = new UTF8Encoding(false, true).GetString(data).TrimEnd('\0', '\r', '\n');
                        if (!ValidAssetPath(path)) throw new InvalidDataException("Assets外または不正なパスを含むパッケージです: " + path);
                        if (!seen.Add(parts[0])) throw new InvalidDataException("重複したGUIDを含むパッケージです。");
                        entries.Add(new PackageAsset { guid = parts[0].ToLowerInvariant(), path = path });
                        if (entries.Count > 200000) throw new InvalidDataException("パッケージの項目数が上限を超えています。");
                    }
                    else if (parts.Length == 2 && IsGuid(parts[0]) && parts[1] == "asset")
                    {
                        if (!fileGuids.Add(parts[0])) throw new InvalidDataException("重複したassetエントリです。");
                        if (header[156] != 0 && header[156] != (byte)'0') throw new InvalidDataException("assetが通常ファイルではありません。");
                        if (includeContentHashes)
                        {
                            using (var sha = SHA256.Create())
                            {
                                var remaining = size;
                                while (remaining > 0)
                                {
                                    var n = (int)Math.Min(Buffer.Length, remaining);
                                    ReadExactly(gzip, Buffer, n);
                                    sha.TransformBlock(Buffer, 0, n, null, 0);
                                    remaining -= n;
                                    progress?.Invoke(input.Position);
                                }
                                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                hashes[parts[0]] = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                            }
                        }
                        else Skip(gzip, size);
                    }
                    else Skip(gzip, size);
                    Skip(gzip, (512 - size % 512) % 512);
                    progress?.Invoke(input.Position);
                }
            }
            if (entries.Count == 0) throw new InvalidDataException("パッケージにアセットのパス情報がありません。");
            if (entries.Select(e => e.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
                throw new InvalidDataException("複数GUIDが同じパスを指すパッケージです。");
            foreach (var entry in entries)
            {
                entry.folder = !fileGuids.Contains(entry.guid);
                if (hashes.TryGetValue(entry.guid, out var hash)) entry.contentHash = hash;
            }
            return entries;
        }
        private static string Text(byte[] buffer, int offset, int length) => Encoding.UTF8.GetString(buffer, offset, length).TrimEnd('\0');
        private static long Octal(byte[] buffer, int offset, int length)
        {
            long value = 0;
            for (var i = offset; i < offset + length; i++)
            {
                var c = buffer[i];
                if (c == 0 || c == 32) continue;
                if (c < '0' || c > '7') throw new InvalidDataException("未対応のTARサイズ形式です。");
                value = checked(value * 8 + c - '0');
            }
            return value;
        }
        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("パッケージが途中で切れています。");
                offset += read;
            }
        }
        private static void Skip(Stream stream, long count)
        {
            while (count > 0) { var n = (int)Math.Min(Buffer.Length, count); ReadExactly(stream, Buffer, n); count -= n; }
        }

        public static List<string> ZipPackages(string path)
        {
            using (var zip = ZipFile.OpenRead(path)) return zip.Entries.Where(e => e.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName).ToList();
        }
        public static string ExtractZipPackage(string zipPath, string entryName, Action<long> progress = null)
        {
            var directory = BridgeStorage.CacheDirectory("Packages");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var matches = zip.Entries.Where(e => e.FullName == entryName).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException("ZIP内のパッケージを一意に特定できません。");
                    var entry = matches[0];
                    if (entry.Length > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP内のパッケージが4GBを超えています。");
                    using (var input = entry.Open())
                    using (var output = File.Create(temporary))
                    {
                        long size = 0; int read;
                        while ((read = input.Read(Buffer, 0, Buffer.Length)) > 0)
                        {
                            size += read;
                            if (size > entry.Length || size > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIPのサイズ情報が不正です。");
                            output.Write(Buffer, 0, read);
                            progress?.Invoke(size);
                        }
                        if (size != entry.Length) throw new InvalidDataException("ZIPの内容が途中で切れています。");
                    }
                }
                // Never use archive entry paths as filesystem destinations. Use a content-addressed filename.
                var target = Path.Combine(directory, Hash(temporary) + ".unitypackage");
                if (File.Exists(target))
                {
                    if (Hash(target) != Path.GetFileNameWithoutExtension(target)) throw new InvalidDataException("キャッシュの内容が不正です。");
                }
                else File.Move(temporary, target);
                return target;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
