using System;
using System.Collections.Generic;
using System.Linq;

namespace bamu
{
    // A view over exact links: grouping must never turn a partially imported
    // folder into a recursive association with files that were not imported.
    public sealed class LinkDisplayGroup
    {
        public string path, productKey, method, overrideId, customFolderGuid;
        public LinkRole role;
        public bool groupedImport;
        public int missingCount;
        public List<AssetLink> links = new List<AssetLink>();
    }

    public static class LinkDisplayGroups
    {
        public static HashSet<string> RelatedProducts(IEnumerable<AssetLink> source, string selectedPath, Func<AssetLink, string> resolve,
            IEnumerable<DisplayFolderRule> rules = null, Func<string, string> resolveFolder = null)
        {
            var links = source.ToList();
            var keys = new HashSet<string>();
            if (string.IsNullOrEmpty(selectedPath)) return keys;
            foreach (var link in links)
            {
                var current = resolve(link);
                if (!string.IsNullOrEmpty(current) && (current == selectedPath ||
                    (link.folder && link.includeDescendants && BridgeStorage.ContainsPath(current, selectedPath))))
                    keys.Add(link.productKey);
            }
            // Selecting a displayed container should reverse-navigate to its
            // product. This does not associate arbitrary children with it.
            foreach (var group in Build(links, resolve, rules, resolveFolder))
                if (group.groupedImport && group.path == selectedPath && group.links.Any(l => !string.IsNullOrEmpty(resolve(l))) &&
                    (string.IsNullOrEmpty(group.customFolderGuid) || !string.IsNullOrEmpty((resolveFolder ?? UnityEditor.AssetDatabase.GUIDToAssetPath)(group.customFolderGuid))))
                    keys.Add(group.productKey);
            return keys;
        }

        public static List<LinkDisplayGroup> Build(IEnumerable<AssetLink> source, Func<AssetLink, string> resolve,
            IEnumerable<DisplayFolderRule> rules = null, Func<string, string> resolveFolder = null)
        {
            var groups = new List<LinkDisplayGroup>();
            foreach (var link in source)
            {
                var current = resolve(link);
                var effective = string.IsNullOrEmpty(current) ? link.lastPath : current;
                effective = (effective ?? "").Replace('\\', '/');
                var automatic = link.method == "Import" || link.method == "Scan";
                var rule = automatic ? FindRule(link.productKey, link.role, effective, rules, resolveFolder) : null;
                var path = automatic ? (rule == null ? ImportFolder(effective, link.folder) : RulePath(rule, resolveFolder)) : effective;
                var group = automatic ? groups.FirstOrDefault(g => g.groupedImport && g.productKey == link.productKey &&
                    g.path == path && g.role == link.role) : null;
                if (group == null)
                {
                    group = new LinkDisplayGroup { path = path, productKey = link.productKey, role = link.role,
                        method = link.method, groupedImport = automatic, overrideId = rule?.id, customFolderGuid = rule?.folderGuid };
                    groups.Add(group);
                }
                group.links.Add(link);
                if (string.IsNullOrEmpty(current)) group.missingCount++;
            }

            // Exported author/root folders are structural entries. If they contain
            // no directly linked files, show them with a descendant asset group
            // instead of adding a redundant author-level row. Every link still
            // belongs to exactly one removable display group.
            foreach (var group in groups.Where(g => g.groupedImport && string.IsNullOrEmpty(g.overrideId) && g.links.All(l => l.folder))
                .OrderByDescending(g => g.path.Length).ToList())
            {
                var descendant = groups.Where(g => g != group && g.groupedImport && g.productKey == group.productKey &&
                    g.role == group.role && g.path.StartsWith(group.path + "/", StringComparison.Ordinal))
                    .OrderBy(g => g.path, StringComparer.Ordinal).FirstOrDefault();
                if (descendant == null) continue;
                descendant.links.AddRange(group.links);
                descendant.missingCount += group.missingCount;
                groups.Remove(group);
            }
            return groups.OrderBy(g => g.path, StringComparer.Ordinal).ThenBy(g => g.role).ToList();
        }

        // Assets / author / asset. If fewer levels exist, use the nearest folder.
        // Root-level files are grouped under Assets, never displayed individually.
        public static string ImportFolder(string path, bool isFolder)
        {
            if (!isFolder)
            {
                var slash = path.LastIndexOf('/');
                path = slash < 0 ? "Assets" : path.Substring(0, slash);
            }
            return string.Join("/", path.Split('/').Take(3));
        }

        public static string FolderFor(string productKey, LinkRole role, string path, bool isFolder,
            IEnumerable<DisplayFolderRule> rules, Func<string, string> resolveFolder = null)
        {
            var rule = FindRule(productKey, role, path, rules, resolveFolder);
            return rule == null ? ImportFolder(path, isFolder) : RulePath(rule, resolveFolder);
        }
        public static string RulePath(DisplayFolderRule rule, Func<string, string> resolveFolder = null)
        {
            var current = (resolveFolder ?? UnityEditor.AssetDatabase.GUIDToAssetPath)(rule.folderGuid);
            return string.IsNullOrEmpty(current) ? rule.lastPath : current;
        }
        private static DisplayFolderRule FindRule(string productKey, LinkRole role, string path,
            IEnumerable<DisplayFolderRule> rules, Func<string, string> resolveFolder)
        {
            return rules?.Where(r => r.productKey == productKey && r.role == role &&
                BridgeStorage.ContainsPath(RulePath(r, resolveFolder), path))
                .OrderByDescending(r => RulePath(r, resolveFolder).Length).FirstOrDefault();
        }
    }
}
