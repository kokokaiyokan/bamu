using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "BoothUnityBridge", "BoothUnityBridge.Editor", "ExistingAssetScanWindow")]
    public sealed class ExistingAssetScanWindow : EditorWindow
    {
        private List<Product> targets;
        private ExistingAssetScanReport report;
        private List<ScanReportProductView> productViews;
        private const int PageSize = 10;
        private int page;
        private string error = "", filter = "";
        private bool findCopies;
        private List<DisplayFolderRule> displayRules = new List<DisplayFolderRule>();
        private Vector2 scroll;
        private readonly HashSet<string> expanded = new HashSet<string>();

        public static void Open(IEnumerable<Product> products)
        {
            var window = GetWindow<ExistingAssetScanWindow>("既存アセットの照合");
            window.targets = products.ToList(); window.report = null; window.productViews = null; window.page = 0; window.error = "";
            window.expanded.Clear(); window.Show();
        }
        private void OnEnable() { minSize = new Vector2(700, 480); }
        private void OnGUI()
        {
            if (targets == null)
            {
                EditorGUILayout.HelpBox("Managerの「既存アセットを探す」から対象商品を選んで開いてください。", MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField("過去に導入したアセットを探す", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("対象: " + targets.Count + "商品 · BLMの配布ファイルと、このプロジェクトを照合します。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.HelpBox("走査ではアセットやリンクを変更しません。候補を確認し、選択したものをリンクしてください。正確な導入版は確定しません。", MessageType.Info);
            if (targets.All(p => string.IsNullOrEmpty(p.directory)))
                EditorGUILayout.HelpBox("BLM保存ファイルがないため、この商品は自動照合できません。Managerの「アセットを手動でリンク」からProject内のフォルダやファイルを関連付けられます。", MessageType.Info);
            findCopies = EditorGUILayout.ToggleLeft("GUIDが変わったコピーも内容で探す（プロジェクト全体の比較に時間がかかります）", findCopies);
            using (new EditorGUI.DisabledScope(ImportCoordinator.HasPending || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button(report == null ? "走査を開始" : "再走査")) StartScan();
            if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Warning);
            if (report == null || productViews == null) return;
            if (report.cancelled) EditorGUILayout.HelpBox("走査を中断しました。ここまでの候補を表示しています。共有関係の調査も途中のため、候補は未選択になっています。", MessageType.Warning);
            var count = report.candidates.Count(c => c.selected && !c.alreadyLinked);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(count == 0 || ImportCoordinator.HasPending))
                    if (GUILayout.Button("選択した候補をリンク（" + count + "項目）")) Apply();
                if (GUILayout.Button("選択をすべて解除", GUILayout.Width(140))) foreach (var c in report.candidates) c.selected = false;
            }
            var nextFilter = EditorGUILayout.TextField("商品・フォルダ検索", filter);
            if (nextFilter != filter) { filter = nextFilter; page = 0; scroll = Vector2.zero; }
            var visible = productViews.Where(v => MatchesFilter(v.result.product.name) || v.groups.Any(g => MatchesFilter(v.result.product.name + " " + g.Key))).ToList();
            var pages = Math.Max(1, (visible.Count + PageSize - 1) / PageSize);
            page = Mathf.Clamp(page, 0, pages - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(visible.Count + "商品", GUILayout.Width(90));
                using (new EditorGUI.DisabledScope(page == 0))
                    if (GUILayout.Button("前", GUILayout.Width(50))) { page--; scroll = Vector2.zero; }
                GUILayout.Label((page + 1) + " / " + pages);
                using (new EditorGUI.DisabledScope(page >= pages - 1))
                    if (GUILayout.Button("次", GUILayout.Width(50))) { page++; scroll = Vector2.zero; }
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("検出 " + report.candidates.Count + "項目 · 索引キャッシュ利用 " + report.cacheHits + "件", EditorStyles.miniLabel);
            foreach (var view in visible.Skip(page * PageSize).Take(PageSize))
            {
                var result = view.result;
                var groups = view.groups.Where(g => MatchesFilter(result.product.name + " " + g.Key));
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(result.product.name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(result.packageCount + "パッケージ · 配布物内 " + result.sourceGuids.Count + "種類 · 検出元 " +
                        result.foundGuids.Count + "種類 · 未検出 " + view.missing + "種類", EditorStyles.wordWrappedMiniLabel);
                    if (view.candidateCount == 0) EditorGUILayout.LabelField(result.packageCount == 0 ? "保存済みの対応パッケージがありません。" : "候補が見つかりませんでした。");
                    foreach (var group in groups) DrawGroup(result.product, group.Key, group.ToList());
                }
            }
            if (report.errors.Count > 0)
            {
                EditorGUILayout.LabelField("読み取れなかったファイル・商品", EditorStyles.boldLabel);
                foreach (var message in report.errors) EditorGUILayout.LabelField(message, EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.EndScrollView();
        }
        private bool MatchesFilter(string value) => filter.Length == 0 || (value ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawGroup(Product product, string folder, List<ScanCandidate> items)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var available = items.Where(c => !c.alreadyLinked).ToList();
                var selected = available.Count(c => c.selected);
                using (new EditorGUI.DisabledScope(available.Count == 0))
                {
                    EditorGUI.showMixedValue = selected > 0 && selected < available.Count;
                    EditorGUI.BeginChangeCheck();
                    var value = EditorGUILayout.ToggleLeft(folder, available.Count > 0 && selected == available.Count);
                    if (EditorGUI.EndChangeCheck()) foreach (var item in available) item.selected = value;
                    EditorGUI.showMixedValue = false;
                }
                var shared = items.Count(c => c.sharedProducts.Count > 0);
                EditorGUILayout.LabelField(items.Count(c => c.kind == ScanMatchKind.Guid) + " GUID一致 · " + items.Count(c => c.kind == ScanMatchKind.Content) +
                    " 内容のみ一致 · " + items.Count(c => c.kind == ScanMatchKind.Path) + " パスのみ一致 · " +
                    items.Count(c => c.alreadyLinked) + " リンク済み", EditorStyles.wordWrappedMiniLabel);
                if (shared > 0) EditorGUILayout.HelpBox("既知の共有候補 " + shared + "項目。複数商品に同じGUIDが含まれるため、初期選択から除外しています。", MessageType.Warning);
                if (items.Any(c => c.kind != ScanMatchKind.Guid))
                    EditorGUILayout.LabelField("内容・パスのみの候補は未選択です。照合詳細を確認して選択してください。", EditorStyles.wordWrappedMiniLabel);
                var key = product.key + "\n" + folder;
                var open = EditorGUILayout.Foldout(expanded.Contains(key), "照合詳細（" + items.Count + "項目）", true);
                if (open) expanded.Add(key); else expanded.Remove(key);
                if (!open) return;
                foreach (var item in items)
                {
                    using (new EditorGUI.DisabledScope(item.alreadyLinked))
                        item.selected = EditorGUILayout.ToggleLeft(item.path + (item.alreadyLinked ? "（リンク済み）" : ""), item.selected);
                    EditorGUILayout.LabelField(KindLabel(item.kind) + " · " + item.ContentLabel, EditorStyles.miniLabel);
                    if (item.sharedProducts.Count > 0)
                        EditorGUILayout.LabelField("共有先: " + string.Join(", ", item.sharedProducts.Select(k =>
                            targets.FirstOrDefault(p => p.key == k)?.name ?? k)), EditorStyles.wordWrappedMiniLabel);
                    foreach (var evidence in item.evidence)
                    {
                        EditorGUILayout.LabelField("配布元: " + evidence.packageName + " · " + (evidence.contentEqual ? "内容一致" : "内容相違"), EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField("元の場所: " + evidence.sourcePath, EditorStyles.wordWrappedMiniLabel);
                    }
                }
            }
        }
        private static string KindLabel(ScanMatchKind kind) => kind == ScanMatchKind.Guid ? "GUID一致" : kind == ScanMatchKind.Content ? "GUID相違・内容一致" : "パスのみ一致（弱い候補）";
        private void StartScan()
        {
            try
            {
                error = ""; report = null; productViews = null;
                var next = EditorApplication.timeSinceStartup;
                var ledger = BridgeStorage.Load();
                displayRules = ledger.displayFolders;
                report = ExistingAssetScanner.Scan(targets, ledger, findCopies, (message, fraction) =>
                {
                    if (EditorApplication.timeSinceStartup < next) return;
                    next = EditorApplication.timeSinceStartup + .1;
                    if (EditorUtility.DisplayCancelableProgressBar("既存アセットの照合", message, fraction)) throw new OperationCanceledException();
                });
                productViews = ScanReportView.Build(report, displayRules, AssetDatabase.GUIDToAssetPath);
                page = 0; scroll = Vector2.zero;
                expanded.Clear();
            }
            catch (Exception ex) { error = ex.Message; }
            finally { EditorUtility.ClearProgressBar(); }
        }
        private void Apply()
        {
            try
            {
                var added = ExistingAssetScanner.Apply(report);
                error = "";
                BridgeWindow.RefreshOpenWindows();
                ShowNotification(new GUIContent(added + "項目をリンクしました"));
            }
            catch (Exception ex) { error = ex.Message; }
        }
    }
}
