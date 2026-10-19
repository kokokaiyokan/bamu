using System;
using bamu;
using UnityEngine;

internal static class ConverterRegistryTests
{
    private sealed class Converter : IPrefabConverter
    {
        public string Id { get; set; }
        public string DisplayName => "Test converter";
        public bool Available => true;
        public bool HasPending { get; private set; }
        public string Notice => "";
        public void Open(Product product, GameObject prefab) { HasPending = true; }
        public void Cancel() { HasPending = false; }
    }

    internal static void Run(Action<bool, string> check)
    {
        check(PrefabConverterRegistry.All.Count == 0, "core has no built-in converter dependency");
        var first = new Converter { Id = "z-test" };
        var replacement = new Converter { Id = "z-test" };
        var other = new Converter { Id = "a-test" };
        var events = 0;
        Action changed = () => events++;
        PrefabConverterRegistry.Changed += changed;
        try
        {
            PrefabConverterRegistry.Register(first);
            PrefabConverterRegistry.Register(first);
            check(PrefabConverterRegistry.All.Count == 1 && events == 1, "registering the same converter is idempotent");
            check(PrefabConverterRegistry.LabelFor(first.Id) == first.DisplayName, "link labels use optional module display name");
            PrefabConverterRegistry.Register(replacement);
            PrefabConverterRegistry.Unregister(first);
            check(PrefabConverterRegistry.All.Count == 1 && ReferenceEquals(PrefabConverterRegistry.All[0], replacement), "stale module removal cannot remove its replacement");
            PrefabConverterRegistry.Register(other);
            var snapshot = PrefabConverterRegistry.All;
            check(snapshot.Count == 2 && snapshot[0].Id == "a-test", "converter menu order is deterministic");
            PrefabConverterRegistry.Unregister(other);
            check(snapshot.Count == 2 && PrefabConverterRegistry.All.Count == 1, "module removal does not mutate a GUI enumeration snapshot");
            replacement.Open(new Product(), new GameObject());
            check(replacement.HasPending, "registered optional converter accepts product and prefab");
            replacement.Cancel();
            check(!replacement.HasPending, "optional converter owns cancellation state");
            PrefabConverterRegistry.Unregister(replacement);
            check(PrefabConverterRegistry.All.Count == 0 && PrefabConverterRegistry.LabelFor("legacy-converter") == "legacy-converter",
                "removing all modules leaves core usable and historical link method readable");
            var rejected = false;
            try { PrefabConverterRegistry.Register(new Converter { Id = "" }); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && PrefabConverterRegistry.All.Count == 0, "invalid optional module cannot pollute registry");
        }
        finally
        {
            PrefabConverterRegistry.Changed -= changed;
            PrefabConverterRegistry.Unregister(first); PrefabConverterRegistry.Unregister(replacement); PrefabConverterRegistry.Unregister(other);
        }
    }
}
