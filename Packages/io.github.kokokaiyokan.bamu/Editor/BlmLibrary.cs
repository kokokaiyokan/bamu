using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace bamu
{
    // Windows ships winsqlite3.dll. Open READONLY, without IMMUTABLE (BLM may use WAL).
    internal sealed class ReadOnlySqlite : IDisposable
    {
        private IntPtr db;
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr stmt, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int column);

        public ReadOnlySqlite(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("初期版はWindows専用です。");
            if (!File.Exists(path)) throw new FileNotFoundException("BLMのdata.dbが見つかりません。", path);
            var rc = sqlite3_open_v2(Utf8(path), out db, 1, IntPtr.Zero); // SQLITE_OPEN_READONLY
            if (rc != 0) { Dispose(); throw new IOException("BLMのDBを読み取り専用で開けません (SQLite " + rc + ")。"); }
            sqlite3_busy_timeout(db, 1500);
        }
        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
        private static IOException Error(int rc) => new IOException(rc == 5 || rc == 6
            ? "BLMのDBが使用中です。少し待って再読み込みしてください。"
            : "BLMのDBを読み取れません。未対応スキーマまたはDBエラー (SQLite " + rc + ")。");

        public List<object[]> Query(string sql)
        {
            IntPtr stmt;
            var rc = sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero);
            try
            {
                if (rc != 0) throw Error(rc);
                var rows = new List<object[]>();
                while ((rc = sqlite3_step(stmt)) == 100)
                {
                    var row = new object[sqlite3_column_count(stmt)];
                    for (var i = 0; i < row.Length; i++)
                    {
                        var type = sqlite3_column_type(stmt, i);
                        if (type == 5) continue;
                        var ptr = type == 4 ? sqlite3_column_blob(stmt, i) : sqlite3_column_text(stmt, i);
                        var bytes = new byte[sqlite3_column_bytes(stmt, i)];
                        if (bytes.Length > 0) Marshal.Copy(ptr, bytes, 0, bytes.Length);
                        row[i] = type == 4 ? (object)bytes : Encoding.UTF8.GetString(bytes);
                    }
                    rows.Add(row);
                }
                if (rc != 101) throw Error(rc);
                return rows;
            }
            finally { if (stmt != IntPtr.Zero) sqlite3_finalize(stmt); }
        }
        public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; } }
    }

    public sealed class BlmLibrary
    {
        public string root, schema;
        public List<Product> products = new List<Product>();
        private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        public static BlmLibrary Load(string databasePath, string directoryOverride = "")
        {
            var library = new BlmLibrary();
            using (var db = new ReadOnlySqlite(databasePath))
            {
                // A single read transaction also includes any committed WAL contents.
                db.Query("BEGIN");
                library.schema = Text(db.Query("SELECT version FROM schema_version").Single()[0]);
                if (library.schema != "20261005")
                    throw new InvalidDataException("未対応のBLMスキーマです: " + library.schema + "。DBは変更していません。");
                var preferences = db.Query("SELECT item_directory_path FROM preferences LIMIT 1");
                if (preferences.Count > 0)
                {
                    var raw = preferences[0][0];
                    library.root = raw is byte[] bytes ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : Text(raw);
                }
                if (!string.IsNullOrWhiteSpace(directoryOverride)) library.root = directoryOverride;
                const string sql = @"SELECT r.id, r.booth_item_id,
                    COALESCE(o.name,b.name,u.name,r.id), COALESCE(s.name,u.shop_name,''),
                    b.thumbnail_url, u.thumbnail_filename
                    FROM registered_items r
                    LEFT JOIN booth_items b ON b.id=r.booth_item_id
                    LEFT JOIN overwritten_booth_items o ON o.booth_item_id=b.id
                    LEFT JOIN shops s ON s.subdomain=b.shop_subdomain
                    LEFT JOIN user_item_info u ON u.id=r.user_item_info_id
                    ORDER BY r.created_at DESC, r.id";
                foreach (var row in db.Query(sql))
                {
                    var registration = Text(row[0]);
                    // Treat DB strings as data, never as arbitrary filesystem traversal.
                    if (registration.Length == 0 || registration.IndexOfAny(new[] {'/', '\\', ':'}) >= 0 || registration == "..")
                        throw new InvalidDataException("BLMの保存フォルダ識別子が不正です。");
                    var boothId = Text(row[1]);
                    var product = new Product
                    {
                        registrationId = registration, boothId = boothId,
                        key = boothId.Length > 0 ? "booth:" + boothId : "blm:" + registration,
                        name = Text(row[2]), shop = Text(row[3]), thumbnailUrl = Text(row[4]),
                        directory = string.IsNullOrEmpty(library.root) ? "" : Path.Combine(library.root, registration)
                    };
                    var thumbnail = Text(row[5]);
                    if (thumbnail.Length > 0 && Path.GetFileName(thumbnail) == thumbnail && !string.IsNullOrEmpty(product.directory))
                        product.localThumbnail = Path.Combine(product.directory, thumbnail);
                    library.products.Add(product);
                }
                db.Query("COMMIT");
            }
            return library;
        }

        public static List<string> FindPackages(Product product)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(product.directory) || !Directory.Exists(product.directory)) return results;
            Walk(product.directory, results);
            return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private static void Walk(string path, List<string> results)
        {
            if (Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal) &&
                Path.GetFileName(Path.GetDirectoryName(path)) == "_BoothUnityBridgeVersions") return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in Directory.GetFiles(path))
                if (file.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    results.Add(file);
            foreach (var directory in Directory.GetDirectories(path)) Walk(directory, results);
        }
    }
}
