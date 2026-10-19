using System;
using System.Collections.Generic;
using System.Linq;

namespace bamu
{
    // One snapshot per ledger/project change, rather than GUID lookups per GUI event.
    internal sealed class LinkViewCache
    {
        private readonly ProjectLedger ledger;
        private readonly Func<AssetLink, string> resolve;
        private readonly Func<string, string> resolveFolder;
        private readonly Dictionary<string, string> paths = new Dictionary<string, string>();
        private readonly Dictionary<string, List<LinkDisplayGroup>> groups = new Dictionary<string, List<LinkDisplayGroup>>();
        private readonly Dictionary<string, HashSet<string>> related = new Dictionary<string, HashSet<string>>();

        internal LinkViewCache(ProjectLedger ledger, Func<AssetLink, string> resolve, Func<string, string> resolveFolder)
        { this.ledger = ledger; this.resolve = resolve; this.resolveFolder = resolveFolder; }

        private string Path(AssetLink link)
        {
            if (!paths.TryGetValue(link.guid, out var path)) paths[link.guid] = path = resolve(link);
            return path;
        }

        private string Folder(string guid)
        {
            if (!paths.TryGetValue(guid, out var path)) paths[guid] = path = resolveFolder(guid);
            return path;
        }

        internal List<LinkDisplayGroup> Groups(string key)
        {
            if (!groups.TryGetValue(key, out var value))
                groups[key] = value = LinkDisplayGroups.Build(ledger.links.Where(l => l.productKey == key), Path, ledger.displayFolders, Folder);
            return value;
        }

        internal HashSet<string> Related(string path)
        {
            path = path ?? "";
            if (!related.TryGetValue(path, out var value))
                related[path] = value = LinkDisplayGroups.RelatedProducts(ledger.links, path, Path, ledger.displayFolders, Folder);
            return value;
        }
    }
}
