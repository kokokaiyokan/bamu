using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using bamu;
using UnityEditor;

internal static class PrefabTests
{
    internal static void Run(Action<bool, string> check)
    {
        var previous = UnityEngine.Application.dataPath;
        UnityEngine.Application.dataPath = Path.Combine(Path.GetDirectoryName(previous), "prefab-tests", "Assets");
        Directory.CreateDirectory(UnityEngine.Application.dataPath);
        try { RunIsolated(check); }
        finally { UnityEngine.Application.dataPath = previous; }
    }

    private static void RunIsolated(Action<bool, string> check)
    {
        const string key = "booth:prefab-test";
        var ledger = new ProjectLedger();
        var paths = new Dictionary<string, string>
        {
            ["red"] = "Assets/Author/Outfit/Red.prefab", ["default"] = "Assets/Author/Outfit/Default.prefab",
            ["copy"] = "Assets/Converted/Default.prefab", ["mat"] = "Assets/Author/Outfit/A.mat",
            ["structural"] = "Assets/Author", ["manual-folder"] = "Assets/Manual", ["manual"] = "Assets/Manual/Other.prefab"
        };
        foreach (var item in paths) AssetDatabase.Register(item.Key, item.Value);
        void Link(string guid, LinkRole role = LinkRole.Original, bool folder = false, bool recursive = false) =>
            ledger.links.Add(new AssetLink { guid = guid, productKey = key, role = role, folder = folder, includeDescendants = recursive, method = "Scan", lastPath = paths[guid] });
        Link("red"); Link("default"); Link("copy", LinkRole.ModifiedCopy); Link("mat"); Link("structural", folder: true);
        var finds = 0;
        string Resolve(string guid) => paths.TryGetValue(guid, out var path) ? path : "";
        IEnumerable<string> Find(string folder) { finds++; return new[] { "manual" }; }
        var candidates = PrefabCatalog.Candidates(ledger, key, Resolve, Find);
        check(candidates.Count == 3 && finds == 0, "prefab discovery ignores materials and nonrecursive structural folders");
        check(PrefabCatalog.Selected(ledger, key, candidates).SequenceEqual(new[] { "default" }), "prefab representative prefers original default and hides color variants");
        Link("manual-folder", folder: true, recursive: true);
        candidates = PrefabCatalog.Candidates(ledger, key, Resolve, Find);
        check(finds == 1 && candidates.Any(c => c.guid == "manual"), "explicit recursive manual folder discovers contained prefabs");
        PrefabCatalog.SetSelected(ledger, key, new[] { "red", "red", "manual" });
        check(PrefabCatalog.Selected(ledger, key, candidates).SequenceEqual(new[] { "red", "manual" }), "manual prefab selection preserves order and removes duplicates");
        PrefabCatalog.SetSelected(ledger, key, Array.Empty<string>());
        check(PrefabCatalog.Selected(ledger, key, candidates).Count == 0, "explicit empty prefab selection does not respawn automatic representative");
        PrefabCatalog.SetSelected(ledger, key, new[] { "red" });
        paths["red"] = "Assets/Moved/Outfit.prefab";
        check(PrefabCatalog.Candidates(ledger, key, Resolve, Find).Any(c => c.guid == "red" && c.path == paths["red"]), "prefab GUID follows moved asset");
        paths.Remove("red");
        check(PrefabCatalog.Selected(ledger, key, candidates).Contains("red") && !PrefabCatalog.Candidates(ledger, key, Resolve, Find).Any(c => c.guid == "red"), "missing selected prefab stays recorded for explicit missing-link display");
        check(!PrefabCatalog.IsPrefab("Packages/Test/A.prefab") && !PrefabCatalog.IsPrefab("Assets/Test/A.fbx"), "prefab selection accepts only project prefab assets");

        var product = new ProductReference { key = key, name = "Prefab test" };
        var result = "Assets/PrefabTest/Converted.prefab";
        var guid = "prefab-converted-test";
        check(!PrefabConversionLinks.IsNewResult("default", "other", "", result), "converter result rejects changed input product");
        check(!PrefabConversionLinks.IsNewResult("default", "default", result, result), "converter result rejects old output");
        check(!PrefabConversionLinks.IsNewResult("default", "default", "", "Assets/Output/file.fbx"), "converter output must be a prefab");
        check(PrefabConversionLinks.IsNewResult("default", "default", "", result), "converter accepts new output for tracked source");
        Directory.CreateDirectory(Path.Combine(BridgeStorage.Root, "Assets/PrefabTest"));
        File.WriteAllText(Path.Combine(BridgeStorage.Root, result), "isolated prefab fixture");
        AssetDatabase.Register(guid, result);
        PrefabCatalog.SetSelected(ledger, key, new[] { "default", "copy" });
        PrefabConversionLinks.LinkResult(ledger, product, "default", result, guid, "TestConverter");
        check(ledger.links.Any(l => l.productKey == key && l.guid == guid && l.role == LinkRole.ModifiedCopy && l.method == "TestConverter"), "converter result records exact modified-copy product link");
        check(PrefabCatalog.Selected(ledger, key, candidates).SequenceEqual(new[] { "default", "copy", guid }), "converter appends converted prefab without dropping manual choices");
        PrefabConversionLinks.LinkResult(ledger, product, "default", result, guid, "TestConverter");
        check(ledger.links.Count(l => l.guid == guid) == 1 && ledger.prefabSelections.Single(s => s.productKey == key).guids.Count == 3, "repeated converter completion is idempotent");
        BridgeStorage.Save(ledger);
        check(BridgeStorage.Load().prefabSelections.Single(s => s.productKey == key).guids.Contains(guid), "prefab choices persist in project ledger");
        var failed = false;
        try { PrefabConversionLinks.LinkResult(ledger, product, "default", result, "wrong-guid", "TestConverter"); }
        catch (ArgumentException) { failed = true; }
        check(failed, "converter rejects mismatched output GUID before linking");
        ledger.prefabSelections.RemoveAll(s => s.productKey == key);
        PrefabConversionLinks.LinkResult(ledger, product, "default", result, guid, "TestConverter");
        check(ledger.prefabSelections.Single(s => s.productKey == key).guids.SequenceEqual(new[] { "default", guid }), "converter keeps source representative when no manual choices exist");
    }
}
