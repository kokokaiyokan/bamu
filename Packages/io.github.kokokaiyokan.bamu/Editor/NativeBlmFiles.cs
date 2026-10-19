using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static bamu.BridgeJson;

namespace bamu
{
    internal static class NativeBlmFiles
    {
        private const long MaxFile = 4L * 1024 * 1024 * 1024, MaxTotal = 20L * 1024 * 1024 * 1024;
        public static bool Inside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static void Plain(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の保存ファイルは確認できません。");
        }
        public static string Hash(string path, CancellationToken cancellation)
        {
            using (var input = File.OpenRead(path)) return Hash(input, cancellation);
        }
        private static string Hash(Stream input, CancellationToken cancellation)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                { cancellation.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, null, 0); }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public static string TextHash(string text)
        {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(text))) return Hash(input, CancellationToken.None);
        }
        public static Dictionary<string, object> Scan(string product, string name, CancellationToken cancellation)
        {
            var result = Obj();
            if (!Directory.Exists(product)) return result;
            Plain(product); long size = 0;
            var stem = Path.GetFileNameWithoutExtension(name); var suffix = Path.GetExtension(name);
            foreach (var top in Directory.GetFileSystemEntries(product))
            {
                var directory = Directory.Exists(top);
                var pattern = "^" + Regex.Escape(stem) + @"(?: \([1-9][0-9]*\))?" + (directory ? "" : Regex.Escape(suffix)) + "$";
                if (!Regex.IsMatch(Path.GetFileName(top), pattern, RegexOptions.IgnoreCase)) continue;
                Walk(top);
            }
            return result;
            void Walk(string path)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Inside(path, product)) throw new IOException("BLM保存先の外を参照しています。");
                Plain(path);
                if (Directory.Exists(path)) { foreach (var child in Directory.GetFileSystemEntries(path)) Walk(child); return; }
                var before = new FileInfo(path); var length = before.Length; var ticks = before.LastWriteTimeUtc.Ticks;
                size += length;
                if (length > MaxFile || size > MaxTotal || result.Count >= 100000) throw new IOException("保存確認のサイズ上限を超えています。");
                var digest = Hash(path, cancellation); var after = new FileInfo(path);
                if (length != after.Length || ticks != after.LastWriteTimeUtc.Ticks) throw new IOException("BLMが保存中です。");
                var relative = path.Substring(Path.GetFullPath(product).TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
                result.Add(relative, Obj("sha256", digest, "size", length, "mtime_ns", checked((ticks - 621355968000000000L) * 100)));
            }
        }
        public static Dictionary<string, object> Existing(string product, string name, CancellationToken cancellation)
        {
            var exact = Scan(product, name, cancellation);
            if (exact.Count > 0 || !Directory.Exists(product)) return exact;
            var remote = Version(name);
            if (remote == null) return exact;
            foreach (var top in Directory.GetFileSystemEntries(product))
            {
                var localName = Directory.Exists(top) ? Regex.Replace(Path.GetFileName(top), @" \([1-9][0-9]*\)$", "") + Path.GetExtension(name) : Path.GetFileName(top);
                var local = Version(localName);
                if (local != null && local.Item1 == remote.Item1 && Compare(local.Item2, remote.Item2) >= 0)
                {
                    var found = Scan(product, localName, cancellation);
                    if (found.Count > 0) return found;
                }
            }
            return exact;
        }
        private static readonly Regex VersionPattern = new Regex(@"v(?:er(?:sion)?)?[ ._-]*(\d+(?:[._]\d+)*)", RegexOptions.IgnoreCase);
        public static Tuple<string, string[]> Version(string name)
        {
            var match = VersionPattern.Match(name);
            return !match.Success ? null : Tuple.Create(VersionPattern.Replace(name, "{version}", 1).ToLowerInvariant(), match.Groups[1].Value.Split('.', '_'));
        }
        public static int Compare(string[] left, string[] right)
        {
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var a = (i < left.Length ? left[i] : "0").TrimStart('0'); var b = (i < right.Length ? right[i] : "0").TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var compare = string.CompareOrdinal(a, b); if (compare != 0) return compare;
            }
            return 0;
        }
        public static void Backup(string home, string item, string product, Dictionary<string, object> entries, CancellationToken cancellation)
        {
            foreach (var pair in entries)
            {
                var digest = Str(Map(pair.Value), "sha256");
                if (!Regex.IsMatch(digest, "^[a-f0-9]{64}$")) throw new IOException("保存ハッシュが不正です。");
                var target = Path.Combine(home, "blm-objects", item, digest);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target)) { if (Hash(target, cancellation) != digest) throw new IOException("保管物が変更されています。"); continue; }
                var source = Path.GetFullPath(Path.Combine(product, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!Inside(source, product)) throw new IOException("保管元のパスが不正です。");
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using (var input = File.OpenRead(source))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[65536]; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                        output.Flush(true);
                    }
                    if (Hash(temporary, cancellation) != digest) throw new IOException("保管中にファイルが変化しました。");
                    cancellation.ThrowIfCancellationRequested(); File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        public static void Validate(string path, CancellationToken cancellation)
        {
            if (path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                using (var input = File.OpenRead(path))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                {
                    var header = new byte[512]; var buffer = new byte[65536]; long total = 0; var found = false; var count = 0;
                    while (true)
                    {
                        Exact(gzip, header, 512, cancellation);
                        if (header.All(b => b == 0)) break;
                        long expected = Octal(header, 148, 8), sum = 0;
                        for (var i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 32 : header[i];
                        if (sum != expected) throw new IOException("TARチェックサムが不正です。");
                        var size = Octal(header, 124, 12); total = checked(total + size);
                        if (total > MaxTotal || ++count > 100000) throw new IOException("パッケージが大きすぎます。");
                        if (Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0').EndsWith("/pathname", StringComparison.Ordinal)) found = true;
                        var remaining = checked((size + 511) / 512 * 512);
                        while (remaining > 0) { var take = (int)Math.Min(remaining, buffer.Length); Exact(gzip, buffer, take, cancellation); remaining -= take; }
                    }
                    int trailing;
                    while ((trailing = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    { cancellation.ThrowIfCancellationRequested(); total = checked(total + trailing); if (total > MaxTotal) throw new IOException("パッケージが大きすぎます。"); }
                    if (!found) throw new IOException("unitypackageのパス情報がありません。");
                }
            }
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ValidateZip(path, cancellation);
            else
            {
                using (var input = File.OpenRead(path))
                {
                    var data = new byte[512]; var n = input.Read(data, 0, data.Length); var text = Encoding.UTF8.GetString(data, 0, n).TrimStart().ToLowerInvariant();
                    if (text.StartsWith("<html") || text.StartsWith("<!doctype html")) throw new IOException("商品ファイルの代わりにHTMLを検出しました。");
                }
            }
        }
        private static void Exact(Stream stream, byte[] bytes, int length, CancellationToken cancellation)
        {
            var offset = 0;
            while (offset < length) { cancellation.ThrowIfCancellationRequested(); var n = stream.Read(bytes, offset, length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
        }
        private static long Octal(byte[] bytes, int offset, int count) => Convert.ToInt64(Encoding.ASCII.GetString(bytes, offset, count).Trim('\0', ' '), 8);
        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            var result = new uint[256];
            for (uint i = 0; i < result.Length; i++) { var value = i; for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0); result[i] = value; }
            return result;
        }
        private static void ValidateZip(string path, CancellationToken cancellation)
        {
            using (var input = File.OpenRead(path))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                // CRC is checked explicitly: ZipArchive on some Unity runtimes
                // does not validate it when an entry stream is read.
                var tailSize = (int)Math.Min(input.Length, 65557); input.Position = input.Length - tailSize; var tail = reader.ReadBytes(tailSize); var end = -1;
                for (var i = tail.Length - 22; i >= 0; i--) if (BitConverter.ToUInt32(tail, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(tail, i + 20) == tail.Length) { end = i; break; }
                if (end < 0) throw new IOException("ZIP終端が不正です。");
                long offset = BitConverter.ToUInt32(tail, end + 16), count = BitConverter.ToUInt16(tail, end + 10);
                if (BitConverter.ToUInt16(tail, end + 4) != 0 || BitConverter.ToUInt16(tail, end + 6) != 0) throw new IOException("分割ZIPは保存確認できません。");
                if (offset == uint.MaxValue || count == ushort.MaxValue)
                {
                    var locator = input.Length - tailSize + end - 20;
                    if (locator < 0) throw new IOException("ZIP64終端が不正です。");
                    input.Position = locator;
                    if (reader.ReadUInt32() != 0x07064b50 || reader.ReadUInt32() != 0) throw new IOException("ZIP64索引が不正です。");
                    var zip64 = reader.ReadInt64(); if (reader.ReadUInt32() != 1 || zip64 < 0 || zip64 >= input.Length) throw new IOException("分割ZIPは保存確認できません。");
                    input.Position = zip64;
                    if (reader.ReadUInt32() != 0x06064b50) throw new IOException("ZIP64終端が不正です。");
                    reader.ReadInt64(); reader.ReadUInt16(); reader.ReadUInt16();
                    if (reader.ReadUInt32() != 0 || reader.ReadUInt32() != 0) throw new IOException("分割ZIPは保存確認できません。");
                    var diskCount = reader.ReadInt64(); count = reader.ReadInt64(); reader.ReadInt64(); offset = reader.ReadInt64();
                    if (diskCount != count) throw new IOException("ZIP64の件数が不正です。");
                }
                if (count < 0 || count > 100000 || offset < 0 || offset >= input.Length) throw new IOException("ZIP索引が大きすぎるか不正です。");
                var crc = new List<uint>(); input.Position = offset;
                for (var i = 0; i < count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (reader.ReadUInt32() != 0x02014b50) throw new IOException("ZIP索引が不正です。");
                    var header = reader.ReadBytes(42); if (header.Length != 42) throw new EndOfStreamException();
                    crc.Add(BitConverter.ToUInt32(header, 12));
                    input.Position += BitConverter.ToUInt16(header, 24) + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);
                }
                input.Position = 0;
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
                {
                    if (archive.Entries.Count != count) throw new IOException("ZIP索引の件数が一致しません。");
                    long total = 0; var buffer = new byte[65536];
                    for (var i = 0; i < count; i++)
                    {
                        var entry = archive.Entries[i]; total = checked(total + entry.Length); if (total > MaxTotal) throw new IOException("ZIP展開サイズが大きすぎます。");
                        uint actual = uint.MaxValue; long length = 0;
                        using (var stream = entry.Open())
                        {
                            int n;
                            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellation.ThrowIfCancellationRequested(); length += n;
                                if (length > entry.Length) throw new IOException("ZIPの長さが不正です。");
                                for (var j = 0; j < n; j++) actual = (actual >> 8) ^ CrcTable[(actual ^ buffer[j]) & 255];
                            }
                        }
                        if (length != entry.Length || (actual ^ uint.MaxValue) != crc[i]) throw new IOException("ZIP CRCが不正です。");
                    }
                }
            }
        }
    }
}
