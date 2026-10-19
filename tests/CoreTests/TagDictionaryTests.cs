using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using bamu;

internal static class TagDictionaryTests
{
    public static void Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "artifacts", "dictionary-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "dictionary.json");
        var dictionary = new TagDictionary(path);
        check(dictionary.Data.mode == "suggest" && dictionary.Data.rules.Count == 7 && !File.Exists(path),
            "dictionary starts with seven preset terms in suggestion-only mode without writing settings");
        var raw = new[] { "ピアス", "髪型", "ドレス", "靴", "ネイル", "テクスチャ", "メイク", "ラムネ", "ラムネ対応", "VRChat", "新しいアバター名" };
        check(dictionary.Match(raw).Count == 7 && !dictionary.Match(raw).Contains("ラムネ"),
            "only dictionary terms match regardless of unknown avatar or platform tags");
        check(dictionary.Match(new[] { "ヘッドドレス", "ラムネ用テクスチャ", "髪型対応", "ピアスセット" }).Count == 0,
            "dictionary matching never extracts substrings from compound tags");
        check(dictionary.Match(new[] { "髪形", "3D髪型", "hair", " ＭＡＫＥＵＰ ", "texture" }).SequenceEqual(new[] { "髪型", "テクスチャ", "メイク" }),
            "explicit aliases normalize case and width and merge repeated meanings");
        var tags = new ProductTags(Path.Combine(folder, "tags.json"));
        var product = new SyncProduct { item_id = "123", tags = raw.ToList() };
        var status = new SyncStatus { products = new List<SyncProduct> { product } };
        tags.Add("booth:123", "手動タグ");
        tags.SyncAutomatic(status, dictionary);
        check(tags.Get("booth:123").SequenceEqual(new[] { "手動タグ" }) && tags.Candidates("booth:123", dictionary).Count == 7,
            "suggestion-only mode presents dictionary candidates without adopting tags");
        tags.Add("booth:123", "ピアス");
        check(!tags.Candidates("booth:123", dictionary).Contains("ピアス") && !tags.IsAutomatic("booth:123", "ピアス"),
            "accepting a BOOTH candidate creates a manual tag and removes that candidate");
        dictionary.Data.mode = "automatic"; dictionary.Save(dictionary.Data);
        tags.SyncAutomatic(status, dictionary);
        check(tags.Get("booth:123").Count == 8 && tags.IsAutomatic("booth:123", "髪型") && !tags.IsAutomatic("booth:123", "ピアス"),
            "automatic mode adopts approved terms while preserving manual tag provenance");
        tags.Remove("booth:123", "髪型");
        tags.SyncAutomatic(status, dictionary);
        tags = new ProductTags(Path.Combine(folder, "tags.json"));
        tags.SyncAutomatic(status, new TagDictionary(path));
        check(!tags.Get("booth:123").Contains("髪型"), "removed automatic tags stay dismissed across refresh and restart");
        dictionary.Data.mode = "suggest"; dictionary.Save(dictionary.Data);
        tags.SyncAutomatic(null, dictionary);
        check(tags.Get("booth:123").SequenceEqual(new[] { "手動タグ", "ピアス" }) && tags.Candidates("booth:123", dictionary).Count == 6,
            "switching to suggestions retracts only automatic tags and retains cached candidates offline");
        dictionary.Data.mode = "off"; dictionary.Save(dictionary.Data);
        tags.SyncAutomatic(status, dictionary);
        check(tags.Candidates("booth:123", dictionary).Count == 0 && tags.Get("booth:123").Count == 2 && dictionary.Match(raw).Count == 0,
            "dictionary-off mode has no generated tags or BOOTH candidates while preserving manual tags");
        dictionary.Data.mode = "automatic";
        dictionary.Data.rules.Add(new TagRule { tag = "ゆめかわ", aliases = "ゆめかわいい" }); dictionary.Save(dictionary.Data);
        product.tags.Add("ゆめかわいい"); tags.SyncAutomatic(status, dictionary);
        check(tags.Get("booth:123").Contains("ゆめかわ"), "user dictionary additions and aliases are applied to existing products");
        dictionary.Data.rules.RemoveAll(r => r.tag == "ゆめかわ"); dictionary.Save(dictionary.Data);
        tags.SyncAutomatic(null, dictionary);
        check(!tags.Get("booth:123").Contains("ゆめかわ") && tags.Get("booth:123").Contains("手動タグ"),
            "removing a dictionary term retracts its automatic tag without deleting manual tags");
        tags.Add("booth:123", "髪型"); tags.SyncAutomatic(status, dictionary);
        check(tags.Get("booth:123").Contains("髪型") && !tags.IsAutomatic("booth:123", "髪型"),
            "a dismissed automatic tag can be explicitly restored as a manual tag");
        var categoryTagsPath = Path.Combine(folder, "category-tags.json");
        var categoryTags = new ProductTags(categoryTagsPath);
        var categoryProduct = new SyncProduct { item_id = "456", category = "3D装飾品" };
        var categoryStatus = new SyncStatus { products = new List<SyncProduct> { categoryProduct } };
        categoryTags.SyncAutomatic(categoryStatus, dictionary);
        check(categoryTags.SubcategoryCandidates("booth:456", dictionary).SequenceEqual(new[] { "3D装飾品" }) && categoryTags.Get("booth:456").Count == 0,
            "subcategory outside the dictionary is offered but never automatically adopted");
        categoryProduct.category = "3D髪型";
        categoryTags.SyncAutomatic(categoryStatus, dictionary);
        check(categoryTags.SubcategoryCandidates("booth:456", dictionary).SequenceEqual(new[] { "髪型" }) && categoryTags.Subcategory("booth:456") == "3D髪型",
            "subcategory aliases use the preferred tag while preserving the original category label");
        categoryTags = new ProductTags(categoryTagsPath);
        categoryTags.SyncAutomatic(null, dictionary);
        check(categoryTags.SubcategoryCandidates("booth:456", dictionary).Single() == "髪型",
            "subcategory candidates survive restart and offline dictionary application");
        categoryTags.Add("booth:456", "髪型");
        check(categoryTags.SubcategoryCandidates("booth:456", dictionary).Count == 0 && !categoryTags.IsAutomatic("booth:456", "髪型"),
            "accepting a subcategory creates a manual tag and suppresses duplicate candidates");
        dictionary.Data.mode = "off"; dictionary.Save(dictionary.Data);
        categoryTags.SyncAutomatic(categoryStatus, dictionary);
        check(categoryTags.SubcategoryCandidates("booth:456", dictionary).Count == 0 && categoryTags.Get("booth:456").Single() == "髪型",
            "dictionary-off mode hides subcategories without removing accepted manual tags");
        dictionary.Data.mode = "suggest"; dictionary.Save(dictionary.Data);
        categoryProduct.category = "3D衣装";
        categoryTags.SyncAutomatic(categoryStatus, dictionary);
        check(categoryTags.SubcategoryCandidates("booth:456", dictionary).Single() == "3D衣装" && categoryTags.Get("booth:456").Single() == "髪型",
            "category changes update suggestions without rewriting accepted tags");
        dictionary.Data.mode = "automatic";
        dictionary.Data.rules.Clear(); dictionary.Save(dictionary.Data); tags.SyncAutomatic(null, dictionary);
        check(tags.Get("booth:123").SequenceEqual(new[] { "手動タグ", "ピアス", "髪型" }),
            "an empty dictionary adopts nothing and keeps all manual tags");
        check(new TagDictionary(path).Data.mode == "automatic" && new TagDictionary(path).Data.rules.Count == 0,
            "dictionary mode and user-edited rules survive restart without restoring presets");
        dictionary.Data.mode = "invalid";
        var before = File.ReadAllText(path); var rejected = false;
        try { dictionary.Save(dictionary.Data); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(path) == before, "invalid adoption settings do not overwrite the saved dictionary");
        File.WriteAllText(path, "{\"schemaVersion\":99,\"mode\":\"automatic\",\"rules\":[]}");
        before = File.ReadAllText(path); rejected = false;
        try { dictionary.Save(TagDictionary.Preset()); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(path) == before, "an unreadable dictionary cannot be silently replaced with presets");
        var legacyPath = Path.Combine(folder, "legacy-tags.json");
        File.WriteAllText(legacyPath, "{\"schemaVersion\":1,\"products\":[{\"key\":\"booth:123\",\"tags\":[\"legacy\"]}]}");
        tags = new ProductTags(legacyPath); tags.SyncAutomatic(status, new TagDictionary(Path.Combine(folder, "preset.json")));
        check(tags.Get("booth:123").Single() == "legacy" && tags.Candidates("booth:123", new TagDictionary(Path.Combine(folder, "preset.json"))).Count == 7,
            "manual-only tag files migrate without losing existing assignments");
        var samples = Path.Combine(root, "artifacts", "research", "tag-samples.json");
        if (File.Exists(samples))
        {
            var expected = new Dictionary<string, string> { { "8616342", "ピアス" }, { "7451985", "髪型" }, { "3626398", "ドレス" }, { "7852794", "メイク" } };
            using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(samples)))
            {
                var preset = new TagDictionary(Path.Combine(folder, "real-sample-preset.json"));
                foreach (var sample in document.RootElement.EnumerateArray())
                {
                    var id = sample.GetProperty("id").ToString();
                    var actual = preset.Match(sample.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
                    check(actual.SequenceEqual(new[] { expected[id] }), "real BOOTH sample " + id + " accepts only its preset category and excludes avatar tags");
                }
            }
        }
    }
}
