using System;
using System.Collections.Generic;
using System.IO;
using bamu;

internal static class SyncTests
{
    public static void Run(Action<bool, string> check)
    {
        var ledger = new ProjectLedger();
        var status = new SyncStatus {
            files = new List<SyncFile> { new SyncFile { key="123:1:2", item_id="123", latest_sha256="newzip" } },
            revisions = new List<SyncRevision> {
                new SyncRevision { key="123:1:2", sha256="oldzip", package_hashes=new List<string>{"oldpackage"} },
                new SyncRevision { key="123:1:2", sha256="newzip", package_hashes=new List<string>{"newpackage"} }
            }
        };
        check(!BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,status),"sync unknown imported version is not called outdated");
        ledger.revisions.Add(new ArtifactRevision { id="r1",sha256="oldpackage" });
        ledger.imports.Add(new ImportRecord { productKey="booth:123",revisionId="r1",state="Success" });
        check(BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,status),"sync detects newer ZIP containing different package");
        check(!BoothSyncVersions.HasNewSavedVersion("booth:456",ledger,status),"sync isolates products");
        ledger.revisions.Add(new ArtifactRevision { id="r2",sha256="newpackage" });
        ledger.imports.Add(new ImportRecord { productKey="booth:123",revisionId="r2",state="Cancelled" });
        check(BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,status),"sync cancelled import does not clear update");
        ledger.imports[1].state="PartialSuccess";
        check(!BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,status),"sync latest successful import clears update");
        ledger.imports.RemoveAt(1);
        status.revisions[1].package_hashes.Add("oldpackage");
        check(!BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,status),"sync changed ZIP with same imported package has no update");
        var root=Path.Combine(BridgeStorage.Root,"sync-product");
        var pending=Path.Combine(root,"_BoothUnityBridgeVersions",".pending-abc");
        var saved=Path.Combine(root,"_BoothUnityBridgeVersions","1","2","hash");
        Directory.CreateDirectory(pending); Directory.CreateDirectory(saved);
        File.WriteAllText(Path.Combine(pending,"partial.unitypackage"),"partial");
        File.WriteAllText(Path.Combine(saved,"saved.unitypackage"),"complete");
        var packages=BlmLibrary.FindPackages(new Product { directory=root });
        check(packages.Count==1 && packages[0].EndsWith("saved.unitypackage"),"sync unpublished staging is hidden from Unity import candidates");
        var lineage = new SyncStatus {
            files = new List<SyncFile> {
                new SyncFile {key="123:1:10",item_id="123",name="Dress_v1.0.zip",latest_sha256="oldzip"},
                new SyncFile {key="123:1:20",item_id="123",name="Dress_v2.0.zip",latest_sha256="newzip"}
            },
            revisions = new List<SyncRevision> {
                new SyncRevision {key="123:1:10",name="Dress_v1.0.zip",sha256="oldzip",package_hashes=new List<string>{"oldpackage"}},
                new SyncRevision {key="123:1:20",name="Dress_v2.0.zip",sha256="newzip",package_hashes=new List<string>{"newpackage"}}
            }
        };
        check(BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,lineage),"sync new BOOTH file ID follows the same versioned distribution lineage");
        ledger.imports.Add(new ImportRecord {productKey="booth:123",revisionId="r2",state="Success"});
        check(!BoothSyncVersions.HasNewSavedVersion("booth:123",ledger,lineage),"sync old available downloads do not mark an updated project outdated");
    }
}
