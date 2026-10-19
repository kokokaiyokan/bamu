using System;
using System.Linq;
using bamu;

internal static class ProductListOrderTests
{
    internal static void Run(Action<bool, string> check)
    {
        var items = new[] {
            new Product { key = "new", registrationId = "r2", name = "B", shop = "A", boothId = "20" },
            new Product { key = "old", registrationId = "r1", name = "A", shop = "B", boothId = "3" },
            new Product { key = "remote", name = "C", shop = "C", boothId = "100" },
            new Product { key = "unknown", name = "D", shop = "D" }
        };
        string Keys(int category, bool descending) => string.Join(",", ProductListOrder.Sort(items, category, descending).Select(p => p.key));
        check(Keys(0, true) == "new,old,remote,unknown" && Keys(0, false) == "old,new,remote,unknown", "registration direction preserves unregistered items at end");
        check(Keys(1, false) == "old,new,remote,unknown" && Keys(1, true) == "unknown,remote,new,old", "product name ascending and descending");
        check(Keys(2, false) == "new,old,remote,unknown" && Keys(2, true) == "unknown,remote,old,new", "shop name ascending and descending");
        check(Keys(3, false) == "old,new,remote,unknown" && Keys(3, true) == "remote,new,old,unknown", "numeric product ID direction keeps missing ID at end");
        var categories = new[] { 0, 1, 1, 2, 2, 3, 3 };
        var directions = new[] { true, false, true, false, true, true, false };
        var migration = true;
        for (var old = 0; old < 7; old++)
        {
            ProductListOrder.FromLegacy(old, out var category, out var descending);
            migration &= category == categories[old] && descending == directions[old];
        }
        check(migration, "all seven legacy sort options migrate to category and direction");
        check(ProductListOrder.PageSize(50) == 50 && ProductListOrder.PageSize(100) == 100 && ProductListOrder.PageSize(200) == 200 &&
            ProductListOrder.PageSize(40) == 50 && ProductListOrder.PageSize(-1) == 50, "page size supports 50/100/200 and defaults invalid settings to 50");
        var many = Enumerable.Range(0, 251).Select(i => new Product { key = i.ToString(), boothId = i.ToString() }).ToList();
        foreach (var size in new[] { 50, 100, 200 })
        {
            var sorted = ProductListOrder.Sort(many, 3, true);
            var pages = (sorted.Count + size - 1) / size;
            var all = Enumerable.Range(0, pages).SelectMany(page => sorted.Skip(page * size).Take(size)).Select(p => p.key).ToList();
            check(all.Count == 251 && all.Distinct().Count() == 251 && all.First() == "250" && all.Last() == "0", "page size " + size + " covers final partial page without loss or duplication");
        }
    }
}
