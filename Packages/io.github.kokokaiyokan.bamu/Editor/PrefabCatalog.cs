using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace bamu
{
    internal sealed class PrefabCandidate
    {
        internal string guid, path;
        internal LinkRole role;
    }

    internal static class PrefabCatalog
    {
        internal static bool IsPrefab(string path) => path != null && path.StartsWith("Assets/", StringComparison.Ordinal) &&
            path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        internal static List<PrefabCandidate> Candidates(ProjectLedger ledger, string productKey,
            Func<string, string> resolve, Func<string, IEnumerable<string>> findInFolder)
        {
            var found = new Dictionary<string, PrefabCandidate>(StringComparer.Ordinal);
            void Add(string guid, LinkRole role)
            {
                var path = resolve(guid);
                if (!IsPrefab(path)) return;
                if (!found.TryGetValue(guid, out var existing) || role < existing.role)
                    found[guid] = new PrefabCandidate { guid = guid, path = path, role = role };
            }
            foreach (var link in ledger.links.Where(l => l.productKey == productKey))
            {
                if (!link.folder) Add(link.guid, link.role);
                else if (link.includeDescendants)
                {
                    var folder = resolve(link.guid);
                    if (!string.IsNullOrEmpty(folder))
                        foreach (var guid in findInFolder(folder)) Add(guid, link.role);
                }
            }
            foreach (var guid in ledger.prefabSelections.FirstOrDefault(s => s.productKey == productKey)?.guids ?? new List<string>())
                Add(guid, LinkRole.Reference);
            return found.Values.OrderBy(c => c.role).ThenBy(c => DefaultRank(c.path))
                .ThenBy(c => c.path.Count(ch => ch == '/')).ThenBy(c => c.path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int DefaultRank(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            return name.Contains("default") || name.Contains("デフォルト") || name.Contains("standard") || name.Contains("基本") ? 0 : 1;
        }

        internal static List<string> Selected(ProjectLedger ledger, string productKey, List<PrefabCandidate> candidates)
        {
            var selection = ledger.prefabSelections.FirstOrDefault(s => s.productKey == productKey);
            // An explicit empty selection stays empty; deleting the record restores automatic selection.
            return selection == null ? candidates.Take(1).Select(c => c.guid).ToList() :
                (selection.guids ?? new List<string>()).Distinct(StringComparer.Ordinal).ToList();
        }

        internal static void SetSelected(ProjectLedger ledger, string productKey, IEnumerable<string> guids)
        {
            ledger.prefabSelections.RemoveAll(s => s.productKey == productKey);
            ledger.prefabSelections.Add(new ProductPrefabSelection { productKey = productKey,
                guids = guids.Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.Ordinal).ToList() });
        }
    }
}
