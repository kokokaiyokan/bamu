using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using bamu;
using UnityEngine;

internal static class GroupingTests
{
    private static AssetLink Link(string path, string method = "Import", bool folder = false, string product = "booth:7229780") => new AssetLink
    {
        id = Guid.NewGuid().ToString("N"), guid = Guid.NewGuid().ToString("N"), lastPath = path,
        method = method, folder = folder, productKey = product, role = LinkRole.Original
    };

    public static void Run(Action<bool, string> check, string referenceLedger)
    {
        var links = new List<AssetLink>
        {
            Link("Assets/NFD_Item/NFD_TwinkleTulleRibbon/Common/Materials/Ribbon.mat"),
            Link("Assets/NFD_Item/NFD_TwinkleTulleRibbon/Common/Textures/Ribbon.png"),
            Link("Assets/NFD_Item/NFD_TwinkleTulleRibbon/Prefab/Ribbon.prefab"),
            Link("Assets/NFD/_Naffunado_CommonTextures/Normal.png")
        };
        var before = JsonUtility.ToJson(new ProjectLedger { links = links }, true);
        var groups = LinkDisplayGroups.Build(links, l => l.lastPath);
        check(groups.Count == 2, "imported files display as asset and common-texture folders");
        check(groups.Any(g => g.path == "Assets/NFD_Item/NFD_TwinkleTulleRibbon" && g.links.Count == 3) &&
            groups.Any(g => g.path == "Assets/NFD/_Naffunado_CommonTextures" && g.links.Count == 1), "author/asset hierarchy grouping");
        check(before == JsonUtility.ToJson(new ProjectLedger { links = links }, true), "grouping leaves legacy per-file records untouched");
        check(LinkDisplayGroups.RelatedProducts(links, "Assets/NFD_Item/NFD_TwinkleTulleRibbon", l => l.lastPath).Contains("booth:7229780"),
            "displayed asset folder supports product reverse lookup");
        check(LinkDisplayGroups.RelatedProducts(links, links[0].lastPath, l => l.lastPath).Contains("booth:7229780"),
            "imported files retain exact reverse lookup");
        check(LinkDisplayGroups.RelatedProducts(links, "Assets/NFD_Item/NFD_TwinkleTulleRibbon/NotImported.mat", l => l.lastPath).Count == 0,
            "displayed folder does not reverse-link unrelated files");
        check(LinkDisplayGroups.RelatedProducts(links, "Assets/NFD_Item/NFD_TwinkleTulleRibbon", l => "").Count == 0,
            "fully broken group does not claim a replacement folder");

        links.Add(Link("Assets/NFD_Item", folder: true));
        links.Add(Link("Assets/NFD_Item/NFD_TwinkleTulleRibbon", folder: true));
        groups = LinkDisplayGroups.Build(links, l => l.lastPath);
        check(groups.Count == 2 && groups.Sum(g => g.links.Count) == links.Count, "structural parent folders do not create redundant rows");
        check(groups.SelectMany(g => g.links).Select(l => l.id).Distinct().Count() == links.Count, "each link belongs to exactly one removal group");

        var mixed = new[] { Link("Assets/Author/Asset/Textures/A.png"), Link("Assets/Author/Asset/Textures/B.png"),
            Link("Assets/Shared/Shader/Shader.shader"), Link("Assets/Manual.txt", "Manual") };
        groups = LinkDisplayGroups.Build(mixed, l => l.lastPath);
        check(groups.Count == 3 && groups.Single(g => !g.groupedImport).path == "Assets/Manual.txt", "explicit manual file links are preserved");
        var assetGroup = groups.Single(g => g.path == "Assets/Author/Asset");
        var removed = new HashSet<string>(assetGroup.links.Select(l => l.id));
        check(mixed.Where(l => !removed.Contains(l.id)).Count() == 2 &&
            !removed.Contains(mixed[2].id) && !removed.Contains(mixed[3].id), "unlink group keeps shared and manual links");
        check(!BridgeStorage.Matches(mixed[0], "Assets/Author/Asset/NotImported.png"), "display grouping does not add recursive ownership");

        groups = LinkDisplayGroups.Build(new[] { Link("Assets/Root.txt"), Link("Assets/Standalone/A.png"),
            Link("Assets/Author/Asset/Mat/A.mat") }, l => l.lastPath);
        check(groups.Any(g => g.path == "Assets") && groups.Any(g => g.path == "Assets/Standalone"), "shallow layouts also display folders only");
        var moved = Link("Assets/OldAuthor/OldAsset/A.mat");
        groups = LinkDisplayGroups.Build(new[] { moved }, l => "Assets/NewAuthor/NewAsset/Materials/A.mat");
        check(groups.Single().path == "Assets/NewAuthor/NewAsset", "groups use current GUID location after move");
        groups = LinkDisplayGroups.Build(new[] { moved }, l => "");
        check(groups.Single().path == "Assets/OldAuthor/OldAsset" && groups.Single().missingCount == 1, "missing files retain grouped last-known location");
        var otherProduct = Link(moved.lastPath, product: "booth:other");
        groups = LinkDisplayGroups.Build(new[] { moved, otherProduct }, l => l.lastPath);
        check(groups.Count == 2, "products sharing a folder are not merged");
        var copy = Link(moved.lastPath); copy.role = LinkRole.ModifiedCopy;
        groups = LinkDisplayGroups.Build(new[] { moved, copy }, l => l.lastPath);
        check(groups.Count == 2, "different association roles are preserved");

        if (!string.IsNullOrEmpty(referenceLedger))
        {
            var ledger = JsonUtility.FromJson<ProjectLedger>(File.ReadAllText(referenceLedger));
            var target = ledger.links.Where(l => l.productKey == "booth:7229780").ToList();
            check(target.Count > 0, "TwinkleTulleRibbon reference ledger contains imported links");
            groups = LinkDisplayGroups.Build(target, l => l.lastPath);
            check(groups.Count == 2 && groups.Sum(g => g.links.Count) == target.Count &&
                groups.Any(g => g.path == "Assets/NFD_Item/NFD_TwinkleTulleRibbon") &&
                groups.Any(g => g.path == "Assets/NFD/_Naffunado_CommonTextures"), "actual TwinkleTulleRibbon import groups into two folders");
            var project = Directory.GetParent(Path.GetDirectoryName(referenceLedger)).FullName;
            check(groups.All(g => Directory.Exists(Path.Combine(project, g.path))), "both real reference folders exist for Project selection");
        }
    }
}
