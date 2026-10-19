using System;
using System.Linq;

namespace bamu
{
    public static class PrefabConversionLinks
    {
        public static bool IsPrefab(string path) => PrefabCatalog.IsPrefab(path);

        public static bool IsNewResult(string sourceGuid, string inputGuid, string previous, string result) =>
            !string.IsNullOrEmpty(sourceGuid) && sourceGuid == inputGuid && PrefabCatalog.IsPrefab(result) &&
            !string.Equals(previous?.Replace('\\', '/'), result, StringComparison.OrdinalIgnoreCase);

        public static void LinkResult(ProjectLedger ledger, ProductReference product, string sourceGuid, string result, string resultGuid, string method = "PrefabConversion")
        {
            if (!PrefabCatalog.IsPrefab(result) || string.IsNullOrEmpty(resultGuid) || resultGuid == sourceGuid ||
                UnityEditor.AssetDatabase.AssetPathToGUID(result) != resultGuid)
                throw new ArgumentException("変換後のprefabを確認できません。");
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("変換モジュールの識別子が必要です。");
            BridgeStorage.EnsureProduct(ledger, product);
            BridgeStorage.AddLink(ledger, product.key, result, LinkRole.ModifiedCopy, method);
            var selected = ledger.prefabSelections.FirstOrDefault(s => s.productKey == product.key)?.guids ?? new System.Collections.Generic.List<string> { sourceGuid };
            PrefabCatalog.SetSelected(ledger, product.key, selected.Concat(new[] { resultGuid }));
        }
    }
}
