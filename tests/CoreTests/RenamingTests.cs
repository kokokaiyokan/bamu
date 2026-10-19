using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using bamu;
using UnityEditor;

internal static class RenamingTests
{
    internal static int Probe(string current, string previous)
    {
        using (var process = new Process { StartInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true } })
        {
            process.StartInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            process.StartInfo.ArgumentList.Add("--lease-probe");
            process.StartInfo.ArgumentList.Add(current); process.StartInfo.ArgumentList.Add(previous);
            process.Start();
            if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("lease child timed out"); }
            return process.ExitCode;
        }
    }

    internal static void Run(Action<bool, string> check, string root, string fixtureLedger)
    {
        var folder = Path.Combine(TestEnvironment.RunRoot, "rename-test-" + Guid.NewGuid().ToString("N"));
        var old = Path.Combine(folder, "previous"); var current = Path.Combine(folder, "bamu"); Directory.CreateDirectory(old);
        var source = Path.Combine(old, "state.json"); var target = Path.Combine(current, "state.json");
        File.WriteAllText(source, "{\"preserved\":true}");
        BamuMigration.AdoptFile(source, target);
        check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(target)), "rename copies old data byte-for-byte and retains original");
        File.WriteAllText(target, "new data wins"); BamuMigration.AdoptFile(source, target);
        check(File.ReadAllText(target) == "new data wins", "rename never overwrites existing new data");
        File.Delete(target); BamuMigration.AdoptFile(source, target);
        check(File.ReadAllText(target).Contains("preserved") && !Directory.GetFiles(current, "*.tmp").Any(), "rename retry completes atomically without temporary-file residue");
        var pending = Path.Combine(current, "pending.json");
        BamuMigration.AdoptOnceFile(source, pending); File.Delete(pending); BamuMigration.AdoptOnceFile(source, pending);
        check(!File.Exists(pending) && File.Exists(source), "completed import does not resurrect legacy pending transaction");

        Directory.CreateDirectory(Path.Combine(old, "blm-objects", "item"));
        Directory.CreateDirectory(Path.Combine(old, "blm-receipts"));
        File.WriteAllText(Path.Combine(old, "blm-objects", "item", "asset"), "archive bytes");
        File.WriteAllText(Path.Combine(old, "blm-receipts", "receipt.json"), "{}");
        BamuMigration.ReceiverData(old, current, CancellationToken.None);
        check(File.ReadAllText(Path.Combine(current, "blm-objects", "item", "asset")) == "archive bytes" &&
            File.Exists(Path.Combine(current, "blm-receipts", "receipt.json")) && File.ReadAllText(target).Contains("preserved"), "receiver migration preserves objects receipts and state");
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel(); var cancelled = false;
            try { BamuMigration.ReceiverData(old, Path.Combine(folder, "cancelled"), stopped.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !File.Exists(Path.Combine(folder, "cancelled", "state.json")), "cancelled receiver migration does not install state before copying its data");
        }
        var oldConnection = Path.Combine(old, "connection.json"); var newConnection = Path.Combine(current, "connection.json");
        File.WriteAllText(oldConnection, "{\"port\":48763,\"token\":\"synthetic-test-key\"}");
        BamuMigration.AdoptFile(oldConnection, newConnection);
        check(File.ReadAllBytes(oldConnection).SequenceEqual(File.ReadAllBytes(newConnection)), "rename retains connection key without generating another");
        var oldImages = Path.Combine(old, "thumbnails"); var newImages = Path.Combine(current, "thumbnails");
        var oldCache = new ThumbnailDiskCache(oldImages); var bytes = new byte[] { 1, 2, 3, 4 };
        oldCache.Store("https://booth.pximg.net/rename-test.png", bytes);
        var cache = new ThumbnailDiskCache(newImages, oldImages);
        check(cache.Read("https://booth.pximg.net/rename-test.png").SequenceEqual(bytes) && Directory.GetFiles(newImages).Length == 1,
            "thumbnail read-through migration reuses bytes without network");

        EditorPrefs.SetString("BoothUnityBridge.RenameTest.Path", "old path");
        EditorPrefs.SetInt("BoothUnityBridge.RenameTest.Sort", 3);
        EditorPrefs.SetBool("BoothUnityBridge.RenameTest.Images", false);
        check(BamuPreferences.GetString("bamu.RenameTest.Path", "") == "old path" && BamuPreferences.GetInt("bamu.RenameTest.Sort", 0) == 3 &&
            !BamuPreferences.GetBool("bamu.RenameTest.Images", true), "string integer and boolean preferences migrate from old prefix");
        EditorPrefs.SetString("bamu.RenameTest.Path", "new path");
        check(BamuPreferences.GetString("bamu.RenameTest.Path", "") == "new path", "new preference takes precedence over old preference");

        var oldDataPath = UnityEngine.Application.dataPath;
        try
        {
            UnityEngine.Application.dataPath = Path.Combine(folder, "project", "Assets"); Directory.CreateDirectory(UnityEngine.Application.dataPath);
            var oldLedger = Path.Combine(BridgeStorage.Root, "ProjectSettings", "BoothUnityBridge.json");
            var ledger = new ProjectLedger(); ledger.products.Add(new ProductReference { key = "test" });
            ledger.links.Add(new AssetLink { id = "link", productKey = "test", guid = "guid" });
            ledger.prefabSelections.Add(new ProductPrefabSelection { productKey = "test", guids = new System.Collections.Generic.List<string> { "guid" } });
            ledger.displayFolders.Add(new DisplayFolderRule { productKey = "test", folderGuid = "folder" });
            BridgeStorage.Write(oldLedger, ledger);
            var loaded = BridgeStorage.Load();
            check(Path.GetFileName(BridgeStorage.LedgerPath) == "bamu.json" && loaded.links.Single().id == "link" &&
                loaded.prefabSelections.Single().guids.Single() == "guid" && loaded.displayFolders.Count == 1, "project ledger migration keeps asset links prefab choices and display rules");
            if (File.Exists(fixtureLedger))
            {
                File.Delete(BridgeStorage.LedgerPath); File.Copy(fixtureLedger, oldLedger, true);
                var synthetic = BridgeStorage.Load();
                check(File.ReadAllBytes(oldLedger).SequenceEqual(File.ReadAllBytes(BridgeStorage.LedgerPath)) && synthetic.links.Count > 0,
                    "synthetic project ledger copy migrates byte-for-byte in isolated project");
            }
        }
        finally { UnityEngine.Application.dataPath = oldDataPath; }

        var leaseOld = Path.Combine(folder, "leases-old"); var leaseNew = Path.Combine(folder, "leases-new");
        using (BamuMigration.AcquireReceiverLeases(leaseNew, leaseOld))
            check(Probe(leaseNew, leaseOld) == 42, "second process cannot acquire receiver during new/legacy lease ownership");
        check(Probe(leaseNew, leaseOld) == 0, "receiver leases transfer to another process after disposal");
        using (var legacy = new FileStream(Path.Combine(leaseOld, "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            legacy.Lock(0, 1);
            check(Probe(leaseNew, leaseOld) == 42, "running old receiver blocks new receiver migration");
        }
        using (var native = new FileStream(Path.Combine(leaseNew, "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            native.Lock(0, 1);
            check(Probe(leaseNew, leaseOld) == 42, "failed second lease does not bypass active new receiver");
        }
        check(Probe(leaseNew, leaseOld) == 0, "failed lease attempt releases partial legacy ownership");
    }
}
