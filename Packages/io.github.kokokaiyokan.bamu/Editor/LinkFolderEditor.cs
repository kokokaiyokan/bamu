using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace bamu
{
    public static class LinkFolderEditor
    {
        public static void Change(ProjectLedger ledger, IEnumerable<string> linkIds, string folderPath, string previousRuleId = null)
        {
            if (!UnityPackageReader.ValidAssetPath(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                throw new ArgumentException("Assets配下のフォルダを指定してください。");
            var guid = AssetDatabase.AssetPathToGUID(folderPath);
            if (string.IsNullOrEmpty(guid)) throw new ArgumentException("フォルダのGUIDを取得できません。");
            var ids = new HashSet<string>(linkIds);
            var links = ledger.links.Where(l => ids.Contains(l.id)).ToList();
            if (links.Count == 0 || links.Count != ids.Count) throw new InvalidOperationException("リンクが変更されています。Managerを開き直してください。");
            var first = links[0];
            if (links.Any(l => l.productKey != first.productKey || l.role != first.role))
                throw new ArgumentException("同じ商品・種類のリンクを選択してください。");
            if (links.All(l => l.method == "Manual") && links.Count == 1)
            {
                var duplicate = ledger.links.FirstOrDefault(l => l.id != first.id && l.productKey == first.productKey && l.role == first.role && l.guid == guid);
                if (duplicate != null) ledger.links.Remove(duplicate);
                first.guid = guid; first.lastPath = folderPath; first.folder = true; first.includeDescendants = true;
                return;
            }
            if (links.Any(l => l.method != "Import" && l.method != "Scan")) throw new ArgumentException("自動リンクのグループを選択してください。");
            if (!links.Any(l => BridgeStorage.ContainsPath(folderPath, EffectivePath(l))))
                throw new ArgumentException("表示中のリンク先を含むフォルダを指定してください。別のフォルダを関連付ける場合は手動リンクを利用できます。");
            if (ledger.displayFolders == null) ledger.displayFolders = new List<DisplayFolderRule>();
            // A new broader root intentionally merges nested display rules for
            // this product/role; sibling products and shared-material roots stay independent.
            ledger.displayFolders.RemoveAll(r => r.productKey == first.productKey && r.role == first.role &&
                (r.id == previousRuleId || BridgeStorage.ContainsPath(folderPath, LinkDisplayGroups.RulePath(r))));
            ledger.displayFolders.Add(new DisplayFolderRule { id = Guid.NewGuid().ToString("N"), productKey = first.productKey,
                role = first.role, folderGuid = guid, lastPath = folderPath });
        }
        private static string EffectivePath(AssetLink link)
        {
            var current = BridgeStorage.CurrentPath(link);
            return string.IsNullOrEmpty(current) ? link.lastPath : current;
        }
    }
}
