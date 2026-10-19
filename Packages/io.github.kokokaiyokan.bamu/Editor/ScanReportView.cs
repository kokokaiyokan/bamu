using System;
using System.Collections.Generic;
using System.Linq;

namespace bamu
{
    internal sealed class ScanReportProductView
    {
        internal ScanProductResult result;
        internal int candidateCount, missing;
        internal List<IGrouping<string, ScanCandidate>> groups;
    }

    internal static class ScanReportView
    {
        // Keep the candidate objects: selection/link state changes must remain visible.
        internal static List<ScanReportProductView> Build(ExistingAssetScanReport report,
            IEnumerable<DisplayFolderRule> rules, Func<string, string> resolveFolder)
        {
            var candidates = report.candidates.ToLookup(c => c.product.key);
            var paths = new Dictionary<string, string>();
            string Folder(string guid)
            {
                if (!paths.TryGetValue(guid, out var path)) paths[guid] = path = resolveFolder(guid);
                return path;
            }
            return report.products.Select(result => new ScanReportProductView
            {
                result = result, missing = result.Missing,
                candidateCount = candidates[result.product.key].Count(),
                groups = candidates[result.product.key].GroupBy(c => LinkDisplayGroups.FolderFor(
                    c.product.key, LinkRole.Original, c.path, false, rules, Folder)).ToList()
            }).ToList();
        }
    }
}
