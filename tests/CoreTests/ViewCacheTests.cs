using System;
using System.Collections.Generic;
using System.Linq;
using bamu;

internal static class ViewCacheTests
{
    internal static void Run(Action<bool, string> check)
    {
        var report = new ExistingAssetScanReport();
        var ledger = new ProjectLedger();
        var rule = new DisplayFolderRule { productKey = "booth:0", role = LinkRole.Original,
            folderGuid = "custom-folder", lastPath = "Assets/Author/Asset" };
        ledger.displayFolders.Add(rule);
        for (var productIndex = 0; productIndex < 20; productIndex++)
        {
            var product = new Product { key = "booth:" + productIndex, name = "Product " + productIndex };
            report.products.Add(new ScanProductResult { product = product });
            for (var i = 0; i < 250; i++)
            {
                var candidate = new ScanCandidate { product = product, guid = productIndex + ":" + i,
                    path = "Assets/Author/Asset/" + i + ".mat", selected = true };
                report.candidates.Add(candidate);
                ledger.links.Add(new AssetLink { productKey = product.key, guid = candidate.guid,
                    lastPath = candidate.path, method = "Scan", role = LinkRole.Original });
            }
        }
        var folderCalls = 0;
        string Folder(string guid) { folderCalls++; return "Assets/Author/Asset"; }
        var views = ScanReportView.Build(report, ledger.displayFolders, Folder);
        check(views.Count == 20 && views.Sum(v => v.candidateCount) == 5000 &&
            views.All(v => v.groups.Count == 1), "large scan view preserves product/folder grouping and all candidates");
        check(folderCalls == 1, "scan view resolves custom folder once for 5000 candidates");
        var item = views[0].groups[0].First();
        item.selected = false; item.alreadyLinked = true;
        check(!report.candidates[0].selected && report.candidates[0].alreadyLinked,
            "cached scan groups share selection and applied-link state with report");

        var calls = 0;
        string Resolve(AssetLink link) { calls++; return link.lastPath; }
        var cache = new LinkViewCache(ledger, Resolve, Folder);
        check(calls == 0, "manager creates link snapshot without eagerly traversing GUIDs");
        var groups = cache.Groups("booth:0");
        var firstCalls = calls;
        for (var frame = 0; frame < 100; frame++) cache.Groups("booth:0");
        check(firstCalls == 250 && calls == firstCalls && ReferenceEquals(groups, cache.Groups("booth:0")),
            "100 manager redraws reuse product groups without repeating GUID resolution");
        var related = cache.Related("Assets/Author/Asset");
        var reverseCalls = calls;
        for (var frame = 0; frame < 100; frame++) cache.Related("Assets/Author/Asset");
        check(related.Count == 20 && calls == reverseCalls && ReferenceEquals(related, cache.Related("Assets/Author/Asset")),
            "reverse lookup reuses snapshot across redraws");
        check(cache.Related("Assets/Author/Asset/NotImported.mat").Count == 0,
            "cached lookup does not claim unrelated files under displayed folder");
        ledger.links[0].lastPath = "Assets/Moved/NewAsset/a.mat";
        var refreshed = new LinkViewCache(ledger, Resolve, Folder);
        check(refreshed.Groups("booth:0").Any(g => g.path == "Assets/Moved/NewAsset"),
            "replacement snapshot reflects project move");
        var broken = new LinkViewCache(ledger, l => "", guid => "");
        check(broken.Related("Assets/Author/Asset").Count == 0,
            "replacement snapshot reflects deleted assets without claiming replacement folder");
    }
}
