using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using bamu;

internal static class CatalogTests
{
    public static void Run(Action<bool,string> check, string root)
    {
        var remote=new SyncStatus {
            products=new List<SyncProduct> {
                new SyncProduct {item_id="123",name="Remote title",shop="Remote shop",thumbnail_url="https://booth.pximg.net/sample",remote_available=true,sync_state="RemoteOnly"},
                new SyncProduct {item_id="123",name="Other variation"},
                new SyncProduct {item_id="456",name="Owned",registered=true}
            },
            files=new List<SyncFile> {
                new SyncFile {item_id="123",key="123:1:1"},new SyncFile {item_id="123",key="123:1:2"},
                new SyncFile {item_id="123",key="123:2:3"},new SyncFile {item_id="789",key="789:1:4"}
            }
        };
        var local=new List<Product> {new Product {key="booth:456",boothId="456",name="Custom title",shop="Local shop",directory="D:/BLM/b456"}};
        var linked=new List<ProductReference> {new ProductReference {key="booth:123",boothId="123",name="Linked"},new ProductReference {key="manual:1",name="Manual"}};
        var result=UnifiedCatalog.Build(local,linked,remote);
        check(result.Count==4,"catalog groups BOOTH products, variations and files into one card per product");
        check(result.Count(p=>p.boothId=="123")==1,"catalog remote-only product with multiple files has one card");
        check(result.Single(p=>p.boothId=="456").name=="Custom title","catalog preserves BLM custom product names");
        check(result.Single(p=>p.boothId=="123").thumbnailUrl=="https://booth.pximg.net/sample","catalog uses BOOTH image for unregistered product");
        check(UnifiedCatalog.State(result.Single(p=>p.boothId=="123"),remote)=="BOOTHのみ・未取得","catalog remote-only state is visible before download");
        check(UnifiedCatalog.State(result.Single(p=>p.boothId=="456"),remote)=="BLM保存済み","catalog preserves local saved state");
        check(local[0].name=="Custom title" && local[0].shop=="Local shop","catalog merge leaves original BLM records intact");
        check(UnifiedCatalog.Build(local,linked,null).Count==3,"catalog works offline with local and linked products");
        local.Add(new Product {key="booth:123",boothId="123",name="Downloaded",directory="D:/BLM/b123"});
        result=UnifiedCatalog.Build(local,linked,remote);
        check(result.Count==4 && result.Single(p=>p.boothId=="123").directory=="D:/BLM/b123","catalog download changes the existing product card instead of adding a new card");
        remote.watches = new List<SyncWatch> { new SyncWatch {item_id="123",hidden=true}, new SyncWatch {item_id="456",target_mode="exclude"} };
        check(result.Where(p => !UnifiedCatalog.Hidden(p, remote)).All(p => p.boothId != "123"), "hidden product with multiple files is excluded from the list even when BLM saved and asset linked");
        check(!UnifiedCatalog.Hidden(result.Single(p => p.boothId == "456"), remote), "old acquisition-only exclusion does not hide a product");
        check(result.Count == 4 && linked.Count == 2 && local.Count == 2, "hiding leaves catalog BLM and asset link records intact for restoration");
        check(!UnifiedCatalog.Hidden(result[0], null), "visibility handles missing sync snapshots");
        var snapshot=Path.Combine(root,"artifacts","unified-catalog-snapshot.json");
        if (File.Exists(snapshot))
        {
            var actual=JsonSerializer.Deserialize<SyncStatus>(File.ReadAllText(snapshot),new JsonSerializerOptions {IncludeFields=true});
            var current=BlmLibrary.Load(BridgeStorage.DefaultDatabase).products;
            var merged=UnifiedCatalog.Build(current,new List<ProductReference>(),actual);
            var expected=current.Select(p=>p.key).Union(actual.products.Select(p=>"booth:"+p.item_id)).Count();
            check(merged.Count==expected,"live unified snapshot produces product cards instead of download-file cards");
            // The user may download a product after this historical snapshot was captured.
            var currentIds = new HashSet<string>(current.Select(p => p.boothId));
            check(actual.products.Where(p=>!p.registered && !currentIds.Contains(p.item_id)).All(p=>string.IsNullOrEmpty(merged.Single(m=>m.boothId==p.item_id).directory)),"live BOOTH-only products remain selectable without a BLM directory");
            check(current.All(p=>merged.Single(m=>m.key==p.key).directory==p.directory),"current BLM registration wins over stale remote snapshots");
            check(actual.files.GroupBy(f=>f.item_id).Where(g=>g.Count()>1).All(g=>merged.Count(p=>p.boothId==g.Key)==1),"live multi-file and multi-variation assets each occupy one left-menu card");
        }
    }
}
