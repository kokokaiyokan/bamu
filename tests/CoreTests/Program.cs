using System;
using System.IO;
using System.Linq;
using System.Reflection;
using bamu;
using UnityEditor;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static string root, fixtures;
    private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static Product Product => new Product { key = "booth:123", boothId = "123", name = "テスト商品" };
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        checks++; Console.WriteLine("PASS: " + name);
    }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException)
        { Check(true, name); return; }
        throw new Exception("FAIL: no rejection: " + name);
    }
    private static string Package => Path.Combine(fixtures, "two-assets.unitypackage");
    private static void Begin() => ImportCoordinator.Begin(Product, Package, "test", false);
    private static void AddAsset(string guid, string relative, string content)
    {
        var path = Path.Combine(BridgeStorage.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
        File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        AssetDatabase.Register(guid, relative);
    }
    private static string DbHash()
    {
        using (var file = new FileStream(BridgeStorage.DefaultDatabase, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = System.Security.Cryptography.SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(file));
    }
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--lease-probe")
        {
            try { using (BamuMigration.AcquireReceiverLeases(args[1], args[2])) return 0; }
            catch (IOException) { return 42; }
        }
        try
        {
            if (args.Length > 1) throw new ArgumentException("Usage: CoreTests [repository path]. External project ledgers are not accepted.");
            root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
            TestEnvironment.Initialize(root);
            fixtures = TestEnvironment.Fixtures;
            // Every invocation gets a fresh isolated project; do not delete existing project data.
            Application.dataPath = Path.Combine(TestEnvironment.RunRoot, "project", "Assets");
            Directory.CreateDirectory(Application.dataPath);
            foreach (var path in new[] { BridgeStorage.Root, BridgeStorage.DefaultDatabase, BridgeStorage.SharedHome,
                BridgeStorage.LegacySharedHome, BridgeStorage.ReceiverHome, BridgeStorage.TagsPath, BridgeStorage.TagDictionaryPath })
                Check(TestEnvironment.RequireInsideRun(path) == Path.GetFullPath(path), "storage stays inside this test run: " + Path.GetFileName(path));
            var originalDataPath = Application.dataPath;
            Reject(() => Application.dataPath = Path.Combine(root, "validation-project", "Assets"), "real Unity project paths are rejected by test stubs");
            Check(Application.dataPath == originalDataPath, "rejected project path leaves the isolated project active");
            GroupingTests.Run(Check);
            ScannerTests.Run(fixtures, Check);
            FolderEditorTests.Run(Check);
            ViewCacheTests.Run(Check);
            PrefabTests.Run(Check);
            ConverterRegistryTests.Run(Check);
            ProductListOrderTests.Run(Check);
            RenamingTests.Run(Check, root, Path.Combine(fixtures, "reference-project", "ProjectSettings", "bamu.json"));
            SyncTests.Run(Check);
            CatalogTests.Run(Check,root);
            ThumbnailTests.Run(Check, root);
            TagTests.Run(Check, root);
            TagDictionaryTests.Run(Check, root);
            NativeReceiverTests.Run(Check, root);
            var before = DbHash();
            var blm = BlmLibrary.Load(BridgeStorage.DefaultDatabase);
            Check(blm.schema == "20261005" && blm.products.Count == 2, "synthetic BLM read-only adapter");
            var packages = BlmLibrary.FindPackages(blm.products.Single(p => p.key == "booth:123"));
            Check(packages.Count == 1, "synthetic product download directory");
            var entries = UnityPackageReader.Read(packages.First(p => p.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)));
            Check(entries.Count == 2, "synthetic product package indexed without Unity import");
            Check(before == DbHash(), "synthetic BLM database unchanged");
            var fixture = BlmLibrary.Load(Path.Combine(fixtures, "fixture.db"));
            Check(fixture.products.Count == 2 && fixture.products.Single(p => p.key == "booth:123").name == "上書き商品名", "overrides and manual products");
            Check(fixture.root.EndsWith("ライブラリ", StringComparison.Ordinal), "UTF-16LE preference path");
            var overridden = BlmLibrary.Load(Path.Combine(fixtures, "fixture.db"), fixtures);
            Check(overridden.root == fixtures, "library root override");
            Check(UnityPackageReader.Read(Package).Count == 2, "synthetic package index");
            Reject(() => UnityPackageReader.Read(Path.Combine(fixtures, "bad-path.unitypackage")), "TAR parent traversal rejection");
            Reject(() => UnityPackageReader.Read(Path.Combine(fixtures, "bad-header.unitypackage")), "TAR checksum rejection");
            var zip = Path.Combine(fixtures, "nested.zip");
            var extracted = UnityPackageReader.ExtractZipPackage(zip, UnityPackageReader.ZipPackages(zip).Single());
            Check(extracted.StartsWith(Path.Combine(BridgeStorage.Root, "Library"), StringComparison.Ordinal), "ZIP paths cannot escape cache");
            Check(UnityPackageReader.Read(extracted).Count == 2, "ZIP extraction and parse");
            File.WriteAllText(extracted, "tampered");
            Reject(() => UnityPackageReader.ExtractZipPackage(zip, UnityPackageReader.ZipPackages(zip).Single()), "tampered ZIP cache rejection");

            AddAsset(GuidA, "Assets/Generated/ItemA.txt", "A");
            AddAsset(GuidB, "Assets/Generated/ItemB.txt", "B");
            AssetDatabase.Register(new string('f', 32), "Assets/Generated");
            var ledger = BridgeStorage.Load();
            BridgeStorage.EnsureProduct(ledger, ProductReference.From(Product));
            Check(BridgeStorage.AddLink(ledger, Product.key, "Assets/Generated/ItemA.txt", LinkRole.ModifiedCopy, "Manual"), "manual asset link");
            Check(!BridgeStorage.AddLink(ledger, Product.key, "Assets/Generated/ItemA.txt", LinkRole.ModifiedCopy, "Manual"), "duplicate link ignored");
            BridgeStorage.AddLink(ledger, Product.key, "Assets/Generated", LinkRole.Reference, "Manual");
            Check(BridgeStorage.Matches(ledger.links.Last(), "Assets/Generated/ItemB.txt"), "manual folder descendant lookup");
            Check(!BridgeStorage.Matches(ledger.links.Last(), "Assets/GeneratedOther/ItemB.txt"), "folder boundary respected");
            BridgeStorage.AddLink(ledger, "booth:other", "Assets/Generated", LinkRole.Original, "Import");
            Check(!BridgeStorage.Matches(ledger.links.Last(), "Assets/Generated/ItemB.txt"), "auto-linked folder does not claim unselected descendants");
            ledger.links.RemoveAt(ledger.links.Count - 1);
            BridgeStorage.Save(ledger); BridgeStorage.Save(ledger);
            Check(File.Exists(BridgeStorage.LedgerPath + ".bak") && BridgeStorage.Load().links.Count == 2, "atomic persistence and backup");
            AssetDatabase.Register(GuidA, "Assets/Moved/ItemA.txt");
            Check(BridgeStorage.CurrentPath(ledger.links.First()) == "Assets/Moved/ItemA.txt", "GUID lookup after rename/move");
            AssetDatabase.Forget(GuidA);
            Check(BridgeStorage.CurrentPath(ledger.links.First()) == "", "broken link after delete");
            AddAsset(GuidA, "Assets/Generated/ItemA.txt", "A");
            Reject(() => BridgeStorage.AddLink(ledger, Product.key, "Packages/NotAllowed", LinkRole.Original, "Manual"), "Assets-only manual linking");

            _ = ImportCoordinator.HasPending;
            EditorApplication.Tick(); // Initial Editor delayCall runs before normal user interaction.
            Begin(); var pending = ImportCoordinator.Pending;
            Check(pending.sawStart && pending.entries.All(e => e.existedBefore), "durable before-import state");
            Reject(Begin, "concurrent import rejected");
            AssetDatabase.Complete("two-assets", "Assets/Generated/ItemA.txt");
            EditorApplication.Tick();
            ledger = BridgeStorage.Load();
            var record = ledger.imports.Last();
            Check(record.state == "PartialSuccess" && record.assets.Count == 1, "partial import commits selected files only");
            Check(!ledger.links.Any(l => l.method == "Import" && l.guid == GuidB), "preexisting unselected file not auto-linked");
            Check(record.assets.First().contentHash.Length == 64 && record.assets.First().metaHash.Length == 64, "post-import content and meta hashes");
            ImportCoordinator.Commit(WithEvidence(pending, "Assets/Generated/ItemA.txt"));
            Check(BridgeStorage.Load().imports.Count == 1, "replayed commit is idempotent");
            Check(!ImportCoordinator.HasPending, "completed pending record cleared");

            Begin(); AssetDatabase.Cancel("two-assets");
            Check(BridgeStorage.Load().imports.Last().state == "Cancelled", "cancel callback records no success");
            Begin(); AssetDatabase.Fail("two-assets");
            Check(BridgeStorage.Load().imports.Last().state == "Failed", "failed callback records no success");
            Check(BridgeStorage.Load().imports.TakeLast(2).All(r => r.assets.Count == 0), "cancel/failure contains no imported links");
            Begin(); AssetDatabase.CompleteWithoutItems("two-assets"); EditorApplication.Tick();
            Check(ImportCoordinator.Pending.phase == "NeedsReview", "completion without selected-items evidence requires review");
            ImportCoordinator.FinishWithoutLinks("NeedsReview", "test");

            Begin(); AssetDatabase.Start("foreign-package");
            AssetDatabase.Complete("foreign-package", "Assets/Generated/ItemB.txt"); EditorApplication.Tick();
            Check(ImportCoordinator.Pending.phase == "NeedsReview", "unrelated import cannot claim pending transaction");
            ImportCoordinator.FinishWithoutLinks("NeedsReview", "test");
            Begin(); pending = ImportCoordinator.Pending; pending.processId = -1;
            BridgeStorage.Write(BridgeStorage.PendingPath, pending);
            typeof(ImportCoordinator).GetMethod("Recover", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Check(ImportCoordinator.Pending.phase == "NeedsReview", "Editor restart without completion evidence requires review");
            ImportCoordinator.FinishWithoutLinks("NeedsReview", "test");

            Begin(); pending = WithEvidence(ImportCoordinator.Pending, "Assets/Generated/ItemA.txt", "Assets/Generated/ItemB.txt");
            BridgeStorage.Write(BridgeStorage.PendingPath, pending);
            typeof(ImportCoordinator).GetMethod("Recover", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            EditorApplication.Tick();
            Check(BridgeStorage.Load().imports.Last().state == "Success", "durable completion evidence recovers transaction");
            var dotted = Path.Combine(fixtures, "avatar.v1.2.unitypackage");
            File.Copy(Package, dotted, true);
            ImportCoordinator.Begin(Product, dotted, "1.2", false);
            Check(ImportCoordinator.Pending.sawStart && ImportCoordinator.Pending.phase == "Importing", "dotted version in package name");
            AssetDatabase.Complete("avatar.v1.2", "Assets/Generated/ItemA.txt", "Assets/Generated/ItemB.txt");
            EditorApplication.Tick();
            Check(!ImportCoordinator.HasPending && BridgeStorage.Load().imports.Last().state == "Success", "dotted-name completion commits");
            Begin();
            AssetDatabase.Register(GuidA, "Assets/Elsewhere/Conflicting.txt");
            AssetDatabase.Complete("two-assets", "Assets/Generated/ItemA.txt");
            EditorApplication.Tick();
            Check(BridgeStorage.Load().imports.Last().state == "NeedsReview" && BridgeStorage.Load().imports.Last().assets.Count == 0,
                "GUID conflict cannot link an unrelated resolved path");
            AssetDatabase.Register(GuidA, "Assets/Generated/ItemA.txt");
            ledger = BridgeStorage.Load();
            ledger.links.Clear(); BridgeStorage.Save(ledger);
            Check(File.Exists(Path.Combine(Application.dataPath, "Generated", "ItemA.txt")), "unlinking does not delete files");
            ledger.schemaVersion = 999; BridgeStorage.Save(ledger);
            Reject(() => BridgeStorage.Load(), "unknown ledger version not overwritten");
            File.WriteAllText(BridgeStorage.LedgerPath, "{broken");
            Reject(() => BridgeStorage.Load(), "corrupt ledger not overwritten");
            Check(before == DbHash(), "synthetic BLM unchanged after all checks");
            File.WriteAllText(Path.Combine(TestEnvironment.RunRoot, "core-test-result.json"),
                "{\"status\":\"passed\",\"checks\":" + checks + ",\"nativeUnityTested\":false}");
            Console.WriteLine("CORE TESTS PASSED: " + checks + " (Unity APIs simulated; separate real-DLL compilation)");
            Console.WriteLine("Isolated test output: " + TestEnvironment.RunRoot);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static PendingImport WithEvidence(PendingImport pending, params string[] paths)
    {
        pending.receivedItems = true; pending.selectedPaths = paths.ToList(); return pending;
    }
}
