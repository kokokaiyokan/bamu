using System;
using System.Collections.Generic;
using System.Linq;

namespace bamu
{
    public static class UnifiedCatalog
    {
        public static List<Product> Build(IEnumerable<Product> local, IEnumerable<ProductReference> linked, SyncStatus remote)
        {
            var result = new Dictionary<string, Product>(StringComparer.Ordinal);
            foreach (var product in local ?? Enumerable.Empty<Product>())
            {
                var key = string.IsNullOrEmpty(product.boothId) ? product.key : "booth:" + product.boothId;
                if (string.IsNullOrEmpty(key)) continue;
                result[key] = new Product { key = key, registrationId = product.registrationId, boothId = product.boothId,
                    name = product.name, shop = product.shop, thumbnailUrl = product.thumbnailUrl,
                    localThumbnail = product.localThumbnail, directory = product.directory };
            }
            foreach (var product in remote?.products ?? new List<SyncProduct>())
            {
                if (string.IsNullOrEmpty(product.item_id)) continue;
                var key = "booth:" + product.item_id;
                if (!result.TryGetValue(key, out var existing))
                    result[key] = existing = new Product { key = key, boothId = product.item_id, directory = product.registered ? product.directory : "" };
                if (string.IsNullOrEmpty(existing.name)) existing.name = product.name;
                if (string.IsNullOrEmpty(existing.shop)) existing.shop = product.shop;
                if (string.IsNullOrEmpty(existing.thumbnailUrl)) existing.thumbnailUrl = product.thumbnail_url;
            }
            // Old companion snapshots can still expose only file-level metadata.
            foreach (var file in remote?.files ?? new List<SyncFile>())
            {
                if (string.IsNullOrEmpty(file.item_id)) continue;
                var key = "booth:" + file.item_id;
                if (!result.ContainsKey(key)) result[key] = new Product { key = key, boothId = file.item_id, name = "BOOTH #" + file.item_id, directory = "" };
            }
            foreach (var product in linked ?? Enumerable.Empty<ProductReference>())
                if (!result.ContainsKey(product.key)) result[product.key] = new Product { key = product.key, boothId = product.boothId,
                    name = product.name, shop = "リンク情報のみ", directory = "" };
            return result.Values.ToList();
        }

        public static SyncProduct Remote(Product product, SyncStatus status) => status?.products?.FirstOrDefault(p => p.item_id == product.boothId);
        public static string State(Product product, SyncStatus status)
        {
            var remote = Remote(product, status);
            switch (remote?.sync_state)
            {
                case "NeedsAttention": return "取得の確認が必要";
                case "Processing": return "取得・保存確認中";
                case "Queued": return "照合待ち";
                case "UpdateAvailable": return "更新候補あり";
            }
            if (!string.IsNullOrEmpty(product.directory)) return "BLM保存済み";
            return remote != null ? (remote.remote_available ? "BOOTHのみ・未取得" : "BOOTH一覧で未確認") : "リンク情報のみ";
        }
    }
}
