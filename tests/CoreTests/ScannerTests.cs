using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using bamu;
using UnityEditor;
using UnityEngine;

internal static class ScannerTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Copy = "dddddddddddddddddddddddddddddddd";
    private static void FileAsset(string guid, string path, string contents)
    {
        var full = Path.Combine(BridgeStorage.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, contents);
        File.WriteAllText(full + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        AssetDatabase.Register(guid, path);
    }
    private static Product Product(string directory, string id = "900") => new Product
        { key = "booth:" + id, boothId = id, name = "照合テスト" + id, directory = directory };
    private static ExistingAssetScanReport Scan(Product product, bool copies = false) =>
        ExistingAssetScanner.Scan(new[] { product }, BridgeStorage.Load(), copies);

    public static void Run(string fixtures, Action<bool, string> check)
    {
        // A separate synthetic project keeps the stage-1 test fixtures untouched.
        var previousDataPath = Application.dataPath;
        Application.dataPath = Path.Combine(BridgeStorage.Root, "ScanProject", "Assets");
        Directory.CreateDirectory(Application.dataPath);
        try
        {
            var source = Path.Combine(BridgeStorage.Root, "Sources", "A"); Directory.CreateDirectory(source);
            File.Copy(Path.Combine(fixtures, "two-assets.unitypackage"), Path.Combine(source, "base.unitypackage"));
            var product = Product(source);
            var indexed = UnityPackageReader.Read(Path.Combine(source, "base.unitypackage"), null, true);
            check(indexed.All(e => !e.folder && e.contentHash.Length == 64), "scan index contains content hashes");
            FileAsset(A, "Assets/Author/OldAsset/Textures/ItemA.txt", "original A\n");
            FileAsset(B, "Assets/Author/OldAsset/Textures/ItemB.txt", "modified B\n");
            var contentA = UnityPackageReader.Hash(Path.Combine(BridgeStorage.Root, AssetDatabase.GUIDToAssetPath(A)));
            check(indexed.Single(e => e.guid == A).contentHash == contentA, "streaming package hash matches existing file bytes");
            var report = Scan(product);
            check(report.candidates.Count == 2 && report.candidates.All(c => c.kind == ScanMatchKind.Guid), "preexisting moved files found by GUID");
            check(report.candidates.Single(c => c.guid == A).ContentLabel == "内容一致" &&
                report.candidates.Single(c => c.guid == B).ContentLabel == "内容相違", "unchanged and modified content distinguished");
            check(!File.Exists(BridgeStorage.LedgerPath), "scan does not create or mutate ledger");
            check(report.products.Single().sourceGuids.Count == 2 && report.products.Single().Missing == 0, "unique package-file coverage");
            check(Scan(product).cacheHits == 1, "package content index reused on second scan");

            var sharedSource = Path.Combine(BridgeStorage.Root, "Sources", "B"); Directory.CreateDirectory(sharedSource);
            File.Copy(Path.Combine(fixtures, "shared.unitypackage"), Path.Combine(sharedSource, "shared.unitypackage"));
            var second = Product(sharedSource, "901");
            report = ExistingAssetScanner.Scan(new[] { product, second }, BridgeStorage.Load());
            check(report.candidates.Where(c => c.guid == A).All(c => c.sharedProducts.Count == 1 && !c.selected), "shared GUID across products is unchecked");
            check(report.candidates.Single(c => c.guid == B).selected, "unshared GUID defaults to selected");

            File.Copy(Path.Combine(fixtures, "revision.unitypackage"), Path.Combine(source, "revision.unitypackage"));
            report = Scan(product);
            check(report.candidates.Count == 2 && report.candidates.Single(c => c.guid == A).evidence.Count == 2, "multiple package versions merge evidence without duplicate candidates");
            check(report.candidates.Single(c => c.guid == A).ContentLabel == "配布版によって内容が異なる", "ambiguous distribution version is not declared installed");
            check(ExistingAssetScanner.Apply(report) == 2, "confirmed candidates create exact asset links");
            var ledger = BridgeStorage.Load();
            check(ledger.links.All(l => l.method == "Scan" && l.revisionId == "" && !l.includeDescendants &&
                l.evidencePackageHashes.Count > 0 && l.matchedContentHash.Length == 64), "scan links preserve evidence without assigning installed version");
            check(LinkDisplayGroups.Build(ledger.links, BridgeStorage.CurrentPath).Single().path == "Assets/Author/OldAsset", "scanned links use asset-folder display");
            check(ExistingAssetScanner.Apply(report) == 0, "apply is idempotent");
            check(Scan(product).candidates.All(c => c.alreadyLinked && !c.selected), "rescan respects existing links");

            // Preserve a deliberate manual link, even with a different role.
            ledger.links.Clear(); BridgeStorage.AddLink(ledger, product.key, AssetDatabase.GUIDToAssetPath(A), LinkRole.ModifiedCopy, "Manual");
            BridgeStorage.Save(ledger);
            report = Scan(product); ExistingAssetScanner.Apply(report);
            check(BridgeStorage.Load().links.Single(l => l.guid == A).method == "Manual" &&
                BridgeStorage.Load().links.Single(l => l.guid == A).role == LinkRole.ModifiedCopy, "manual association is not replaced or duplicated");

            var clean = new ProjectLedger(); BridgeStorage.Save(clean);
            report = Scan(product);
            File.WriteAllText(Path.Combine(BridgeStorage.Root, AssetDatabase.GUIDToAssetPath(B)), "changed after scan");
            var failed = false;
            try { ExistingAssetScanner.Apply(report); } catch (InvalidOperationException) { failed = true; }
            check(failed && BridgeStorage.Load().links.Count == 0, "post-scan modifications reject the entire apply before saving");

            AssetDatabase.Forget(A); AssetDatabase.Forget(B);
            FileAsset(Copy, "Assets/MyCopy/Asset/Copy.txt", "original A\n");
            report = Scan(product);
            check(report.candidates.Count == 0 && report.products.Single().Missing == 2, "missing GUID does not imply a link");
            report = Scan(product, true);
            check(report.candidates.Any(c => c.guid == Copy && c.kind == ScanMatchKind.Content && !c.selected), "changed-GUID copy offered as unchecked content candidate");
            FileAsset(new string('e', 32), "Assets/Generated/ItemB.txt", "unrelated bytes");
            report = Scan(product);
            check(report.candidates.Single().kind == ScanMatchKind.Path && !report.candidates.Single().selected, "path-only collision is a weak unchecked candidate");

            var folderSource = Path.Combine(BridgeStorage.Root, "Sources", "Folders"); Directory.CreateDirectory(folderSource);
            File.Copy(Path.Combine(fixtures, "folder-only.unitypackage"), Path.Combine(folderSource, "folder.unitypackage"));
            var folderIndex = UnityPackageReader.Read(Path.Combine(folderSource, "folder.unitypackage"), null, true);
            check(folderIndex.Single().folder, "package folder entries distinguished from file payloads");
            check(Scan(Product(folderSource, "902")).candidates.Count == 0, "folder GUID alone does not claim an installed product");

            var zipSource = Path.Combine(BridgeStorage.Root, "Sources", "Zip"); Directory.CreateDirectory(zipSource);
            File.Copy(Path.Combine(fixtures, "nested.zip"), Path.Combine(zipSource, "bundle.zip"));
            FileAsset(A, "Assets/Author/OldAsset/Textures/ItemA.txt", "original A\n");
            report = Scan(Product(zipSource, "903"));
            check(report.products.Single().packageCount == 1 && report.candidates.Any(c => c.guid == A), "scan supports unitypackage nested in ZIP");
            File.Copy(Path.Combine(fixtures, "bad-header.unitypackage"), Path.Combine(zipSource, "broken.unitypackage"));
            report = Scan(Product(zipSource, "903"));
            check(report.errors.Count == 1 && report.candidates.Any(c => c.guid == A), "one invalid package does not discard other matches");
            var beforeCancel = File.ReadAllText(BridgeStorage.LedgerPath);
            report = ExistingAssetScanner.Scan(new[] { product }, BridgeStorage.Load(), false, (message, fraction) => { throw new OperationCanceledException(); });
            check(report.cancelled && File.ReadAllText(BridgeStorage.LedgerPath) == beforeCancel, "cancellation leaves ledger unchanged");

            // The source hash, not the filename or timestamp, controls index identity.
            var indexReport = new ExistingAssetScanReport();
            var directory = Path.Combine(BridgeStorage.Root, "Library", "IndexReplacementTest");
            var replacement = Path.Combine(source, "replace.unitypackage");
            File.Copy(Path.Combine(fixtures, "shared.unitypackage"), replacement);
            var firstIndex = ExistingAssetScanner.ReadIndex(replacement, directory, null, indexReport);
            File.Copy(Path.Combine(fixtures, "revision.unitypackage"), replacement, true);
            var secondIndex = ExistingAssetScanner.ReadIndex(replacement, directory, null, indexReport);
            check(firstIndex.packageHash != secondIndex.packageHash && indexReport.cacheHits == 0, "same-name replacement invalidates index");
            File.WriteAllText(Path.Combine(directory, secondIndex.packageHash + ".json"), "{bad");
            check(ExistingAssetScanner.ReadIndex(replacement, directory, null, indexReport).entries.Count == 1, "corrupt disposable index is regenerated");

        }
        finally { Application.dataPath = previousDataPath; AssetDatabase.Forget(A); AssetDatabase.Forget(B); AssetDatabase.Forget(Copy); }
    }

}
