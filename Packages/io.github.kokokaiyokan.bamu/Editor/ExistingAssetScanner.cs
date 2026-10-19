using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace bamu
{
    public enum ScanMatchKind { Guid, Content, Path }

    public sealed class ScanEvidence
    {
        public string packageName, packageHash, sourceGuid, sourcePath, sourceContentHash;
        public bool contentEqual;
    }

    public sealed class ScanCandidate
    {
        public Product product;
        public string guid, path, projectContentHash;
        public ScanMatchKind kind;
        public bool selected, alreadyLinked;
        public List<ScanEvidence> evidence = new List<ScanEvidence>();
        public List<string> sharedProducts = new List<string>();
        public string ContentLabel => evidence.All(e => e.contentEqual) ? "内容一致" :
            evidence.Any(e => e.contentEqual) ? "配布版によって内容が異なる" : "内容相違";
    }

    public sealed class ScanProductResult
    {
        public Product product;
        public int packageCount;
        public HashSet<string> sourceGuids = new HashSet<string>();
        public HashSet<string> foundGuids = new HashSet<string>();
        public int Missing => sourceGuids.Except(foundGuids).Count();
    }

    public sealed class ExistingAssetScanReport
    {
        public List<ScanCandidate> candidates = new List<ScanCandidate>();
        public List<ScanProductResult> products = new List<ScanProductResult>();
        public List<string> errors = new List<string>();
        public bool cancelled;
        public int cacheHits;
    }

    [Serializable]
    public sealed class PackageScanIndex
    {
        public int schemaVersion = 1;
        public string packageHash;
        public List<PackageAsset> entries;
    }

    public static class ExistingAssetScanner
    {
        private sealed class PackageWork
        {
            public Product product;
            public string path, displayName;
        }

        // Synchronous Editor operation with cancellable progress; only caches are
        // written by scanning. Registration is an explicit, separate operation.
        public static ExistingAssetScanReport Scan(IEnumerable<Product> products, ProjectLedger ledger,
            bool findCopies = false, Action<string, float> progress = null, string cacheDirectory = null)
        {
            var report = new ExistingAssetScanReport();
            var work = new List<PackageWork>();
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var localHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, ScanCandidate>(StringComparer.Ordinal);
            Dictionary<string, List<string>> contentIndex = null;
            cacheDirectory = cacheDirectory ?? BridgeStorage.CacheDirectory("Indexes");
            var results = new Dictionary<string, ScanProductResult>();
            try
            {
                foreach (var product in products.GroupBy(p => p.key).Select(g => g.First()))
                {
                    progress?.Invoke(product.name + "：保存ファイルを確認中", 0);
                    var result = new ScanProductResult { product = product };
                    results[product.key] = result; report.products.Add(result);
                    try
                    {
                        foreach (var path in BlmLibrary.FindPackages(product))
                        {
                            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var entry in UnityPackageReader.ZipPackages(path))
                                    work.Add(new PackageWork { product = product, path = path, displayName = entry });
                            }
                            else work.Add(new PackageWork { product = product, path = path, displayName = "" });
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                    { report.errors.Add(product.name + ": " + ex.Message); }
                }
                for (var i = 0; i < work.Count; i++)
                {
                    var item = work[i];
                    var fraction = (float)i / Math.Max(1, work.Count);
                    Action<long> tick = _ => progress?.Invoke(item.product.name + "：" + Path.GetFileName(item.path), fraction);
                    tick(0);
                    try
                    {
                        var package = item.displayName.Length == 0 ? item.path : UnityPackageReader.ExtractZipPackage(item.path, item.displayName, tick);
                        var index = ReadIndex(package, cacheDirectory, tick, report);
                        var result = results[item.product.key];
                        result.packageCount++;
                        foreach (var entry in index.entries.Where(e => !e.folder))
                        {
                            result.sourceGuids.Add(entry.guid);
                            if (!owners.TryGetValue(entry.guid, out var set)) owners[entry.guid] = set = new HashSet<string>();
                            set.Add(item.product.key);
                            var current = AssetDatabase.GUIDToAssetPath(entry.guid);
                            if (IsProjectFile(current))
                            {
                                AddCandidate(item.product, entry, current, ScanMatchKind.Guid, index.packageHash,
                                    DisplayName(item), localHashes, candidates);
                                result.foundGuids.Add(entry.guid);
                                continue;
                            }
                            var atSource = AssetDatabase.AssetPathToGUID(entry.path);
                            if (!string.IsNullOrEmpty(atSource) && IsProjectFile(entry.path))
                            {
                                var kind = LocalHash(entry.path, localHashes) == entry.contentHash ? ScanMatchKind.Content : ScanMatchKind.Path;
                                AddCandidate(item.product, entry, entry.path, kind, index.packageHash, DisplayName(item), localHashes, candidates);
                                result.foundGuids.Add(entry.guid);
                            }
                            if (!findCopies) continue;
                            if (contentIndex == null)
                            {
                                contentIndex = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                                var projectGuids = AssetDatabase.FindAssets("", new[] { "Assets" });
                                foreach (var guid in projectGuids)
                                {
                                    progress?.Invoke("GUIDが変わったコピーを探すため、プロジェクト内の内容を比較中", fraction);
                                    var path = AssetDatabase.GUIDToAssetPath(guid);
                                    if (!IsProjectFile(path)) continue;
                                    var hash = LocalHash(path, localHashes);
                                    if (!contentIndex.TryGetValue(hash, out var list)) contentIndex[hash] = list = new List<string>();
                                    list.Add(path);
                                }
                            }
                            if (contentIndex.TryGetValue(entry.contentHash, out var copies))
                                foreach (var path in copies)
                                {
                                    AddCandidate(item.product, entry, path, ScanMatchKind.Content, index.packageHash,
                                        DisplayName(item), localHashes, candidates);
                                    result.foundGuids.Add(entry.guid);
                                }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                    { report.errors.Add(item.product.name + " / " + DisplayName(item) + ": " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { report.cancelled = true; }

            foreach (var candidate in candidates.Values)
            {
                candidate.alreadyLinked = ledger.links.Any(l => l.productKey == candidate.product.key && l.guid == candidate.guid);
                var shared = new HashSet<string>(ledger.links.Where(l => l.guid == candidate.guid).Select(l => l.productKey));
                foreach (var evidence in candidate.evidence)
                    if (owners.TryGetValue(evidence.sourceGuid, out var set)) shared.UnionWith(set);
                shared.Remove(candidate.product.key);
                candidate.sharedProducts = shared.OrderBy(k => k, StringComparer.Ordinal).ToList();
                candidate.selected = !report.cancelled && candidate.kind == ScanMatchKind.Guid && !candidate.alreadyLinked && shared.Count == 0;
                report.candidates.Add(candidate);
            }
            report.candidates = report.candidates.OrderBy(c => c.product.name, StringComparer.Ordinal)
                .ThenBy(c => c.path, StringComparer.Ordinal).ToList();
            return report;
        }

        private static string DisplayName(PackageWork work) => work.displayName.Length == 0 ? Path.GetFileName(work.path) :
            Path.GetFileName(work.path) + " > " + work.displayName;

        public static PackageScanIndex ReadIndex(string package, string directory, Action<long> progress, ExistingAssetScanReport report)
        {
            // Verify content rather than trusting mtime/size: same-name replacements
            // must not reuse an old package index.
            var hash = UnityPackageReader.Hash(package, progress);
            var path = Path.Combine(directory, hash + ".json");
            try
            {
                var cached = BridgeStorage.Read<PackageScanIndex>(path);
                if (cached != null && cached.schemaVersion == 1 && cached.packageHash == hash && cached.entries != null && cached.entries.Count > 0 &&
                    cached.entries.All(e => e != null) &&
                    cached.entries.Select(e => e.guid).Distinct(StringComparer.OrdinalIgnoreCase).Count() == cached.entries.Count &&
                    cached.entries.All(e => e != null && UnityPackageReader.IsGuid(e.guid) && UnityPackageReader.ValidAssetPath(e.path) &&
                        (e.folder || (e.contentHash != null && e.contentHash.Length == 64))))
                { report.cacheHits++; return cached; }
            }
            catch (InvalidDataException) { /* Regenerate only this disposable index. */ }
            var entries = UnityPackageReader.Read(package, progress, true);
            var index = new PackageScanIndex { packageHash = hash, entries = entries };
            BridgeStorage.Write(path, index);
            return index;
        }

        private static bool IsProjectFile(string path) => UnityPackageReader.ValidAssetPath(path) &&
            File.Exists(Path.Combine(BridgeStorage.Root, path));
        private static string LocalHash(string path, Dictionary<string, string> cache)
        {
            if (!cache.TryGetValue(path, out var hash)) cache[path] = hash = UnityPackageReader.Hash(Path.Combine(BridgeStorage.Root, path));
            return hash;
        }
        private static void AddCandidate(Product product, PackageAsset entry, string path, ScanMatchKind kind,
            string hash, string name, Dictionary<string, string> localHashes, Dictionary<string, ScanCandidate> candidates)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return;
            var key = product.key + "\n" + guid;
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidate = new ScanCandidate { product = product, guid = guid, path = path, kind = kind,
                    projectContentHash = LocalHash(path, localHashes) };
                candidates[key] = candidate;
            }
            if (kind < candidate.kind) candidate.kind = kind;
            if (candidate.evidence.Any(e => e.packageHash == hash && e.sourceGuid == entry.guid)) return;
            candidate.evidence.Add(new ScanEvidence { packageHash = hash, packageName = name, sourceGuid = entry.guid,
                sourcePath = entry.path, sourceContentHash = entry.contentHash, contentEqual = candidate.projectContentHash == entry.contentHash });
        }

        public static int Apply(ExistingAssetScanReport report)
        {
            if (ImportCoordinator.HasPending) throw new InvalidOperationException("インポート処理が完了してからリンクしてください。");
            var selected = report.candidates.Where(c => c.selected && !c.alreadyLinked).ToList();
            // Validate every selected item before changing or saving the ledger.
            foreach (var item in selected)
                if (AssetDatabase.GUIDToAssetPath(item.guid) != item.path || !IsProjectFile(item.path) ||
                    UnityPackageReader.Hash(Path.Combine(BridgeStorage.Root, item.path)) != item.projectContentHash)
                    throw new InvalidOperationException("走査後に移動・変更・削除された項目があります。再走査してください: " + item.path);
            var ledger = BridgeStorage.Load();
            var added = 0;
            foreach (var item in selected)
            {
                if (ledger.links.Any(l => l.productKey == item.product.key && l.guid == item.guid)) continue;
                BridgeStorage.EnsureProduct(ledger, ProductReference.From(item.product));
                if (!BridgeStorage.AddLink(ledger, item.product.key, item.path, LinkRole.Original, "Scan")) continue;
                var link = ledger.links.Last();
                link.evidencePackageHashes = item.evidence.Select(e => e.packageHash).Distinct().ToList();
                link.scanMatchKind = item.kind.ToString(); link.matchedContentHash = item.projectContentHash;
                // Matching a distribution is evidence, not proof of the installed version.
                link.revisionId = "";
                added++;
            }
            if (added > 0) BridgeStorage.Save(ledger);
            foreach (var item in report.candidates)
                if (ledger.links.Any(l => l.productKey == item.product.key && l.guid == item.guid))
                { item.alreadyLinked = true; item.selected = false; }
            return added;
        }
    }
}
