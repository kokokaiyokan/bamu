using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    [InitializeOnLoad]
    public static class BoothSyncStatusCache
    {
        public static SyncStatus Latest { get; private set; }
        public static event Action Changed;
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private static bool busy;
        private static double next;
        private static string lastJson;
        static BoothSyncStatusCache() { EditorApplication.update += Poll; }
        public static void Accept(SyncStatus status) { Latest = status; Changed?.Invoke(); }
        private static async void Poll()
        {
            if (busy || EditorApplication.timeSinceStartup < next || EditorApplication.isCompiling) return;
            next = EditorApplication.timeSinceStartup + 5;
            UnityBridgeReceiver.EnsureStarted();
            var directory = UnityBridgeReceiver.Home;
            var path = Path.Combine(directory, "connection.json");
            if (!File.Exists(path)) return;
            busy = true;
            try
            {
                var config = JsonUtility.FromJson<SyncConnection>(File.ReadAllText(path));
                if (config.port != 48763 || string.IsNullOrEmpty(config.token)) return;
                using (var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:48763/v1/status"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.token);
                    using (var response = await Client.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode) return;
                        var json = await response.Content.ReadAsStringAsync();
                        if (json == lastJson) return;
                        Latest = JsonUtility.FromJson<SyncStatus>(json); lastJson = json; Changed?.Invoke();
                    }
                }
            }
            catch { /* The receiving Unity instance may be reloading. No credentials or network errors in project logs. */ }
            finally { busy = false; }
        }

        public static bool HasNewSavedVersion(string productKey, ProjectLedger ledger, SyncStatus status) =>
            BoothSyncVersions.HasNewSavedVersion(productKey, ledger, status);
    }
}
