using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using static bamu.BridgeJson;

namespace bamu
{
    [InitializeOnLoad]
    internal static class UnityBridgeReceiver
    {
        private static Task<Session> starting;
        private static Session current;
        private static double next;
        private static bool disposed;
        private static bool restartRequested;
        private static CancellationTokenSource lifetime = new CancellationTokenSource();
        public static string StatusText { get; private set; } = "Unity内の受信を準備しています。";
        public static string Home => BridgeStorage.ReceiverHome;
        static UnityBridgeReceiver()
        {
            EditorApplication.update += EnsureStarted;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }
        public static void EnsureStarted()
        {
            if (disposed || EditorApplication.isCompiling) return;
            if (starting != null)
            {
                if (!starting.IsCompleted) return;
                try { current = starting.GetAwaiter().GetResult(); StatusText = current == null ? "別のUnityプロジェクトで受信中です。" : "Unity内でChromeからの情報を受信しています。"; }
                catch { StatusText = "受信を開始できません。BLMの設定・保存先・ポート48763を確認してください。"; }
                starting = null; next = EditorApplication.timeSinceStartup + 5;
                if (restartRequested) { current?.Dispose(); current = null; next = 0; restartRequested = false; }
            }
            if (current != null || EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 5;
            var home = Home; var legacyHome = BridgeStorage.LegacyReceiverHome; var database = BridgeStorage.DatabasePath; var root = BridgeStorage.LibraryOverride;
            starting = Task.Run(() => Open(home, legacyHome, database, root, lifetime.Token));
        }
        public static void Restart()
        {
            if (starting != null) { restartRequested = true; return; }
            current?.Dispose(); current = null; next = 0;
            if (starting == null) EnsureStarted();
        }
        public static string RegenerateConnectionKey() => ReceiverCredentials.Regenerate(Home);
        private static Session Open(string home, string legacyHome, string database, string root, CancellationToken cancellation)
        {
            var token = ReceiverCredentials.LoadOrCreate(home, legacyHome, cancellation);
            // Existing Python process is shut down through its authenticated API,
            // never by killing a process. In-flight native BLM jobs stay in state.
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                try
                {
                    var json = client.GetStringAsync("http://127.0.0.1:48763/v1/status").GetAwaiter().GetResult();
                    var status = Map(Parse(json));
                    if (Str(status, "runtime") == "unity") return null;
                    if (Str(status, "transport") == "blm" && Str(status, "version").Length > 0)
                        using (var response = client.PostAsync("http://127.0.0.1:48763/v1/shutdown", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult())
                            response.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException) { /* No legacy companion is running. */ }
                catch (TaskCanceledException) { }
            }
            IDisposable ownership = null;
            for (var i = 0; i < 30; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                try { ownership = BamuMigration.AcquireReceiverLeases(home, legacyHome); break; }
                catch (IOException) { ownership?.Dispose(); ownership = null; Thread.Sleep(100); }
            }
            if (ownership == null) return null;
            var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            UnityBridgeHost host = null;
            try
            {
                stopped.Token.ThrowIfCancellationRequested();
                BamuMigration.ReceiverData(legacyHome, home, stopped.Token);
                var core = new UnityBridgeCore(home, () => BlmLibrary.Load(database, root), stopped.Token);
                // Every request reads the current key, including changes made from another project.
                host = new UnityBridgeHost(core, () => ReceiverCredentials.ReadToken(home), stopped);
                stopped.Token.ThrowIfCancellationRequested();
                return new Session(host, ownership, stopped);
            }
            catch { host?.Dispose(); stopped.Cancel(); stopped.Dispose(); ownership.Dispose(); throw; }
        }
        private static void Stop()
        {
            disposed = true; lifetime.Cancel();
            current?.Dispose(); current = null;
            if (starting != null)
            {
                try { if (starting.Wait(5000)) starting.GetAwaiter().GetResult()?.Dispose(); }
                catch { }
            }
        }
        private sealed class Session : IDisposable
        {
            private readonly UnityBridgeHost host; private readonly IDisposable ownership; private readonly CancellationTokenSource stopped;
            private int closed;
            public Session(UnityBridgeHost host, IDisposable ownership, CancellationTokenSource stopped) { this.host = host; this.ownership = ownership; this.stopped = stopped; }
            public void Dispose() { if (Interlocked.Exchange(ref closed, 1) != 0) return; host.Dispose(); ownership.Dispose(); stopped.Dispose(); }
        }
    }
}
