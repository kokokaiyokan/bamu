using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    public static class BridgeStorage
    {
        public static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        internal static string LegacySharedHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoothUnityBridge");
        internal static string SharedHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bamu");
        public static string LedgerPath => BamuMigration.AdoptFile(Path.Combine(Root, "ProjectSettings", "BoothUnityBridge.json"), Path.Combine(Root, "ProjectSettings", "bamu.json"));
        public static string TagsPath => BamuMigration.AdoptFile(Path.Combine(LegacySharedHome, "product-tags.json"), Path.Combine(SharedHome, "product-tags.json"));
        public static string TagDictionaryPath => BamuMigration.AdoptFile(Path.Combine(LegacySharedHome, "tag-dictionary.json"), Path.Combine(SharedHome, "tag-dictionary.json"));
        public static string PendingPath => BamuMigration.AdoptOnceFile(Path.Combine(Root, "UserSettings", "BoothUnityBridge", "pending.json"), Path.Combine(Root, "UserSettings", "bamu", "pending.json"));
        public static string SettingsKey => "bamu." + Root;
        internal static string LegacyReceiverHome => EditorPrefs.GetString("BoothUnityBridge.CompanionState", LegacySharedHome);
        internal static string ReceiverHome
        {
            get
            {
                var home = BamuPreferences.GetString("bamu.CompanionState", SharedHome);
                if (BamuMigration.SamePath(home, LegacySharedHome)) { home = SharedHome; EditorPrefs.SetString("bamu.CompanionState", home); }
                return home;
            }
        }
        private static readonly System.Collections.Generic.HashSet<string> AdoptedCaches = new System.Collections.Generic.HashSet<string>();
        internal static string CacheDirectory(string kind)
        {
            var current = Path.Combine(Root, "Library", "bamu", kind);
            if (!AdoptedCaches.Contains(current))
            {
                BamuMigration.CopyDirectoryMissing(Path.Combine(Root, "Library", "BoothUnityBridge", kind), current);
                AdoptedCaches.Add(current);
            }
            return current;
        }
        public static string DefaultDatabase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "pm.booth.library-manager", "data.db");
        public static string DatabasePath
        {
            get => BamuPreferences.GetString(SettingsKey + ".Database", DefaultDatabase);
            set => EditorPrefs.SetString(SettingsKey + ".Database", value);
        }
        public static string BlmExecutable
        {
            get => BamuPreferences.GetString("bamu.BlmExecutable", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BOOTH Library Manager", "booth_library_manager_client.exe"));
            set => EditorPrefs.SetString("bamu.BlmExecutable", value);
        }
        public static string LibraryOverride
        {
            get => BamuPreferences.GetString(SettingsKey + ".Library", "");
            set => EditorPrefs.SetString(SettingsKey + ".Library", value);
        }

        public static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                var result = JsonUtility.FromJson<T>(File.ReadAllText(path, Encoding.UTF8));
                if (result == null) throw new InvalidDataException("JSONが空です。");
                return result;
            }
            catch (Exception ex) { throw new InvalidDataException("台帳を読み取れません。上書きせず停止しました: " + path, ex); }
        }

        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonUtility.ToJson(value, true));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static ProjectLedger Load()
        {
            var ledger = Read<ProjectLedger>(LedgerPath) ?? new ProjectLedger();
            if (ledger.schemaVersion != 1 || ledger.products == null || ledger.links == null ||
                ledger.revisions == null || ledger.imports == null)
                throw new InvalidDataException("未対応または壊れたリンク台帳です。元のJSONは保持しました。");
            if (ledger.displayFolders == null) ledger.displayFolders = new System.Collections.Generic.List<DisplayFolderRule>();
            if (ledger.prefabSelections == null) ledger.prefabSelections = new System.Collections.Generic.List<ProductPrefabSelection>();
            return ledger;
        }
        public static void Save(ProjectLedger ledger) => Write(LedgerPath, ledger);

        public static void EnsureProduct(ProjectLedger ledger, ProductReference product)
        {
            var found = ledger.products.FirstOrDefault(p => p.key == product.key);
            if (found == null) ledger.products.Add(product);
            else { found.name = product.name; found.boothId = product.boothId; }
        }

        public static string CurrentPath(AssetLink link) => AssetDatabase.GUIDToAssetPath(link.guid);

        public static bool ContainsPath(string parent, string child) => !string.IsNullOrEmpty(parent) &&
            (string.Equals(parent, child, StringComparison.Ordinal) || child.StartsWith(parent + "/", StringComparison.Ordinal));

        public static bool Matches(AssetLink link, string selectedPath)
        {
            var current = CurrentPath(link);
            return !string.IsNullOrEmpty(current) && (current == selectedPath || (link.folder && link.includeDescendants && ContainsPath(current, selectedPath)));
        }

        public static bool AddLink(ProjectLedger ledger, string productKey, string path, LinkRole role,
            string method, string revisionId = "")
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Assets配下のフォルダまたはファイルを選択してください。");
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) throw new ArgumentException("アセットが見つかりません: " + path);
            // Do not overwrite an existing manual association on a subsequent import.
            if (ledger.links.Any(l => l.productKey == productKey && l.guid == guid && l.role == role)) return false;
            ledger.links.Add(new AssetLink
            {
                id = Guid.NewGuid().ToString("N"), productKey = productKey, guid = guid,
                lastPath = path, folder = AssetDatabase.IsValidFolder(path), role = role,
                includeDescendants = method == "Manual" && AssetDatabase.IsValidFolder(path),
                method = method, revisionId = revisionId, createdUtc = DateTime.UtcNow.ToString("o")
            });
            return true;
        }
    }
}
