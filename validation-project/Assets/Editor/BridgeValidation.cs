using System;
using System.IO;
using System.Linq;
using System.Reflection;
using bamu;
using UnityEditor;
using UnityEngine;

// A dependency-free Editor integration runner; invoked only in this isolated validation project.
[InitializeOnLoad]
public static class BridgeValidation
{
    private static string FixtureRoot => Path.GetFullPath(Path.Combine(BridgeStorage.Root, "..", "artifacts", "fixtures"));
    private const string StageKey = "BoothBridgeValidation.Stage";
    private static double deadline;
    private static int assertions;
    private static Product Product => new Product { key = "booth:123", boothId = "123", name = "検証商品" };
    static BridgeValidation()
    {
        if (SessionState.GetInt(StageKey, 0) != 0)
        {
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update += Tick;
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        assertions++;
        Debug.Log("BRIDGE CHECK: " + message);
    }
    private static void ExpectFailure(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { Check(true, message); return; }
        throw new Exception("FAIL: expected rejection: " + message);
    }
    private static void RemoveOwnedFile(string path) { if (File.Exists(path)) File.Delete(path); }

    public static void Run()
    {
        try
        {
            Check(BridgeStorage.Root.EndsWith("validation-project", StringComparison.Ordinal), "isolated project guard");
            RemoveOwnedFile(BridgeStorage.LedgerPath);
            RemoveOwnedFile(BridgeStorage.PendingPath);
            var fixture = BlmLibrary.Load(Path.Combine(FixtureRoot, "fixture.db"));
            Check(fixture.products.Count == 2, "BLM booth and manual products");
            Check(fixture.products.Single(p => p.boothId == "123").name == "上書き商品名", "BLM overridden product names");
            Check(fixture.root.EndsWith("ライブラリ", StringComparison.Ordinal), "UTF-16LE library path");
            Check(fixture.products.Single(p => p.key == "blm:u1").name == "手動商品", "manual product identity");
            var live = BlmLibrary.Load(BridgeStorage.DefaultDatabase);
            Check(live.products.Count > 0, "real BLM 0.5.0 read-only connection");
            var realPackages = BlmLibrary.FindPackages(live.products.First());
            Check(realPackages.Count > 0, "real BLM product directory resolution");
            var realPackage = realPackages.First(p => p.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase));
            Check(UnityPackageReader.Read(realPackage).Count > 0, "real downloaded package index (no import)");
            Check(UnityPackageReader.Read(Path.Combine(FixtureRoot, "two-assets.unitypackage")).Count == 2, "package GUID/path index");
            ExpectFailure(() => UnityPackageReader.Read(Path.Combine(FixtureRoot, "bad-path.unitypackage")), "reject parent traversal");
            ExpectFailure(() => UnityPackageReader.Read(Path.Combine(FixtureRoot, "bad-header.unitypackage")), "reject invalid TAR");
            var zip = Path.Combine(FixtureRoot, "nested.zip");
            var extracted = UnityPackageReader.ExtractZipPackage(zip, UnityPackageReader.ZipPackages(zip).Single());
            Check(extracted.StartsWith(Path.Combine(BridgeStorage.Root, "Library"), StringComparison.Ordinal), "ZIP cannot escape cache");
            Check(UnityPackageReader.Read(extracted).Count == 2, "nested ZIP package");

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Generated"));
            File.WriteAllText(Path.Combine(Application.dataPath, "Generated", "Manual.txt"), "manual");
            AssetDatabase.Refresh();
            var ledger = BridgeStorage.Load();
            BridgeStorage.EnsureProduct(ledger, ProductReference.From(Product));
            Check(BridgeStorage.AddLink(ledger, Product.key, "Assets/Generated/Manual.txt", LinkRole.ModifiedCopy, "Manual"), "manual file link");
            Check(!BridgeStorage.AddLink(ledger, Product.key, "Assets/Generated/Manual.txt", LinkRole.ModifiedCopy, "Manual"), "manual link idempotency");
            BridgeStorage.AddLink(ledger, Product.key, "Assets/Generated", LinkRole.Reference, "Manual");
            BridgeStorage.Save(ledger);
            ledger = BridgeStorage.Load();
            Check(ledger.links.Count == 2, "ledger persists across load");
            Check(BridgeStorage.Matches(ledger.links.Single(l => l.folder), "Assets/Generated/Manual.txt"), "folder reverse lookup");
            Check(!BridgeStorage.ContainsPath("Assets/Generated", "Assets/GeneratedOther/x"), "folder boundary");
            Check(AssetDatabase.MoveAsset("Assets/Generated/Manual.txt", "Assets/Generated/Moved.txt") == "", "move linked asset");
            Check(BridgeStorage.CurrentPath(ledger.links.First()) == "Assets/Generated/Moved.txt", "GUID survives move");
            ledger.links.RemoveAt(0); BridgeStorage.Save(ledger);
            Check(File.Exists(Path.Combine(Application.dataPath, "Generated", "Moved.txt")), "unlink preserves asset");

            // Existing B is deliberately excluded from the completion event below.
            File.WriteAllText(Path.Combine(Application.dataPath, "Generated", "ItemB.txt"), "preexisting B");
            File.WriteAllText(Path.Combine(Application.dataPath, "Generated", "ItemB.txt.meta"), "fileFormatVersion: 2\nguid: " + new string('b', 32) + "\n");
            File.WriteAllText(Path.Combine(Application.dataPath, "Generated", "ItemA.txt"), "new A");
            File.WriteAllText(Path.Combine(Application.dataPath, "Generated", "ItemA.txt.meta"), "fileFormatVersion: 2\nguid: " + new string('a', 32) + "\n");
            AssetDatabase.Refresh();
            var pending = MakePending("partial");
            pending.receivedItems = true;
            pending.selectedPaths.Add("Assets/Generated/ItemA.txt");
            ImportCoordinator.Commit(pending);
            ledger = BridgeStorage.Load();
            Check(ledger.imports.Single(r => r.id == "partial").state == "PartialSuccess", "partial selection recorded");
            Check(ledger.links.Any(l => l.guid == new string('a', 32)) && !ledger.links.Any(l => l.guid == new string('b', 32)), "preexisting unselected GUID is not auto-linked");
            ImportCoordinator.Commit(pending);
            Check(BridgeStorage.Load().imports.Count(r => r.id == "partial") == 1, "commit crash recovery is idempotent");
            SimulateTerminal("cancel", "Cancelled", "importPackageCancelled", "two-assets");
            SimulateTerminal("failed", "Failed", "importPackageFailed", "two-assets", "fixture failure");
            Check(BridgeStorage.Load().imports.Where(r => r.id == "cancel" || r.id == "failed").All(r => r.assets.Count == 0), "cancel/failure never links");

            // Now exercise actual native Unity import callbacks, not just synthetic completion records.
            SessionState.SetInt(StageKey, 1);
            SessionState.SetInt("BoothBridgeValidation.Assertions", assertions);
            deadline = EditorApplication.timeSinceStartup + 90;
            EditorApplication.update -= Tick; EditorApplication.update += Tick;
            ImportCoordinator.Begin(Product, Path.Combine(FixtureRoot, "two-assets.unitypackage"), "fixture-1", false);
        }
        catch (Exception ex) { Fail(ex); }
    }
    private static PendingImport MakePending(string id)
    {
        return new PendingImport
        {
            id = id, packageName = "two-assets", product = ProductReference.From(Product), startedUtc = DateTime.UtcNow.ToString("o"),
            processId = System.Diagnostics.Process.GetCurrentProcess().Id, sawStart = true, phase = "Importing",
            revision = new ArtifactRevision { id = "fixture:hash", productKey = Product.key, fileName = "two-assets.unitypackage", sha256 = new string('d', 64) },
            entries = UnityPackageReader.Read(Path.Combine(FixtureRoot, "two-assets.unitypackage"))
        };
    }
    private static void SimulateTerminal(string id, string expected, string callbackField, params object[] args)
    {
        var pending = MakePending(id);
        BridgeStorage.Write(BridgeStorage.PendingPath, pending);
        // Invoke our registered callback directly to verify cancellation/failure handling without UI automation.
        var method = typeof(ImportCoordinator).GetMethod(callbackField == "importPackageCancelled" ? "Cancelled" : "Failed", BindingFlags.NonPublic | BindingFlags.Static);
        method.Invoke(null, args);
        Check(!ImportCoordinator.HasPending && BridgeStorage.Load().imports.Single(r => r.id == id).state == expected, expected + " callback");
    }
    private static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Native import callback timeout");
            if (ImportCoordinator.HasPending || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var stage = SessionState.GetInt(StageKey, 0);
            if (stage == 1)
            {
                var record = BridgeStorage.Load().imports.Last();
                Check(record.state == "Success" && record.assets.Count == 2, "native Unity import selected-item callbacks");
                SessionState.SetInt(StageKey, 2);
                ImportCoordinator.Begin(Product, Path.Combine(FixtureRoot, "script-asset.unitypackage"), "fixture-script", false);
            }
            else if (stage == 2)
            {
                var ledger = BridgeStorage.Load();
                Check(ledger.imports.Last().state == "Success", "script import survives recompilation");
                Check(ledger.links.Any(l => l.guid == new string('c', 32)), "script asset linked");
                AssetDatabase.DeleteAsset("Assets/Generated/ItemA.txt");
                Check(string.IsNullOrEmpty(BridgeStorage.CurrentPath(ledger.links.Single(l => l.guid == new string('a', 32)))), "deleted asset is a broken link");
                var window = EditorWindow.GetWindow<BridgeWindow>();
                Check(window != null, "Editor window opens with live catalog");
                window.Close();
                SessionState.SetInt(StageKey, 0);
                Debug.Log("BRIDGE VALIDATION PASSED");
                File.WriteAllText(Path.Combine(BridgeStorage.Root, "..", "artifacts", "validation-result.json"), "{\"status\":\"passed\",\"unity\":\"" + Application.unityVersion + "\"}");
                EditorApplication.Exit(0);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }
    private static void Fail(Exception ex)
    {
        SessionState.SetInt(StageKey, 0);
        Debug.LogException(ex);
        Debug.LogError("BRIDGE VALIDATION FAILED");
        EditorApplication.Exit(1);
    }
}
