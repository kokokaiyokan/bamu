using System;
using System.Collections.Generic;
using System.Linq;

namespace bamu
{
    internal static class ProductListOrder
    {
        internal static void FromLegacy(int value, out int category, out bool descending)
        {
            category = value == 1 || value == 2 ? 1 : value == 3 || value == 4 ? 2 : value == 5 || value == 6 ? 3 : 0;
            descending = value == 0 || value == 2 || value == 4 || value == 5;
        }
        internal static int PageSize(int value) => value == 100 || value == 200 ? value : 50;

        internal static List<Product> Sort(IEnumerable<Product> products, int category, bool descending)
        {
            var list = products.ToList();
            if (category == 0)
            {
                // BlmLibrary supplies newest registrations first. Unregistered items have no BLM order.
                var registered = list.Where(p => !string.IsNullOrEmpty(p.registrationId));
                return (descending ? registered : registered.Reverse())
                    .Concat(list.Where(p => string.IsNullOrEmpty(p.registrationId))).ToList();
            }
            if (category == 3)
            {
                var withId = list.Where(p => long.TryParse(p.boothId, out _));
                Func<Product, long> id = p => long.Parse(p.boothId);
                return (descending ? withId.OrderByDescending(id) : withId.OrderBy(id)).ThenBy(p => p.key, StringComparer.Ordinal)
                    .Concat(list.Where(p => !long.TryParse(p.boothId, out _))).ToList();
            }
            Func<Product, string> field = category == 2 ? (Func<Product, string>)(p => p.shop ?? "") : p => p.name ?? "";
            return (descending ? list.OrderByDescending(field, StringComparer.CurrentCultureIgnoreCase) : list.OrderBy(field, StringComparer.CurrentCultureIgnoreCase))
                .ThenBy(p => p.name ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.key, StringComparer.Ordinal).ToList();
        }
    }
}
