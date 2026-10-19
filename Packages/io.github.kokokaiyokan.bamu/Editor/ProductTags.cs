using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace bamu
{
    [Serializable] internal sealed class ProductTagData
    {
        public int schemaVersion = 1;
        public List<ProductTagEntry> products = new List<ProductTagEntry>();
    }
    [Serializable] internal sealed class ProductTagEntry
    {
        public string key;
        public string boothSubcategory = "";
        public List<string> tags = new List<string>();
        public List<string> boothTags = new List<string>(), autoTags = new List<string>(), dismissedTags = new List<string>();
    }
    internal sealed class ProductTags
    {
        private readonly string path;
        private ProductTagData data;
        public ProductTags(string path) { this.path = path; Reload(); }
        public void Reload()
        {
            var loaded = BridgeStorage.Read<ProductTagData>(path) ?? new ProductTagData();
            if (loaded.schemaVersion != 1 || loaded.products == null ||
                loaded.products.Any(p => p == null || string.IsNullOrEmpty(p.key) || p.tags == null || p.tags.Any(t => string.IsNullOrWhiteSpace(t))) ||
                loaded.products.Select(p => p.key).Distinct(StringComparer.Ordinal).Count() != loaded.products.Count)
                throw new InvalidDataException("タグデータを読み取れません。元のファイルは保持しました。");
            data = loaded;
            foreach (var entry in data.products)
            {
                // Optional fields preserve compatibility with manual-only 0.7.0 files.
                if (entry.boothTags == null) entry.boothTags = new List<string>();
                if (entry.autoTags == null) entry.autoTags = new List<string>();
                if (entry.dismissedTags == null) entry.dismissedTags = new List<string>();
            }
        }
        public List<string> Get(string key)
        {
            var entry = data.products.FirstOrDefault(p => p.key == key);
            return entry == null ? new List<string>() : entry.tags.Concat(entry.autoTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public bool IsAutomatic(string key, string tag)
        {
            var entry = data.products.FirstOrDefault(p => p.key == key);
            return entry != null && entry.autoTags.Contains(tag, StringComparer.OrdinalIgnoreCase) && !entry.tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        }
        public List<string> Candidates(string key, TagDictionary dictionary)
        {
            var entry = data.products.FirstOrDefault(p => p.key == key);
            return dictionary.Data.mode != "suggest" || entry == null ? new List<string>() :
                dictionary.Match(entry.boothTags).Except(Get(key), StringComparer.OrdinalIgnoreCase).ToList();
        }
        public string Subcategory(string key) => data.products.FirstOrDefault(p => p.key == key)?.boothSubcategory ?? "";
        public List<string> SubcategoryCandidates(string key, TagDictionary dictionary)
        {
            var category = Subcategory(key);
            if (dictionary.Data.mode == "off" || string.IsNullOrWhiteSpace(category)) return new List<string>();
            var normalized = dictionary.Match(new[] { category });
            // BOOTH's leaf category is a curated source, so unlike free-form tags
            // it may be offered even when it isn't in the user's dictionary.
            if (normalized.Count == 0) normalized.Add(category);
            return normalized.Except(Get(key), StringComparer.OrdinalIgnoreCase).ToList();
        }
        public List<string> Suggest(string query, string key) => data.products.SelectMany(p => Get(p.key))
            .Distinct(StringComparer.OrdinalIgnoreCase).Except(Get(key), StringComparer.OrdinalIgnoreCase)
            .Where(t => string.IsNullOrEmpty(query) || t.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).Take(8).ToList();
        public void Add(string key, string tag)
        {
            tag = (tag ?? "").Trim();
            if (string.IsNullOrEmpty(key) || tag.Length == 0) return;
            Reload(); // Preserve edits from another Manager window or Unity project.
            var entry = data.products.FirstOrDefault(p => p.key == key);
            if (entry == null) data.products.Add(entry = new ProductTagEntry { key = key });
            if (entry.tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;
            entry.tags.Add(tag);
            entry.dismissedTags.RemoveAll(t => TagDictionary.Normalize(t) == TagDictionary.Normalize(tag));
            BridgeStorage.Write(path, data);
        }
        public void Remove(string key, string tag)
        {
            Reload();
            var entry = data.products.FirstOrDefault(p => p.key == key);
            if (entry == null) return;
            entry.tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            if (entry.autoTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0 &&
                !entry.dismissedTags.Any(t => TagDictionary.Normalize(t) == TagDictionary.Normalize(tag))) entry.dismissedTags.Add(tag);
            if (entry.tags.Count == 0 && entry.boothTags.Count == 0 && entry.dismissedTags.Count == 0 && string.IsNullOrEmpty(entry.boothSubcategory)) data.products.Remove(entry);
            BridgeStorage.Write(path, data);
        }
        public void SyncAutomatic(SyncStatus status, TagDictionary dictionary)
        {
            Reload();
            var changed = false;
            foreach (var remote in status?.products ?? new List<SyncProduct>())
            {
                if (string.IsNullOrEmpty(remote.item_id)) continue;
                var key = "booth:" + remote.item_id;
                var entry = data.products.FirstOrDefault(p => p.key == key);
                var source = remote.tags == null ? entry?.boothTags ?? new List<string>() :
                    remote.tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var category = (remote.category ?? "").Trim();
                if (entry == null && source.Count == 0 && category.Length == 0) continue;
                if (entry == null) { data.products.Add(entry = new ProductTagEntry { key = key }); changed = true; }
                if (!entry.boothTags.SequenceEqual(source)) { entry.boothTags = source; changed = true; }
                if (entry.boothSubcategory != category) { entry.boothSubcategory = category; changed = true; }
            }
            foreach (var entry in data.products)
            {
                var automatic = dictionary.Data.mode == "automatic" ? dictionary.Match(entry.boothTags)
                    .Where(t => !entry.dismissedTags.Any(d => TagDictionary.Normalize(t) == TagDictionary.Normalize(d))).ToList() : new List<string>();
                if (!entry.autoTags.SequenceEqual(automatic)) { entry.autoTags = automatic; changed = true; }
            }
            if (changed) BridgeStorage.Write(path, data);
        }
    }
}
