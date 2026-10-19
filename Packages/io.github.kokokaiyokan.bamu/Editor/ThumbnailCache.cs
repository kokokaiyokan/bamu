using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace bamu
{
    internal static class ThumbnailCache
    {
        private static readonly Dictionary<string, Texture2D> Images = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> Tried = new HashSet<string>();
        private static readonly HashSet<string> DiskChecked = new HashSet<string>();
        private static readonly ThumbnailDiskCache Disk = new ThumbnailDiskCache(Path.Combine(BridgeStorage.SharedHome, "thumbnails"), Path.Combine(BridgeStorage.LegacySharedHome, "thumbnails"));
        private static readonly Dictionary<string, UnityWebRequest> Requests = new Dictionary<string, UnityWebRequest>();
        public static bool NetworkEnabled
        {
            get => BamuPreferences.GetBool("bamu.RemoteThumbnails", true);
            set { EditorPrefs.SetBool("bamu.RemoteThumbnails", value); if (!value) Abort(); }
        }

        public static void RetryFailed()
        {
            // Keep decoded images; retry only missing/failed thumbnails on the next repaint.
            Tried.Clear();
            Tried.UnionWith(Requests.Keys);
            DiskChecked.Clear();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        public static Texture2D Get(Product product)
        {
            var key = product.localThumbnail ?? "";
            if (!string.IsNullOrEmpty(key) && File.Exists(key))
            {
                if (Images.Count < 200 && !Images.ContainsKey(key) && Tried.Add(key))
                {
                    try
                    {
                        if (new FileInfo(key).Length < 8 * 1024 * 1024)
                        {
                            var image = new Texture2D(2, 2);
                            if (image.LoadImage(File.ReadAllBytes(key))) Images[key] = image;
                            else UnityEngine.Object.DestroyImmediate(image);
                        }
                    }
                    catch (IOException) { /* A thumbnail must not prevent library access. */ }
                }
                if (Images.TryGetValue(key, out var local)) return local;
            }
            key = product.thumbnailUrl ?? "";
            if (Images.TryGetValue(key, out var cached)) return cached;
            if (!Uri.TryCreate(key, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "booth.pximg.net") return null;
            if (Images.Count >= 200) return null;
            // Read before checking network settings: saved images also work offline.
            if (DiskChecked.Add(key))
            {
                var bytes = Disk.Read(key);
                if (bytes != null)
                {
                    var image = new Texture2D(2, 2);
                    if (image.LoadImage(bytes)) { Images[key] = image; return image; }
                    UnityEngine.Object.DestroyImmediate(image);
                    Disk.Remove(key); // A damaged file should be downloaded again.
                }
            }
            if (!NetworkEnabled || Requests.Count >= 2 || Tried.Contains(key)) return null;
            Tried.Add(key);
            var request = UnityWebRequestTexture.GetTexture(key, true);
            request.timeout = 10;
            request.redirectLimit = 0;
            Requests[key] = request;
            request.SendWebRequest();
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload -= Abort;
            AssemblyReloadEvents.beforeAssemblyReload += Abort;
            return null;
        }
        private static void Poll()
        {
            foreach (var pair in new List<KeyValuePair<string, UnityWebRequest>>(Requests))
            {
                var request = pair.Value;
                if (!request.isDone) continue;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var texture = DownloadHandlerTexture.GetContent(request);
                    if (texture != null)
                    {
                        Images[pair.Key] = texture;
                        Disk.Store(pair.Key, request.downloadHandler.data);
                    }
                }
                request.Dispose(); Requests.Remove(pair.Key);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
            if (Requests.Count == 0) EditorApplication.update -= Poll;
        }
        private static void Abort()
        {
            foreach (var pair in Requests)
            {
                pair.Value.Abort(); pair.Value.Dispose();
                Tried.Remove(pair.Key); // Re-enabling images should retry aborted requests.
            }
            Requests.Clear();
            EditorApplication.update -= Poll;
        }
    }
}
