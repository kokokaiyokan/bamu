using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using static bamu.BridgeJson;

namespace bamu
{
    internal static class ReceiverCredentials
    {
        internal static string ReadToken(string home)
        {
            // Allow atomic replacement by another Unity project while reading.
            using (var stream = new FileStream(Path.Combine(home, "connection.json"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var token = Str(Map(Parse(reader.ReadToEnd())), "token");
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("接続キーを確認してください。");
                return token;
            }
        }

        internal static string LoadOrCreate(string home, string legacyHome, CancellationToken cancellation = default)
        {
            using (Acquire(home, cancellation))
            {
                var connection = Path.Combine(home, "connection.json");
                BamuMigration.AdoptFile(Path.Combine(legacyHome, "connection.json"), connection, cancellation);
                if (File.Exists(connection)) return ReadToken(home);
                return WriteNew(home);
            }
        }

        internal static string Regenerate(string home)
        {
            using (Acquire(home, CancellationToken.None)) return WriteNew(home);
        }

        private static string WriteNew(string home)
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Save(Path.Combine(home, "connection.json"), Obj("port", 48763, "token", token));
            return token;
        }

        private static FileStream Acquire(string home, CancellationToken cancellation)
        {
            Directory.CreateDirectory(home);
            for (var attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try { return new FileStream(Path.Combine(home, "connection.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (attempt < 49) { Thread.Sleep(20); }
            }
        }
    }
}
