using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace bamu
{
    [Serializable] internal sealed class TagRule
    {
        public string tag = "", aliases = "";
    }
    [Serializable] internal sealed class TagDictionaryData
    {
        public int schemaVersion = 1;
        public string mode = "suggest";
        public List<TagRule> rules = new List<TagRule>();
    }
    internal sealed class TagDictionary
    {
        private readonly string path;
        public TagDictionaryData Data { get; private set; }
        public TagDictionary(string path) { this.path = path; Reload(); }
        public static string Normalize(string value) => (value ?? "").Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        public static TagDictionaryData Preset() => new TagDictionaryData { rules = new List<TagRule> {
            new TagRule { tag = "ピアス", aliases = "3Dピアス,piercing" },
            new TagRule { tag = "髪型", aliases = "髪形,髪,髪の毛,3D髪型,hairstyle,hair" },
            new TagRule { tag = "ドレス", aliases = "3Dドレス,dress" },
            new TagRule { tag = "靴", aliases = "シューズ,3D靴,shoes" },
            new TagRule { tag = "ネイル", aliases = "ネイルテクスチャ,nail,nails" },
            new TagRule { tag = "テクスチャ", aliases = "テクスチャー,3Dテクスチャ,texture" },
            new TagRule { tag = "メイク", aliases = "メイクアップ,メイクテクスチャ,メイクテクスチャー,メイクアップテクスチャ,makeup,make up,makeup texture,makeuptexture" }
        }};
        public void Reload()
        {
            var value = BridgeStorage.Read<TagDictionaryData>(path) ?? Preset();
            Validate(value); Data = value;
        }
        private static void Validate(TagDictionaryData value)
        {
            if (value.schemaVersion != 1 || !new[] { "automatic", "suggest", "off" }.Contains(value.mode) || value.rules == null ||
                value.rules.Any(r => r == null || string.IsNullOrWhiteSpace(r.tag)) ||
                value.rules.Select(r => Normalize(r.tag)).Distinct().Count() != value.rules.Count)
                throw new InvalidDataException("自動採用辞書の形式が不正です。採用タグは重複せず、空欄にしないでください。");
        }
        public void Save(TagDictionaryData value)
        {
            Validate(value);
            // Check the existing file too; never replace an unreadable dictionary.
            Reload();
            BridgeStorage.Write(path, value);
            Reload();
        }
        public List<string> Match(IEnumerable<string> boothTags)
        {
            if (Data.mode == "off") return new List<string>();
            var source = new HashSet<string>((boothTags ?? Enumerable.Empty<string>()).Select(Normalize));
            return Data.rules.Where(r => new[] { r.tag }.Concat((r.aliases ?? "").Split(new[] { ',', '、', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(Normalize).Any(source.Contains)).Select(r => r.tag.Trim()).ToList();
        }
    }
}
