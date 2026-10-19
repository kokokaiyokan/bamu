using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    // Embedded in BridgeWindow. No independent update-management window.
    internal sealed class BoothSyncPanel
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly Action repaint;
        private string error = "", notice = "";
        private bool busy, setup, filesOpen = true;
        private readonly Dictionary<string, SyncWatch> edits = new Dictionary<string, SyncWatch>();
        private readonly Dictionary<string, string> savedEdits = new Dictionary<string, string>();
        private SyncStatus Status => BoothSyncStatusCache.Latest;
        public BoothSyncPanel(Action repaint)
        {
            this.repaint = repaint;

        }
        private SyncConnection Connection()
        {
            UnityBridgeReceiver.EnsureStarted();
            var path = Path.Combine(UnityBridgeReceiver.Home, "connection.json");
            if (!File.Exists(path)) throw new IOException("Unityの受信を準備しています。数秒待って再確認してください。");
            var config = JsonUtility.FromJson<SyncConnection>(File.ReadAllText(path));
            if (config == null || config.port != 48763 || string.IsNullOrEmpty(config.token)) throw new IOException("接続情報を確認してください。");
            return config;
        }
        private async Task<string> Request(string path, object body = null)
        {
            var config = Connection();
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, "http://127.0.0.1:48763" + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.token);
                if (body != null) request.Content = new StringContent(JsonUtility.ToJson(body), Encoding.UTF8, "application/json");
                using (var response = await Client.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode) throw new IOException("Unityの受信へ接続できません（HTTP " + (int)response.StatusCode + "）。");
                    return await response.Content.ReadAsStringAsync();
                }
            }
        }
        public async void Refresh()
        {
            if (busy) return;
            busy = true;
            try { BoothSyncStatusCache.Accept(JsonUtility.FromJson<SyncStatus>(await Request("/v1/status"))); error = ""; }
            catch (Exception ex) { error = ex.Message; }
            finally { busy = false; repaint(); }
        }
        private async void Post(string path, object body)
        {
            if (busy) return;
            busy = true;
            var success = false;
            try
            {
                var result = await Request(path, body); error = ""; success = true;
                if (path == "/v1/bulk-missing")
                {
                    var queued = JsonUtility.FromJson<SyncBulkResult>(result);
                    notice = queued.queued_products + "商品・" + queued.queued_files + "ファイルを取得待ちに追加しました。";
                }
                if (body is SyncWatch watch)
                {
                    var json = JsonUtility.ToJson(watch);
                    savedEdits[watch.item_id] = json;
                    edits[watch.item_id] = JsonUtility.FromJson<SyncWatch>(json);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            finally { busy = false; if (success) Refresh(); else repaint(); }
        }
        public void Check() => Post("/v1/check", new SyncIntervals());
        public void DrawNotice()
        {
            if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Warning);
            if (notice.Length > 0) EditorGUILayout.LabelField(notice, EditorStyles.wordWrappedMiniLabel);
            if (Status != null) EditorGUILayout.LabelField("BOOTH一覧: " + Status.products.Count(p => p.remote_available) +
                "商品 · 商品画像・分類は順次反映", EditorStyles.miniLabel);
        }
        public void AddMenuItems(GenericMenu menu)
        {
            var check = new GUIContent("BOOTH/BOOTHを確認");
            if (busy) menu.AddDisabledItem(check);
            else menu.AddItem(check, false, Check);
            var bulk = new GUIContent("BOOTH/BLM未登録を一括ダウンロード");
            if (busy || Status == null) menu.AddDisabledItem(bulk);
            else menu.AddItem(bulk, false, () =>
            {
                if (!busy && Status != null)
                    Post("/v1/bulk-missing", new SyncIntervals());
            });
        }
        private string ToolPath(string relative)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeWindow).Assembly);
            return Path.Combine(package.resolvedPath, "Tools", relative);
        }
        public void DrawSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Chrome（BOOTH）接続設定", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Unityを開くと受信を自動開始します。Chrome拡張の「今すぐBOOTHを確認」を押すと、約5秒ごとに一覧へ反映します。Unityをすべて閉じている間は同期・保存確認を休止します。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(UnityBridgeReceiver.StatusText, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(busy))
                if (GUILayout.Button("Chromeの更新内容を再読み込み")) Refresh();
            if (Status != null) EditorGUILayout.LabelField(Status.browser_status ?? "", EditorStyles.wordWrappedMiniLabel);
            var network = EditorGUILayout.ToggleLeft("商品画像をBOOTHから取得する", ThumbnailCache.NetworkEnabled);
            if (network != ThumbnailCache.NetworkEnabled) ThumbnailCache.NetworkEnabled = network;
            using (new EditorGUI.DisabledScope(!ThumbnailCache.NetworkEnabled))
                if (GUILayout.Button("取得できなかった商品画像を再取得")) ThumbnailCache.RetryFailed();
            setup = EditorGUILayout.Foldout(setup, "初期設定（Chrome拡張の導入～最初の更新）", true);
            if (!setup) return;
            EditorGUILayout.LabelField("1. Unityの受信を確認", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("複数プロジェクトを開いている場合は、1つだけが受信役になります。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("連携データ保存先", UnityBridgeReceiver.Home, EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("受信を再確認")) { UnityBridgeReceiver.EnsureStarted(); Refresh(); }
            EditorGUILayout.LabelField("2. Chromeへ拡張を導入", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Chromeで chrome://extensions を開き、デベロッパーモードを有効にします。「パッケージ化されていない拡張機能を読み込む」で次のフォルダを指定してください。導入済みの場合は再読み込みします。", EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("Chrome拡張のフォルダを開く")) EditorUtility.RevealInFinder(ToolPath("browser-extension"));
            EditorGUILayout.LabelField("3. 接続キーを登録", EditorStyles.boldLabel);
            if (GUILayout.Button("接続キーをコピー"))
                try { GUIUtility.systemCopyBuffer = Connection().token; }
                catch (Exception ex) { error = ex.Message; }
            using (new EditorGUI.DisabledScope(busy))
                if (GUILayout.Button("接続キーを再発行") && EditorUtility.DisplayDialog("接続キーの再発行",
                    "現在の接続キーを無効にし、新しいキーをコピーします。接続しているすべてのChrome拡張でキーの再登録が必要です。", "再発行", "キャンセル"))
                {
                    try
                    {
                        GUIUtility.systemCopyBuffer = UnityBridgeReceiver.RegenerateConnectionKey();
                        error = "";
                        notice = "接続キーを再発行してコピーしました。Chrome拡張へ貼り付け、「保存して接続」を押してください。";
                    }
                    catch (Exception ex) { error = ex.Message; }
                }
            EditorGUILayout.LabelField("Chrome拡張の設定にキーを貼り付け、「保存して接続」を押します。同じChromeでBOOTHへログインし、BLMも起動してください。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("4. 最初の一覧を取得", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Chrome拡張で「今すぐBOOTHを確認」を押します。Unityから確認を要求する場合は下のボタンを使用できます。商品画像・分類は順次反映します。", EditorStyles.wordWrappedLabel);
            using (new EditorGUI.DisabledScope(busy))
                if (GUILayout.Button("BOOTH一覧の最初の更新を要求")) Check();
            if (Status != null)
            {
                var interval = EditorGUILayout.IntField("一覧の定期確認（時間）", Status.interval_hours);
                if (interval != Status.interval_hours && !busy) Post("/v1/settings", new SyncSettings { interval_hours = interval });
            }
        }
        private SyncWatch Edit(string item)
        {
            var saved = Status?.watches?.FirstOrDefault(w => w.item_id == item);
            if (!edits.TryGetValue(item, out var edit))
            {
                edit = saved == null ? new SyncWatch { item_id = item } : JsonUtility.FromJson<SyncWatch>(JsonUtility.ToJson(saved));
                edits[item] = edit;
                savedEdits[item] = JsonUtility.ToJson(edit);
            }
            else if (saved != null && savedEdits.TryGetValue(item, out var baseline) && JsonUtility.ToJson(edit) == baseline)
            {
                var json = JsonUtility.ToJson(saved);
                if (json != baseline) { edits[item] = edit = JsonUtility.FromJson<SyncWatch>(json); savedEdits[item] = json; }
            }
            return edit;
        }
        public void DrawProduct(Product product)
        {
            if (string.IsNullOrEmpty(product.boothId)) return;
            var remote = UnifiedCatalog.Remote(product, Status);
            if (remote == null) { EditorGUILayout.LabelField("BOOTHの一覧・分類は接続設定から更新できます。", EditorStyles.wordWrappedMiniLabel); return; }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(UnifiedCatalog.State(product, Status), EditorStyles.boldLabel);
                var classification = remote.vrchat_state == "yes" ? "VRChat" : remote.vrchat_state == "no" ? "VRChat以外" : "確認待ち・不明";
                EditorGUILayout.LabelField("自動判定: " + classification + (string.IsNullOrEmpty(remote.category) ? "" : " · " + remote.category), EditorStyles.wordWrappedMiniLabel);
                var edit = Edit(product.boothId);
                if (edit.hidden)
                {
                    EditorGUILayout.HelpBox("この商品は一覧・今後の取得対象から除外されています。", MessageType.Info);
                    using (new EditorGUI.DisabledScope(busy))
                        if (GUILayout.Button("一覧・取得対象に戻す"))
                        { var restored = JsonUtility.FromJson<SyncWatch>(JsonUtility.ToJson(edit)); restored.hidden = false; Post("/v1/watch", restored); }
                    return;
                }
                var mode = edit.target_mode == "include" ? 1 : edit.target_mode == "exclude" ? 2 : 0;
                mode = EditorGUILayout.Popup("VRChat取得対象", mode, new[] { "BOOTHのタグ・バッジから判定", "対象にする", "対象外にする" });
                edit.target_mode = new[] { "auto", "include", "exclude" }[mode];
                edit.enabled = EditorGUILayout.ToggleLeft("この商品を一括取得の対象にする", edit.enabled);
                var selection = EditorGUILayout.Popup("取得ファイル", edit.selection_mode == "manual" ? 1 : 0, new[] { "アセット候補から選ぶ", "ファイルを指定する" });
                if (selection == 1 && edit.selection_mode != "manual") edit.file_keys = (remote.recommended_file_keys ?? new List<string>()).ToList();
                edit.selection_mode = selection == 1 ? "manual" : "auto";
                var files = Status.files.Where(f => f.item_id == product.boothId && f.available).ToList();
                filesOpen = EditorGUILayout.Foldout(filesOpen, "BOOTHの配布ファイル（" + files.Count + "件）", true);
                if (filesOpen)
                {
                    foreach (var file in files)
                    {
                        var selected = edit.selection_mode == "manual" ? edit.file_keys.Contains(file.key) : remote.recommended_file_keys?.Contains(file.key) == true;
                        using (new EditorGUI.DisabledScope(edit.selection_mode != "manual"))
                        {
                            var value = EditorGUILayout.ToggleLeft(file.name + "（" + file.variation_id + "）", selected);
                            if (value && !selected) edit.file_keys.Add(file.key);
                            else if (!value && selected) edit.file_keys.Remove(file.key);
                        }
                        if (!string.IsNullOrEmpty(file.saved_at)) EditorGUILayout.LabelField("BLM保存物の確認: " + file.saved_at, EditorStyles.miniLabel);
                    }
                }
                using (new EditorGUI.DisabledScope(busy))
                    if (GUILayout.Button("この商品の取得設定を保存")) Post("/v1/watch", edit);
                EditorGUILayout.LabelField("設定の保存だけでは取得しません。上部の一括ダウンロードボタンで未登録商品を取得します。", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(busy))
                    if (GUILayout.Button("一覧・今後の取得対象から除外する"))
                    {
                        var excluded = JsonUtility.FromJson<SyncWatch>(JsonUtility.ToJson(edit));
                        excluded.hidden = true;
                        Post("/v1/watch", excluded);
                    }
                EditorGUILayout.LabelField("商品単位で除外します。「設定」→「BOOTH」→「除外した商品も表示」から元に戻せます。BLM保存物やAssetリンクは保持します。", EditorStyles.wordWrappedMiniLabel);
                foreach (var job in Status.jobs.Where(j => j.key.StartsWith(product.boothId + ":", StringComparison.Ordinal)).Reverse().Take(4))
                {
                    EditorGUILayout.LabelField(job.state + " · " + job.message, EditorStyles.wordWrappedMiniLabel);
                    if (job.state == "NeedsAttention")
                    {
                        using (new EditorGUI.DisabledScope(busy))
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("BLMで確認済み：この取得を再試行")) Post("/v1/retry", new SyncRetry { job_id = job.id });
                            if (GUILayout.Button("この商品をスキップして次へ"))
                            { Post("/v1/dismiss", new SyncRetry { job_id = job.id }); }
                        }
                    }
                }
            }
        }
    }
}
