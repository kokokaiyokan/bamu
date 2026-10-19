using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace bamu
{
    internal static class BamuMigration
    {
        internal static string AdoptOnceFile(string previous, string current)
        {
            var marker = current + ".legacy-imported";
            if (File.Exists(marker)) return current;
            AdoptFile(previous, current);
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            using (var file = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) file.Flush(true);
            return current;
        }
        internal static string AdoptFile(string previous, string current, CancellationToken cancellation = default)
        {
            if (File.Exists(current) || !File.Exists(previous) || SamePath(previous, current)) return current;
            RejectLink(previous);
            Directory.CreateDirectory(Path.GetDirectoryName(current));
            var temporary = current + "." + Guid.NewGuid().ToString("N") + ".migration.tmp";
            try
            {
                using (var input = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[128 * 1024]; int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                    cancellation.ThrowIfCancellationRequested(); output.Flush(true);
                }
                try { File.Move(temporary, current); }
                catch (IOException) when (File.Exists(current)) { /* Another Editor completed this file first. */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return current;
        }

        internal static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("移行元にリンク／ジャンクションがあるため自動移行を停止しました: " + path);
        }

        internal static void CopyDirectoryMissing(string previous, string current, CancellationToken cancellation = default)
        {
            if (!Directory.Exists(previous) || SamePath(previous, current)) return;
            RejectLink(previous);
            foreach (var path in Directory.EnumerateFileSystemEntries(previous))
            {
                cancellation.ThrowIfCancellationRequested();
                RejectLink(path);
                var destination = Path.Combine(current, Path.GetFileName(path));
                if (Directory.Exists(path)) CopyDirectoryMissing(path, destination, cancellation);
                else if (!path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) AdoptFile(path, destination, cancellation);
            }
        }

        // Called only while both receiver leases are held. Do not snapshot a live old writer.
        internal static void ReceiverData(string previous, string current, CancellationToken cancellation)
        {
            if (SamePath(previous, current)) return;
            foreach (var folder in new[] { "blm-objects", "blm-receipts", "objects", "receipts" })
                CopyDirectoryMissing(Path.Combine(previous, folder), Path.Combine(current, folder), cancellation);
            cancellation.ThrowIfCancellationRequested();
            AdoptFile(Path.Combine(previous, "state.json"), Path.Combine(current, "state.json"));
        }

        internal static IDisposable AcquireReceiverLeases(string current, string previous)
        {
            var acquired = new List<FileStream>();
            try
            {
                foreach (var folder in (SamePath(previous, current) ? new[] { current } : new[] { previous, current }))
                {
                    Directory.CreateDirectory(folder);
                    var stream = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    try { stream.Lock(0, 1); acquired.Add(stream); }
                    catch { stream.Dispose(); throw; }
                }
                return new Leases(acquired);
            }
            catch { foreach (var stream in acquired) stream.Dispose(); throw; }
        }

        private sealed class Leases : IDisposable
        {
            private readonly List<FileStream> streams;
            internal Leases(List<FileStream> streams) { this.streams = streams; }
            public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
        }
    }
}
