using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace bamu
{
    [Serializable] public sealed class SyncConnection { public int port; public string token; }
    [Serializable] public sealed class SyncProduct
    {
        public string item_id, name, directory, shop, thumbnail_url, category, vrchat_state, target_mode, sync_state;
        public bool registered, remote_available, is_target;
        public List<string> tags = new List<string>(), recommended_file_keys = new List<string>();
    }
    [Serializable] public sealed class SyncFile { public string key, item_id, file_id, variation_id, name, saved_at, latest_sha256; public bool available; }
    [Serializable] public sealed class SyncJob { public string id, key, state, message; }
    [Serializable] public sealed class SyncWatch { public string item_id, target_mode = "auto", selection_mode = "auto"; public bool enabled = true, include_new_files, hidden; public List<string> file_keys = new List<string>(); }
    [Serializable] public sealed class SyncRevision { public string key, item_id, name, sha256, saved_at, target_path; public List<string> package_hashes; }
    [Serializable] public sealed class SyncStatus
    {
        public string version, transport, browser_status, library_root;
        public int interval_hours, verify_hours;
        public bool auto_enabled;
        public List<SyncProduct> products;
        public List<SyncFile> files;
        public List<SyncWatch> watches;
        public List<SyncJob> jobs;
        public List<SyncRevision> revisions;
    }
    [Serializable] public sealed class SyncBulkResult { public int queued_products, queued_files; }
    [Serializable] public sealed class SyncRetry { public string job_id; }
    [Serializable] public sealed class SyncIntervals { public int interval_hours, verify_hours; }
    [Serializable] public sealed class SyncSettings { public int interval_hours; public bool auto_enabled; }

    public static class BoothSyncVersions
    {
        public static bool HasNewSavedVersion(string productKey, ProjectLedger ledger, SyncStatus status)
        {
            if (status?.revisions == null || status.files == null) return false;
            var item = productKey.StartsWith("booth:", StringComparison.Ordinal) ? productKey.Substring(6) : "";
            foreach (var file in status.files.Where(f => f.item_id == item && !string.IsNullOrEmpty(f.latest_sha256)))
            {
                if (status.files.Any(f => f.item_id == item && !string.IsNullOrEmpty(f.latest_sha256) &&
                    SameLineage(file.key, file.name, f.key, f.name) && CompareVersions(f.name, file.name) > 0)) continue;
                var history = status.revisions.Where(r => r.key == file.key || SameLineage(r.key, r.name, file.key, file.name)).ToList();
                var latest = history.FirstOrDefault(r => r.sha256 == file.latest_sha256);
                if (latest == null) continue;
                foreach (var import in ledger.imports.Where(r => r.productKey == productKey &&
                    (r.state == "Success" || r.state == "PartialSuccess")).Reverse())
                {
                    var revision = ledger.revisions.FirstOrDefault(r => r.id == import.revisionId);
                    if (revision == null || !history.Any(r => ContainsHash(r, revision.sha256))) continue;
                    if (!ContainsHash(latest, revision.sha256)) return true;
                    break; // Compare the latest import in this file's lineage.
                }
            }
            return false;
        }
        private static bool ContainsHash(SyncRevision revision, string hash) => revision.sha256 == hash ||
            (revision.package_hashes != null && revision.package_hashes.Contains(hash));
        private static readonly Regex VersionPattern = new Regex(@"v(?:er(?:sion)?)?[ ._-]*(\d+(?:[._]\d+)*)", RegexOptions.IgnoreCase);
        private static bool SameLineage(string keyA, string nameA, string keyB, string nameB)
        {
            var a = (keyA ?? "").Split(':'); var b = (keyB ?? "").Split(':');
            if (a.Length != 3 || b.Length != 3 || a[0] != b[0] || a[1] != b[1]) return false;
            if (string.IsNullOrEmpty(nameA) || string.IsNullOrEmpty(nameB) || !VersionPattern.IsMatch(nameA) || !VersionPattern.IsMatch(nameB)) return false;
            return string.Equals(VersionPattern.Replace(nameA, "{version}", 1), VersionPattern.Replace(nameB, "{version}", 1), StringComparison.OrdinalIgnoreCase);
        }
        private static int CompareVersions(string a, string b)
        {
            var left = VersionPattern.Match(a ?? "").Groups[1].Value.Split('.', '_');
            var right = VersionPattern.Match(b ?? "").Groups[1].Value.Split('.', '_');
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var x = (i < left.Length ? left[i] : "0").TrimStart('0');
                var y = (i < right.Length ? right[i] : "0").TrimStart('0');
                var comparison = x.Length.CompareTo(y.Length);
                if (comparison == 0) comparison = string.CompareOrdinal(x, y);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
