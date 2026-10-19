using System;
using System.Collections.Generic;

namespace bamu
{
    [Serializable]
    public sealed class Product
    {
        public string key, registrationId, boothId, name, shop, thumbnailUrl, localThumbnail, directory;
        public string Url => string.IsNullOrEmpty(boothId) ? "" : "https://booth.pm/ja/items/" + boothId;
    }

    [Serializable]
    public sealed class ProductReference
    {
        public string key, boothId, name;
        public static ProductReference From(Product product) => new ProductReference
            { key = product.key, boothId = product.boothId, name = product.name };
    }

    public enum LinkRole { Original, ModifiedCopy, Reference }

    [Serializable]
    public sealed class AssetLink
    {
        public string id, productKey, guid, lastPath, method, revisionId, createdUtc;
        public bool folder, includeDescendants;
        public LinkRole role;
        public List<string> evidencePackageHashes = new List<string>();
        public string scanMatchKind, matchedContentHash;
    }

    [Serializable]
    public sealed class ArtifactRevision
    {
        public string id, productKey, fileName, sha256, label, recordedUtc;
        public long size;
    }

    [Serializable]
    public sealed class ImportedAsset
    {
        public string guid, path, contentHash, metaHash;
        public bool existedBefore;
    }

    [Serializable]
    public sealed class ImportRecord
    {
        public string id, productKey, revisionId, state, startedUtc, endedUtc, message;
        public int packageCount;
        public List<ImportedAsset> assets = new List<ImportedAsset>();
    }

    [Serializable]
    public sealed class ProjectLedger
    {
        public int schemaVersion = 1;
        public List<ProductReference> products = new List<ProductReference>();
        public List<AssetLink> links = new List<AssetLink>();
        public List<ArtifactRevision> revisions = new List<ArtifactRevision>();
        public List<ImportRecord> imports = new List<ImportRecord>();
        public List<DisplayFolderRule> displayFolders = new List<DisplayFolderRule>();
        public List<ProductPrefabSelection> prefabSelections = new List<ProductPrefabSelection>();
    }

    [Serializable]
    public sealed class ProductPrefabSelection
    {
        public string productKey;
        public List<string> guids = new List<string>();
    }

    [Serializable]
    public sealed class DisplayFolderRule
    {
        public string id, productKey, folderGuid, lastPath;
        public LinkRole role;
    }

    [Serializable]
    public sealed class PackageAsset
    {
        public string guid, path, contentHash;
        public bool existedBefore, folder;
    }

    [Serializable]
    public sealed class PendingImport
    {
        public int schemaVersion = 1;
        public ProductReference product;
        public ArtifactRevision revision;
        public string id, packageName, startedUtc, phase = "Waiting", message;
        public int processId;
        public bool sawStart, receivedItems, completed;
        public List<PackageAsset> entries = new List<PackageAsset>();
        public List<string> selectedPaths = new List<string>();
    }
}
