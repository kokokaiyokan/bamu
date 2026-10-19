using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;

namespace bamu
{
    [InitializeOnLoad]
    public static class ImportCoordinator
    {
        public static event Action Changed;
        private static double finalizeAfter;
        private static bool finalizeScheduled;
        public static PendingImport Pending
        {
            get
            {
                var value = BridgeStorage.Read<PendingImport>(BridgeStorage.PendingPath);
                if (value != null && (value.schemaVersion != 1 || value.product == null || value.revision == null ||
                    value.entries == null || value.selectedPaths == null || string.IsNullOrEmpty(value.id)))
                    throw new InvalidDataException("未対応または壊れたインポート処理記録です。元のJSONは保持しました。");
                return value;
            }
        }
        public static bool HasPending => File.Exists(BridgeStorage.PendingPath);

        static ImportCoordinator()
        {
            AssetDatabase.importPackageStarted += Started;
            AssetDatabase.onImportPackageItemsCompleted += ItemsCompleted;
            AssetDatabase.importPackageCompleted += Completed;
            AssetDatabase.importPackageCancelled += Cancelled;
            AssetDatabase.importPackageFailed += Failed;
            EditorApplication.update += Update;
            EditorApplication.delayCall += Recover;
        }
        private static void Save(PendingImport pending) { BridgeStorage.Write(BridgeStorage.PendingPath, pending); Changed?.Invoke(); }
        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { UnityEngine.Debug.LogError("BOOTH Asset Management Utility: " + ex.Message); }
        }

        public static void Begin(Product product, string packagePath, string versionLabel = "", bool interactive = true)
        {
            if (HasPending) throw new InvalidOperationException("前のインポート記録を先に確認してください。");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("コンパイル・更新・再生が終わってから実行してください。");
            BridgeStorage.Load(); // Fail before import if the existing ledger cannot be read.
            try
            {
                var length = new FileInfo(packagePath).Length;
                var nextUpdate = EditorApplication.timeSinceStartup;
                var entries = UnityPackageReader.Read(packagePath, position =>
                {
                    if (EditorApplication.timeSinceStartup < nextUpdate) return;
                    nextUpdate = EditorApplication.timeSinceStartup + .15;
                    if (EditorUtility.DisplayCancelableProgressBar("BOOTH Asset Management Utility", "パッケージの構成を読み取り中…", (float)position / Math.Max(1, length)))
                        throw new OperationCanceledException("読み取りをキャンセルしました。");
                });
                foreach (var entry in entries) entry.existedBefore = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(entry.guid));
                var now = DateTime.UtcNow.ToString("o");
                var hash = UnityPackageReader.Hash(packagePath);
                var pending = new PendingImport
                {
                    id = Guid.NewGuid().ToString("N"), product = ProductReference.From(product),
                    startedUtc = now, packageName = Path.GetFileNameWithoutExtension(packagePath),
                    processId = Process.GetCurrentProcess().Id, entries = entries,
                    revision = new ArtifactRevision
                    {
                        id = product.key + ":" + hash, productKey = product.key,
                        fileName = Path.GetFileName(packagePath), sha256 = hash, size = length,
                        label = versionLabel, recordedUtc = now
                    }
                };
                Save(pending);
                try { AssetDatabase.ImportPackage(packagePath, interactive); }
                catch (Exception ex) { FinishWithoutLinks("Failed", ex.Message); throw; }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private static bool IsCurrent(PendingImport pending, string name)
        {
            // Unity normally reports the name without .unitypackage. Do not strip
            // arbitrary dotted suffixes such as the ".2" in "Outfit.v1.2".
            var fileName = Path.GetFileName(name ?? "");
            if (fileName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - ".unitypackage".Length);
            return pending != null && pending.phase != "NeedsReview" && pending.processId == Process.GetCurrentProcess().Id &&
                string.Equals(fileName, pending.packageName, StringComparison.OrdinalIgnoreCase);
        }

        private static void Started(string name) => Guard(() =>
        {
            var pending = Pending;
            if (pending == null) return;
            if (!IsCurrent(pending, name))
            {
                pending.phase = "NeedsReview"; pending.message = "別のパッケージの処理を検出しました。自動リンクを停止しました。"; Save(pending); return;
            }
            pending.sawStart = true; pending.phase = "Importing"; Save(pending);
        });
        private static void ItemsCompleted(string[] paths) => Guard(() =>
        {
            var pending = Pending;
            if (pending == null || pending.phase == "NeedsReview" || !pending.sawStart || pending.processId != Process.GetCurrentProcess().Id) return;
            pending.receivedItems = true;
            pending.selectedPaths = (paths ?? Array.Empty<string>()).Select(p => p.Replace('\\', '/')).Distinct().ToList();
            Save(pending); Schedule();
        });
        private static void Completed(string name) => Guard(() =>
        {
            var pending = Pending;
            if (!IsCurrent(pending, name)) return;
            pending.completed = true; Save(pending); Schedule();
        });
        private static void Cancelled(string name) => Guard(() => { if (IsCurrent(Pending, name)) FinishWithoutLinks("Cancelled", "インポートをキャンセルしました。"); });
        private static void Failed(string name, string error) => Guard(() => { if (IsCurrent(Pending, name)) FinishWithoutLinks("Failed", error); });
        private static void Schedule() { finalizeScheduled = true; finalizeAfter = EditorApplication.timeSinceStartup + 1; }
        private static void Recover() => Guard(() =>
        {
            var pending = Pending;
            if (pending == null) return;
            if (pending.schemaVersion != 1) throw new InvalidDataException("未対応の処理記録です。");
            if (pending.receivedItems) Schedule();
            else if (pending.processId != Process.GetCurrentProcess().Id)
            {
                pending.phase = "NeedsReview"; pending.message = "前回の処理の完了を確認できません。インポート履歴を確認してください。"; Save(pending);
            }
        });
        private static void Update()
        {
            if (!finalizeScheduled || EditorApplication.timeSinceStartup < finalizeAfter || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            finalizeScheduled = false;
            Guard(() =>
            {
                var pending = Pending;
                if (pending == null || pending.phase == "NeedsReview") return;
                if (!pending.receivedItems)
                {
                    pending.phase = "NeedsReview"; pending.message = "選択項目の完了通知を取得できませんでした。自動リンクは行っていません。"; Save(pending); return;
                }
                Commit(pending);
            });
        }

        // Commit only the items explicitly reported by Unity, never every GUID found afterwards.
        public static void Commit(PendingImport pending)
        {
            if (!pending.receivedItems || pending.phase == "NeedsReview") throw new InvalidOperationException("取り込み項目の完了通知が必要です。");
            var ledger = BridgeStorage.Load();
            if (ledger.imports.Any(r => r.id == pending.id)) { ClearPending(); return; } // Crash after ledger save: idempotent.
            var selected = new HashSet<string>(pending.selectedPaths, StringComparer.Ordinal);
            var imported = new List<ImportedAsset>();
            var unresolved = 0;
            foreach (var entry in pending.entries.Where(e => selected.Contains(e.path) || selected.Contains(AssetDatabase.GUIDToAssetPath(e.guid))))
            {
                var current = AssetDatabase.GUIDToAssetPath(entry.guid);
                // A GUID can conflict with a preexisting asset. Its resolved location
                // must itself be in Unity's completed items, not just the source path.
                if (!UnityPackageReader.ValidAssetPath(current) || !selected.Contains(current)) { unresolved++; continue; }
                var fullPath = Path.Combine(BridgeStorage.Root, current);
                imported.Add(new ImportedAsset
                {
                    guid = entry.guid, path = current, existedBefore = entry.existedBefore,
                    contentHash = UnityPackageReader.HashIfFile(fullPath), metaHash = UnityPackageReader.HashIfFile(fullPath + ".meta")
                });
                BridgeStorage.AddLink(ledger, pending.product.key, current, LinkRole.Original, "Import", pending.revision.id);
            }
            BridgeStorage.EnsureProduct(ledger, pending.product);
            if (!ledger.revisions.Any(r => r.id == pending.revision.id)) ledger.revisions.Add(pending.revision);
            ledger.imports.Add(new ImportRecord
            {
                id = pending.id, productKey = pending.product.key, revisionId = pending.revision.id,
                startedUtc = pending.startedUtc, endedUtc = DateTime.UtcNow.ToString("o"),
                state = imported.Count == 0 || unresolved > 0 ? "NeedsReview" : imported.Count < pending.entries.Count ? "PartialSuccess" : "Success",
                packageCount = pending.entries.Count, assets = imported,
                message = unresolved > 0 ? "完了通知のある一部のGUIDを解決できませんでした。" : imported.Count == 0 ? "確認できた取り込み項目がありません。" : ""
            });
            BridgeStorage.Save(ledger);
            ClearPending();
        }

        public static void FinishWithoutLinks(string state, string message)
        {
            if (state != "Cancelled" && state != "Failed" && state != "NeedsReview") throw new ArgumentException("Invalid terminal state");
            var pending = Pending;
            if (pending == null) return;
            var ledger = BridgeStorage.Load();
            if (!ledger.imports.Any(r => r.id == pending.id))
            {
                BridgeStorage.EnsureProduct(ledger, pending.product);
                if (!ledger.revisions.Any(r => r.id == pending.revision.id)) ledger.revisions.Add(pending.revision);
                ledger.imports.Add(new ImportRecord { id = pending.id, productKey = pending.product.key, revisionId = pending.revision.id,
                    startedUtc = pending.startedUtc, endedUtc = DateTime.UtcNow.ToString("o"), state = state,
                    packageCount = pending.entries.Count, message = message });
                BridgeStorage.Save(ledger);
            }
            ClearPending();
        }
        private static void ClearPending()
        {
            // Keep the backup as a local recovery record, but not as an active transaction.
            if (File.Exists(BridgeStorage.PendingPath)) File.Delete(BridgeStorage.PendingPath);
            finalizeScheduled = false; Changed?.Invoke();
        }
    }
}
