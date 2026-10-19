using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using bamu;
using UnityEditor;
using UnityEngine;

internal static class FolderEditorTests
{
    private static readonly List<string> Guids = new List<string>();
    private static string Register(string path, bool folder = false)
    {
        var guid = Guid.NewGuid().ToString("N"); Guids.Add(guid);
        AssetDatabase.Register(guid, path);
        if (folder) Directory.CreateDirectory(Path.Combine(BridgeStorage.Root, path));
        return guid;
    }
    private static AssetLink Link(string path, string method = "Import", LinkRole role = LinkRole.Original, string product = "booth:folder") =>
        new AssetLink { id = Guid.NewGuid().ToString("N"), productKey = product, guid = Register(path), lastPath = path,
            method = method, role = role, revisionId = "original-revision" };
    private static List<LinkDisplayGroup> Groups(ProjectLedger ledger) => LinkDisplayGroups.Build(ledger.links, BridgeStorage.CurrentPath, ledger.displayFolders);

    public static void Run(Action<bool, string> check)
    {
        var oldDataPath = Application.dataPath;
        Application.dataPath = Path.Combine(BridgeStorage.Root, "FolderEditorProject", "Assets");
        Directory.CreateDirectory(Application.dataPath);
        try
        {
            const string asset = "Assets/Studio/Collection/Avatar/Outfit";
            const string material = asset + "/Materials";
            const string common = "Assets/Shared/Textures";
            Register(asset, true); Register(material, true); Register(common, true); Register("Assets/Studio/Collection", true);
            var ledger = new ProjectLedger();
            ledger.links.Add(Link(material + "/A.mat"));
            ledger.links.Add(Link(asset + "/Textures/A.png", "Scan"));
            ledger.links.Add(Link(common + "/Shared.png"));
            var originalLinks = JsonUtility.ToJson(new ProjectLedger { links = ledger.links }, true);
            var automatic = Groups(ledger).Single(g => g.path == "Assets/Studio/Collection");
            LinkFolderEditor.Change(ledger, automatic.links.Select(l => l.id), asset);
            check(Groups(ledger).Any(g => g.path == asset && g.links.Count == 2), "custom deep asset folder overrides fixed hierarchy");
            check(Groups(ledger).Any(g => g.path == common && g.links.Count == 1), "changing root preserves common-material group");
            check(JsonUtility.ToJson(new ProjectLedger { links = ledger.links }, true) == originalLinks,
                "custom display folder preserves exact GUID links and import evidence");
            check(LinkDisplayGroups.RelatedProducts(ledger.links, asset, BridgeStorage.CurrentPath, ledger.displayFolders).Contains("booth:folder"),
                "custom folder supports reverse product selection");
            check(LinkDisplayGroups.RelatedProducts(ledger.links, asset + "/NotImported.mat", BridgeStorage.CurrentPath, ledger.displayFolders).Count == 0,
                "custom folder does not claim unrelated descendants");
            BridgeStorage.Save(ledger); ledger = BridgeStorage.Load();
            check(Groups(ledger).Any(g => g.path == asset), "folder preference survives ledger reload");
            ledger.links.Add(Link(asset + "/Mesh/New.fbx"));
            check(Groups(ledger).Single(g => g.path == asset).links.Count == 3, "future imports inherit product display folder");
            ledger.links.Add(Link(asset + "/Copy/A.mat", role: LinkRole.ModifiedCopy));
            ledger.links.Add(Link(asset + "/Other/A.mat", product: "booth:another"));
            check(Groups(ledger).Where(g => g.productKey == "booth:another" || g.role == LinkRole.ModifiedCopy).All(g => string.IsNullOrEmpty(g.overrideId)),
                "folder settings are independent by product and role");
            ledger.links.RemoveAll(l => l.productKey == "booth:another" || l.role == LinkRole.ModifiedCopy);

            var oldGroup = Groups(ledger).Single(g => g.path == asset);
            LinkFolderEditor.Change(ledger, oldGroup.links.Select(l => l.id), material, oldGroup.overrideId);
            check(Groups(ledger).Single(g => g.path == material).links.Count == 1 && Groups(ledger).Sum(g => g.links.Count) == ledger.links.Count,
                "narrower folder splits group without losing remaining links");
            var narrow = Groups(ledger).Single(g => g.path == material);
            LinkFolderEditor.Change(ledger, narrow.links.Select(l => l.id), "Assets/Studio/Collection", narrow.overrideId);
            check(Groups(ledger).Single(g => g.path == "Assets/Studio/Collection").links.Count == 3 && ledger.displayFolders.Count == 1,
                "broader folder merges nested groups without changing shared root");

            Register("Assets/Unrelated", true);
            var rulesBefore = JsonUtility.ToJson(ledger, true);
            var rejected = false;
            try { LinkFolderEditor.Change(ledger, narrow.links.Select(l => l.id), "Assets/Unrelated"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && JsonUtility.ToJson(ledger, true) == rulesBefore, "unrelated automatic display target rejected without mutation");
            rejected = false;
            try { LinkFolderEditor.Change(ledger, narrow.links.Select(l => l.id), "Packages/Other"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "display target must be an Assets folder");

            var rule = ledger.displayFolders.Single();
            AssetDatabase.Register(rule.folderGuid, "Assets/RenamedCollection");
            foreach (var link in ledger.links.Where(l => l.lastPath.StartsWith("Assets/Studio/Collection/", StringComparison.Ordinal)))
                AssetDatabase.Register(link.guid, link.lastPath.Replace("Assets/Studio/Collection", "Assets/RenamedCollection"));
            check(Groups(ledger).Any(g => g.path == "Assets/RenamedCollection"), "custom folder follows GUID after move/rename");
            AssetDatabase.Forget(rule.folderGuid);
            foreach (var link in ledger.links.Where(l => l.lastPath.StartsWith("Assets/Studio/Collection/", StringComparison.Ordinal)))
                AssetDatabase.Register(link.guid, link.lastPath);
            check(!LinkDisplayGroups.RelatedProducts(ledger.links, "Assets/Studio/Collection", BridgeStorage.CurrentPath, ledger.displayFolders).Contains("booth:folder"),
                "deleted custom folder does not claim a same-path replacement");
            ledger.displayFolders.Clear();
            check(Groups(ledger).All(g => string.IsNullOrEmpty(g.overrideId)), "automatic grouping is restored without custom rules");

            var manualPath = "Assets/ManualOriginal"; Register(manualPath, true);
            var newPath = "Assets/ManualNew"; Register(newPath, true);
            var manual = Link(manualPath, "Manual", LinkRole.Reference); manual.folder = true; manual.includeDescendants = true;
            ledger.links.Add(manual);
            LinkFolderEditor.Change(ledger, new[] { manual.id }, newPath);
            check(BridgeStorage.CurrentPath(manual) == newPath && manual.method == "Manual" && manual.role == LinkRole.Reference && manual.includeDescendants,
                "manual folder link can change its actual target while retaining role");
            check(Directory.Exists(Path.Combine(BridgeStorage.Root, manualPath)), "manual retarget does not move source folder");
            var duplicate = new AssetLink { id = Guid.NewGuid().ToString("N"), productKey = manual.productKey,
                guid = manual.guid, role = manual.role, method = "Manual", lastPath = newPath, folder = true };
            ledger.links.Add(duplicate);
            LinkFolderEditor.Change(ledger, new[] { manual.id }, newPath);
            check(ledger.links.Count(l => l.guid == manual.guid && l.role == manual.role) == 1 && ledger.links.Contains(manual),
                "manual retarget merges duplicate target without losing edited link");
            // Simulate an old JSON document that predates optional display-folder settings.
            File.WriteAllText(BridgeStorage.LedgerPath,
                "{\"schemaVersion\":1,\"products\":[],\"links\":[],\"revisions\":[],\"imports\":[]}");
            check(BridgeStorage.Load().displayFolders.Count == 0, "old ledger schema loads without migration");
        }
        finally
        {
            Application.dataPath = oldDataPath;
            foreach (var guid in Guids) AssetDatabase.Forget(guid);
            Guids.Clear();
        }
    }
}
