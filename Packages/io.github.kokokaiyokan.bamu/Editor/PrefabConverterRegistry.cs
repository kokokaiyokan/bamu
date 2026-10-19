using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace bamu
{
    /// <summary>Optional Editor module for handing a prefab to an external converter.</summary>
    public interface IPrefabConverter
    {
        string Id { get; }
        string DisplayName { get; }
        bool Available { get; }
        bool HasPending { get; }
        string Notice { get; }
        void Open(Product product, GameObject prefab);
        void Cancel();
    }

    public static class PrefabConverterRegistry
    {
        private static readonly Dictionary<string, IPrefabConverter> Modules = new Dictionary<string, IPrefabConverter>(StringComparer.Ordinal);
        public static event Action Changed;
        public static IReadOnlyList<IPrefabConverter> All => Modules.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();

        public static void Register(IPrefabConverter module)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.Id) || string.IsNullOrWhiteSpace(module.DisplayName))
                throw new ArgumentException("連携モジュールにはIDと表示名が必要です。");
            if (Modules.TryGetValue(module.Id, out var previous) && ReferenceEquals(previous, module)) return;
            Modules[module.Id] = module;
            Changed?.Invoke();
        }

        public static void Unregister(IPrefabConverter module)
        {
            if (module == null || !Modules.TryGetValue(module.Id, out var current) || !ReferenceEquals(current, module)) return;
            Modules.Remove(module.Id);
            Changed?.Invoke();
        }

        public static string LabelFor(string id) => id != null && Modules.TryGetValue(id, out var module)
            ? module.DisplayName : (string.IsNullOrEmpty(id) ? "不明" : id);
    }
}
