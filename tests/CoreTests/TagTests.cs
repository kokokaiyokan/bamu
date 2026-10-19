using System;
using System.IO;
using System.Linq;
using bamu;

internal static class TagTests
{
    public static void Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "artifacts", "tag-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "product-tags.json");
        var a = new ProductTags(path);
        a.Add("booth:123", "  ピアス  ");
        a.Add("booth:123", "Texture");
        a.Add("booth:123", "texture");
        var b = new ProductTags(path);
        check(b.Get("booth:123").SequenceEqual(new[] { "ピアス", "Texture" }), "tags persist across Manager instances with whitespace and duplicate normalization");
        b.Add("booth:456", "ゆめかわ");
        a.Add("manual:custom", "髪型");
        b.Reload();
        check(b.Get("booth:456").Single() == "ゆめかわ" && b.Get("manual:custom").Single() == "髪型", "tag writes preserve other products and another Manager's changes");
        check(b.Suggest("tex", "booth:456").SequenceEqual(new[] { "Texture" }), "tag suggestions match existing names without case sensitivity");
        check(!b.Suggest("", "booth:123").Contains("Texture"), "tag suggestions exclude tags already on the selected product");
        b.Remove("booth:123", "texture");
        a.Reload();
        check(a.Get("booth:123").Single() == "ピアス" && a.Get("booth:456").Single() == "ゆめかわ", "removing a tag preserves other products and remaining tags");
        b.Remove("booth:123", "ピアス");
        check(new ProductTags(path).Get("booth:123").Count == 0, "removing the last tag persists an empty product assignment");
        File.WriteAllText(path, "{\"schemaVersion\":99,\"products\":[]}");
        var before = File.ReadAllText(path);
        var rejected = false;
        try { b.Add("booth:456", "new"); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(path) == before, "unknown tag format is rejected without overwriting the tag file");
        File.WriteAllText(path, "broken json");
        rejected = false;
        try { new ProductTags(path); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(path) == "broken json", "damaged tag data is retained for recovery");
        var originalDataPath = UnityEngine.Application.dataPath;
        try
        {
            UnityEngine.Application.dataPath = Path.Combine(folder, "ProjectA", "Assets");
            var projectAPath = BridgeStorage.TagsPath;
            UnityEngine.Application.dataPath = Path.Combine(folder, "ProjectB", "Assets");
            check(BridgeStorage.TagsPath == projectAPath && Path.GetFileName(projectAPath) == "product-tags.json",
                "different Unity projects use the same user-level tag storage path");
        }
        finally { UnityEngine.Application.dataPath = originalDataPath; }
    }
}
