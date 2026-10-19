using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace bamu
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "BoothUnityBridge", "BoothUnityBridge.Editor", "BridgeWindow")]
    public sealed partial class BridgeWindow : EditorWindow
    {
        private BlmLibrary library;
        private BoothSyncPanel syncPanel;
        private ProjectLedger ledger;
        private LinkViewCache linkView;
        private LinkViewCache Links => linkView ?? (linkView = new LinkViewCache(ledger, BridgeStorage.CurrentPath, AssetDatabase.GUIDToAssetPath));
        private void ProjectChanged() { InvalidateViews(); Repaint(); }
        private void InvalidateViews() { linkView = null; prefabCandidates.Clear(); }
        private string error = "", search = "", selectedKey = "", versionLabel = "";
        private bool packageOpen, manualOpen, extraLinksOpen, acquisitionOpen, missingImportOpen;
        [SerializeField] private int detailTab;
        private int sortMode;
        private bool sortDescending;
        private int pageSize = 50;
        private int page;
        private int catalogFilter;
        private List<Product> Catalog => UnifiedCatalog.Build(library?.products, ledger?.products, BoothSyncStatusCache.Latest);
        private LinkRole role;
        private Vector2 productScroll, detailScroll;
        private List<string> packages = new List<string>();
        private ProductTags tags;
        private TagDictionary tagDictionary;
        private string tagInput = "", tagError = "";
        private float tagCandidateWidth;
        private static readonly string[] RoleNames = { "導入元", "改変用コピー", "参考の関連付け" };

        [MenuItem("Window/BOOTH Asset Management Utility")]
        public static void Open() { GetWindow<BridgeWindow>("BOOTH Asset Management Utility").Show(); }

        public static void RefreshOpenWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<BridgeWindow>()) window.ReloadLedger();
        }

        internal static void ReloadOpenWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<BridgeWindow>()) window.Reload();
        }

        internal static void RefreshTagWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<BridgeWindow>())
            { window.RefreshTags(); window.Repaint(); }
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("BOOTH Asset Management Utility");
            minSize = new Vector2(820, 480);
            ProductListOrder.FromLegacy(Mathf.Clamp(BamuPreferences.GetInt("bamu.ProductSort", 0), 0, 6), out sortMode, out sortDescending);
            sortMode = Mathf.Clamp(BamuPreferences.GetInt("bamu.ProductSortCategory", sortMode), 0, 3);
            sortDescending = BamuPreferences.GetInt("bamu.ProductSortDescending", sortDescending ? 1 : 0) != 0;
            pageSize = ProductListOrder.PageSize(BamuPreferences.GetInt("bamu.ProductPageSize", 50));
            syncPanel = new BoothSyncPanel(Repaint);
            PrefabConverterRegistry.Changed += Repaint;
            Selection.selectionChanged += Repaint;
            EditorApplication.projectChanged += ProjectChanged;
            ImportCoordinator.Changed += ReloadLedger;
            BoothSyncStatusCache.Changed += Reload;
            Reload();
        }
        private void OnFocus() { RefreshTags(); Repaint(); }
        private void RefreshTags()
        {
            try
            {
                if (tags == null) tags = new ProductTags(BridgeStorage.TagsPath);
                if (tagDictionary == null) tagDictionary = new TagDictionary(BridgeStorage.TagDictionaryPath);
                else tagDictionary.Reload();
                tags.SyncAutomatic(BoothSyncStatusCache.Latest, tagDictionary);
                tagError = "";
            }
            catch (Exception ex) { tagError = ex.Message; }
        }
        private void OnDisable()
        {
            PrefabConverterRegistry.Changed -= Repaint;
            Selection.selectionChanged -= Repaint;
            EditorApplication.projectChanged -= ProjectChanged;
            ImportCoordinator.Changed -= ReloadLedger;
            BoothSyncStatusCache.Changed -= Reload;
        }
        private void Run(Action action)
        {
            InvalidateViews();
            try { action(); error = ""; }
            catch (OperationCanceledException) { error = "操作をキャンセルしました。"; }
            catch (Exception ex) { error = ex.Message; }
            Repaint();
        }
        private void ReloadLedger()
        {
            InvalidateViews();
            try { ledger = BridgeStorage.Load(); error = ""; }
            catch (Exception ex) { ledger = null; error = ex.Message; }
            Repaint();
        }
        private void ReloadBlmLibrary()
        {
            try
            {
                library = BlmLibrary.Load(BridgeStorage.DatabasePath, BridgeStorage.LibraryOverride);
                RefreshPackages();
                error = "";
            }
            catch (Exception ex) { error = ex.Message; }
            Repaint();
        }
        private void Reload()
        {
            RefreshTags();
            library = null;
            Run(() => { ledger = BridgeStorage.Load(); library = BlmLibrary.Load(BridgeStorage.DatabasePath, BridgeStorage.LibraryOverride); RefreshPackages(); });
        }
        private void RefreshPackages()
        {
            var product = Catalog.FirstOrDefault(p => p.key == selectedKey);
            packages = product == null ? new List<string>() : BlmLibrary.FindPackages(product);
        }
        private void SelectProduct(string key)
        {
            tagInput = "";
            selectedKey = key; versionLabel = ""; detailScroll = Vector2.zero; detailTab = 0;
            missingImportOpen = false;
            Run(RefreshPackages);
        }
        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var settingsLabel = new GUIContent("設定");
                var settingsRect = GUILayoutUtility.GetRect(settingsLabel, EditorStyles.toolbarDropDown, GUILayout.Width(70));
                if (EditorGUI.DropdownButton(settingsRect, settingsLabel, FocusType.Passive, EditorStyles.toolbarDropDown))
                    ShowSettingsMenu(settingsRect);
                if (GUILayout.Button(new GUIContent("逆引き検索", "Projectで選択中のファイル・フォルダに関連した商品を表示"), EditorStyles.toolbarButton, GUILayout.Width(90)))
                { detailTab = 1; detailScroll = Vector2.zero; }
                GUILayout.FlexibleSpace();
                GUILayout.Label("BAMU · 非公式 / " + UnityBridgeCore.Version, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            }
            syncPanel.DrawNotice();
            if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Warning);
            DrawPending();
            if (ledger == null) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(Mathf.Max(300, position.width * .38f)))) DrawProducts();
                using (new EditorGUILayout.VerticalScope())
                {
                    detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
                    if (detailTab == 1) DrawSelection(); else DrawDetail();
                    EditorGUILayout.EndScrollView();
                }
            }
        }
        private void ShowSettingsMenu(Rect anchor)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("接続設定…"), false, ConnectionSettingsWindow.Open);
            menu.AddItem(new GUIContent("辞書設定…"), false, TagDictionaryWindow.Open);
            menu.AddSeparator("");
            syncPanel.AddMenuItems(menu);
            var scanLabel = new GUIContent("アセット/既存アセットを探す（全商品）");
            if (library == null || ImportCoordinator.HasPending || EditorApplication.isCompiling || EditorApplication.isUpdating)
                menu.AddDisabledItem(scanLabel);
            else
                menu.AddItem(scanLabel, false, () =>
                {
                    if (library != null && !ImportCoordinator.HasPending && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
                        ExistingAssetScanWindow.Open(library.products);
                });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("BLM連携再読み込み"), false, ReloadBlmLibrary);
            menu.AddItem(new GUIContent("Assetリンク情報再読み込み"), false, ReloadLedger);
            menu.DropDown(anchor);
        }
        private void DrawPending()
        {
            if (!ImportCoordinator.HasPending) return;
            try
            {
                var pending = ImportCoordinator.Pending;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("処理記録: " + pending.product.name + " / " + pending.phase);
                    EditorGUILayout.LabelField(pending.message ?? "Unityのインポート操作の完了を待っています。", EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("完了を確認できない処理を「要確認」として終了"))
                    {
                        if (EditorUtility.DisplayDialog("処理記録の終了", "標準のインポート画面を閉じ、処理が停止していることを確認してください。自動リンクせず要確認の履歴を残します。", "終了する", "戻る"))
                            Run(() => ImportCoordinator.FinishWithoutLinks("NeedsReview", "利用者が処理記録を終了。導入結果は未確認。"));
                    }
                }
            }
            catch (Exception ex) { EditorGUILayout.HelpBox(ex.Message, MessageType.Error); }
        }
        private void DrawProducts()
        {
            var query = EditorGUILayout.TextField(new GUIContent("商品検索", "タグでも検索できます"), search);
            if (query != search) { search = query; page = 0; }
            var filter = EditorGUILayout.Popup("表示対象", catalogFilter, new[] { "すべて", "BLM保存済み", "Assetリンク済み" });
            if (filter != catalogFilter) { catalogFilter = filter; page = 0; productScroll = Vector2.zero; }
            DrawProductListOptions();
            var products = Catalog;
            var linkedKeys = new HashSet<string>(ledger.links.Select(l => l.productKey));
            var filtered = products.Where(p => (search.Length == 0 || ((p.name ?? "") + " " + p.shop + " " + string.Join(" ", tags?.Get(p.key) ?? new List<string>())).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) &&
                MatchesFilter(p, linkedKeys)).ToList();
            filtered = ProductListOrder.Sort(filtered, sortMode, sortDescending);
            var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
            page = Mathf.Clamp(page, 0, pages - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(filtered.Count + "商品", GUILayout.Width(75));
                using (new EditorGUI.DisabledScope(page == 0)) if (GUILayout.Button("前", GUILayout.Width(40))) page--;
                GUILayout.Label((page + 1) + " / " + pages);
                using (new EditorGUI.DisabledScope(page >= pages - 1)) if (GUILayout.Button("次", GUILayout.Width(40))) page++;
            }
            productScroll = EditorGUILayout.BeginScrollView(productScroll);
            foreach (var product in filtered.Skip(page * pageSize).Take(pageSize))
                DrawProductCard(product, linkedKeys.Contains(product.key));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndScrollView();
            if (library == null) EditorGUILayout.HelpBox("接続設定でBLMのdata.dbを指定してください。既存のリンクは保持されています。", MessageType.Info);
        }
        private void DrawProductListOptions()
        {
            var row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var half = (row.width - 8) / 2;
            GUI.Label(new Rect(row.x, row.y, 38, row.height), "並び順");
            var sort = EditorGUI.Popup(new Rect(row.x + 38, row.y, Mathf.Max(1, half - 66), row.height),
                sortMode, new[] { "登録順", "商品名", "ショップ名", "商品ID" });
            var descending = sortDescending;
            if (GUI.Button(new Rect(row.x + half - 25, row.y, 25, row.height),
                new GUIContent(descending ? "↓" : "↑", descending ? "降順（クリックで昇順に切り替え）" : "昇順（クリックで降順に切り替え）"), EditorStyles.miniButton))
                descending = !descending;
            var right = row.x + half + 8;
            GUI.Label(new Rect(right, row.y, 42, row.height), "表示数");
            var sizes = new[] { 50, 100, 200 };
            var sizeIndex = Array.IndexOf(sizes, pageSize);
            var size = sizes[EditorGUI.Popup(new Rect(right + 42, row.y, Mathf.Max(1, half - 42), row.height),
                Math.Max(0, sizeIndex), new[] { "50件", "100件", "200件" })];
            if (sort == sortMode && descending == sortDescending && size == pageSize) return;
            sortMode = sort; sortDescending = descending; pageSize = size;
            EditorPrefs.SetInt("bamu.ProductSortCategory", sortMode);
            EditorPrefs.SetInt("bamu.ProductSortDescending", sortDescending ? 1 : 0);
            EditorPrefs.SetInt("bamu.ProductPageSize", pageSize);
            page = 0; productScroll = Vector2.zero;
        }
        private bool MatchesFilter(Product product, HashSet<string> linkedKeys)
        {
            switch (catalogFilter)
            {
                case 1: return !string.IsNullOrEmpty(product.directory);
                case 2: return linkedKeys.Contains(product.key);
                default: return true;
            }
        }
        private void DrawSelection()
        {
            if (GUILayout.Button("商品詳細へ戻る"))
            { detailTab = 0; detailScroll = Vector2.zero; }
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Projectの選択から逆引き", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(string.IsNullOrEmpty(path) ? "Project内のフォルダ・ファイルを選択" : path, EditorStyles.wordWrappedLabel);
                var keys = Links.Related(path).ToArray();
                foreach (var key in keys)
                {
                    var name = ledger.products.FirstOrDefault(p => p.key == key)?.name ?? key;
                    if (GUILayout.Button(name + " を表示")) SelectProduct(key);
                }
                if (!string.IsNullOrEmpty(path) && keys.Length == 0) EditorGUILayout.LabelField("リンクされた商品はありません。", EditorStyles.miniLabel);
            }
        }
        private void DrawDetail()
        {
            var product = Catalog.FirstOrDefault(p => p.key == selectedKey);
            if (product == null)
            {
                var orphan = ledger.products.FirstOrDefault(p => p.key == selectedKey);
                if (orphan != null) product = new Product { key = orphan.key, boothId = orphan.boothId, name = orphan.name };
            }
            if (product == null) { EditorGUILayout.HelpBox("商品を選ぶと、リンク先と保存ファイルを表示します。", MessageType.Info); return; }
            DrawProductHeader(product);
            if (BoothSyncStatusCache.HasNewSavedVersion(product.key, ledger, BoothSyncStatusCache.Latest))
                EditorGUILayout.HelpBox("新しい保存版があります。保存ファイルから確認し、必要な版を導入してください。", MessageType.Info);
            var groups = Links.Groups(product.key);
            var hasAssetLinks = groups.Count > 0;
            var originals = groups.Where(g => g.role == LinkRole.Original).ToList();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Project内格納ディレクトリ（" + originals.Count + "）", EditorStyles.boldLabel);
            if (originals.Count == 0) EditorGUILayout.LabelField("導入元のリンクはありません。", EditorStyles.wordWrappedMiniLabel);
            DrawLinkGroups(originals);
            if (!hasAssetLinks)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(ImportCoordinator.HasPending || EditorApplication.isCompiling || EditorApplication.isUpdating))
                        if (GUILayout.Button("既存アセットから探す")) ExistingAssetScanWindow.Open(new[] { product });
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(product.directory) || ImportCoordinator.HasPending || EditorApplication.isCompiling || EditorApplication.isUpdating))
                        if (GUILayout.Button("BLM保存ファイルから導入")) missingImportOpen = !missingImportOpen;
                }
                if (missingImportOpen && !string.IsNullOrEmpty(product.directory)) DrawPackages(product);
            }
            DrawPrefabs(product);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("設定", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            if (hasAssetLinks)
                using (new EditorGUI.DisabledScope(library == null || string.IsNullOrEmpty(product.directory) || ImportCoordinator.HasPending))
                    if (GUILayout.Button("この商品の既存アセットを探す")) ExistingAssetScanWindow.Open(new[] { product });
            EditorGUILayout.Space();
            manualOpen = EditorGUILayout.Foldout(manualOpen, "アセットを手動でリンク", true);
            if (manualOpen) DrawManualLink(product);
            var extras = groups.Where(g => g.role != LinkRole.Original).ToList();
            if (extras.Count > 0)
            {
                extraLinksOpen = EditorGUILayout.Foldout(extraLinksOpen, "改変用コピー・参考の関連付け（" + extras.Count + "）", true);
                if (extraLinksOpen) DrawLinkGroups(extras);
            }
            if (hasAssetLinks)
            {
                packageOpen = EditorGUILayout.Foldout(packageOpen, "BLM保存ファイルから導入（" + packages.Count + "）", true);
                if (packageOpen) DrawPackages(product);
            }
            acquisitionOpen = EditorGUILayout.Foldout(acquisitionOpen, "BOOTHの取得対象・ファイル設定", true);
            if (acquisitionOpen) syncPanel.DrawProduct(product);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("導入履歴（直近10件）", EditorStyles.boldLabel);
            foreach (var record in ledger.imports.Where(r => r.productKey == product.key).Reverse().Take(10))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var revision = ledger.revisions.FirstOrDefault(r => r.id == record.revisionId);
                    EditorGUILayout.LabelField(record.state + " · " + record.assets.Count + "/" + record.packageCount + "項目 · " + RevisionLabel(revision));
                    EditorGUILayout.LabelField(record.startedUtc ?? "", EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(record.message)) EditorGUILayout.LabelField(record.message, EditorStyles.wordWrappedLabel);
                }
            }
        }

        private void DrawProductHeader(Product product)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var size = Mathf.Clamp(position.width * .62f * .34f, 96, 180);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
                var image = ThumbnailCache.Get(product);
                if (image != null) GUI.DrawTexture(rect, image, ScaleMode.ScaleToFit);
                else GUI.Label(rect, EditorGUIUtility.IconContent("Prefab Icon"));
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(product.name, new GUIStyle(EditorStyles.wordWrappedLabel) { fontStyle = FontStyle.Bold });
                    EditorGUILayout.LabelField(product.shop ?? "", EditorStyles.wordWrappedMiniLabel);
                    DrawTags(product);
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(product.Url)))
                        if (GUILayout.Button("BOOTHリンク")) Application.OpenURL(product.Url);
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(product.directory) || !Directory.Exists(product.directory)))
                        if (GUILayout.Button("BLMの保存元を開く")) EditorUtility.RevealInFinder(product.directory);
                }
            }
        }
        private void DrawTags(Product product)
        {
            if (tagError.Length > 0) EditorGUILayout.HelpBox(tagError, MessageType.Warning);
            if (tags == null) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("タグ", EditorStyles.miniBoldLabel, GUILayout.Width(32));
                GUILayout.FlexibleSpace();
            }
            DrawAssignedTags(product);
            using (new EditorGUILayout.HorizontalScope())
            {
                tagInput = EditorGUILayout.TextField(tagInput);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(tagInput)))
                    if (GUILayout.Button("追加", GUILayout.Width(42)))
                    {
                        EditTag(() => tags.Add(product.key, tagInput));
                        if (tagError.Length == 0) tagInput = "";
                        GUIUtility.ExitGUI();
                    }
            }
            if (tagInput.Length > 0)
                DrawTagCandidateButtons(product, tags.Suggest(tagInput, product.key), value => value, true);
            if (tagDictionary != null)
            {
                var subcategories = tags.SubcategoryCandidates(product.key, tagDictionary);
                var tagCandidates = tags.Candidates(product.key, tagDictionary).Except(subcategories, StringComparer.OrdinalIgnoreCase).ToList();
                if (tagCandidates.Count > 0) EditorGUILayout.LabelField("BOOTHタグ候補", EditorStyles.miniBoldLabel);
                DrawTagCandidateButtons(product, tagCandidates, value => "＋ " + value);
                if (subcategories.Count > 0) EditorGUILayout.LabelField("BOOTHサブカテゴリ候補", EditorStyles.miniBoldLabel);
                var original = tags.Subcategory(product.key);
                DrawTagCandidateButtons(product, subcategories, value => "＋ " + (value == original ? value : value + "（" + original + "）"));
            }
        }
        private void DrawAssignedTags(Product product)
        {
            var values = tags.Get(product.key);
            if (values.Count == 0) return;
            const float gap = 5;
            var dark = EditorGUIUtility.isProSkin;
            var foreground = dark ? new Color(.91f, .96f, 1f) : new Color(.13f, .25f, .33f);
            var fill = dark ? new Color(.18f, .27f, .34f) : new Color(.90f, .95f, .99f);
            var outline = dark ? new Color(.40f, .63f, .78f) : new Color(.43f, .62f, .74f);
            var style = new GUIStyle(EditorStyles.boldLabel) {
                fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(11, 27, 4, 4)
            };
            style.normal.textColor = foreground;
            var removeStyle = new GUIStyle(EditorStyles.label) {
                fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
            };
            removeStyle.normal.textColor = foreground;
            List<Rect> Arrange(float width)
            {
                var result = new List<Rect>();
                float left = 0, top = 0, rowHeight = 0;
                foreach (var tag in values)
                {
                    var content = new GUIContent(tag);
                    var pillWidth = Mathf.Min(width, Mathf.Max(56, style.CalcSize(content).x));
                    var pillHeight = Mathf.Max(26, style.CalcHeight(content, pillWidth));
                    if (left > 0 && left + pillWidth > width)
                    {
                        left = 0; top += rowHeight + gap; rowHeight = 0;
                    }
                    result.Add(new Rect(left, top, pillWidth, pillHeight));
                    left += pillWidth + gap; rowHeight = Mathf.Max(rowHeight, pillHeight);
                }
                return result;
            }
            var imageSize = Mathf.Clamp(position.width * .62f * .34f, 96, 180);
            var estimatedWidth = Mathf.Max(80, position.width - Mathf.Max(300, position.width * .38f) - imageSize - 48);
            var layout = Arrange(tagCandidateWidth > 0 ? tagCandidateWidth : estimatedWidth);
            var area = GUILayoutUtility.GetRect(1, layout.Max(r => r.yMax), GUILayout.ExpandWidth(true));
            if (area.width > 1)
            {
                layout = Arrange(area.width);
                if (Event.current.type == EventType.Repaint && Mathf.Abs(tagCandidateWidth - area.width) > 1)
                { tagCandidateWidth = area.width; Repaint(); }
            }
            for (var i = 0; i < values.Count; i++)
            {
                var rect = layout[i]; rect.x += area.x; rect.y += area.y;
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, outline, 0, rect.height / 2);
                    var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
                    GUI.DrawTexture(inner, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, fill, 0, inner.height / 2);
                }
                var tag = values[i];
                GUI.Label(rect, new GUIContent(tag, tags.IsAutomatic(product.key, tag) ? "BOOTHから自動採用したタグ" : "手動で設定したタグ"), style);
                var remove = new Rect(rect.xMax - 25, rect.y, 22, rect.height);
                if (GUI.Button(remove, new GUIContent("×", "このタグを削除"), removeStyle))
                {
                    EditTag(() => tags.Remove(product.key, tag));
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.Space(3);
        }
        private void DrawTagCandidateButtons(Product product, List<string> values, Func<string, string> caption, bool clearInput = false)
        {
            if (values.Count == 0) return;
            const float gap = 4;
            var style = new GUIStyle(EditorStyles.miniButton) { wordWrap = true };
            var labels = values.Select(caption).ToList();
            List<Rect> Arrange(float width)
            {
                var result = new List<Rect>();
                var cell = Mathf.Max(1, (width - gap * 2) / 3);
                var line = EditorGUIUtility.singleLineHeight + 2;
                var column = 0;
                float top = 0;
                foreach (var label in labels)
                {
                    if (style.CalcSize(new GUIContent(label)).x > cell)
                    {
                        if (column > 0) { top += line + gap; column = 0; }
                        var height = Mathf.Max(line, style.CalcHeight(new GUIContent(label), width));
                        result.Add(new Rect(0, top, width, height));
                        top += height + gap;
                    }
                    else
                    {
                        if (column == 3) { top += line + gap; column = 0; }
                        result.Add(new Rect(column * (cell + gap), top, cell, line));
                        column++;
                    }
                }
                return result;
            }
            var imageSize = Mathf.Clamp(position.width * .62f * .34f, 96, 180);
            var estimatedWidth = Mathf.Max(80, position.width - Mathf.Max(300, position.width * .38f) - imageSize - 48);
            var layout = Arrange(tagCandidateWidth > 0 ? tagCandidateWidth : estimatedWidth);
            var area = GUILayoutUtility.GetRect(1, layout.Max(r => r.yMax), GUILayout.ExpandWidth(true));
            if (area.width > 1)
            {
                layout = Arrange(area.width);
                if (Event.current.type == EventType.Repaint && Mathf.Abs(tagCandidateWidth - area.width) > 1)
                {
                    tagCandidateWidth = area.width;
                    Repaint(); // Reserve the measured height on the next layout pass.
                }
            }
            for (var i = 0; i < values.Count; i++)
            {
                var rect = layout[i]; rect.x += area.x; rect.y += area.y;
                if (GUI.Button(rect, labels[i], style))
                {
                    var value = values[i];
                    EditTag(() => tags.Add(product.key, value));
                    if (clearInput && tagError.Length == 0) tagInput = "";
                    GUIUtility.ExitGUI();
                }
            }
        }
        private void EditTag(Action edit)
        {
            try { edit(); tagError = ""; }
            catch (Exception ex) { tagError = ex.Message; }
            Repaint();
        }
        private void DrawManualLink(Product product)
        {
            EditorGUILayout.LabelField("既存アセットを手動リンク", EditorStyles.boldLabel);
            role = (LinkRole)EditorGUILayout.Popup("関連付けの種類", (int)role, RoleNames);
            var dropRect = GUILayoutUtility.GetRect(1, 48, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "Projectのフォルダ・ファイルをここへドラッグ");
            HandleDrop(dropRect, product);
            if (GUILayout.Button("Projectで選択中の項目をリンク")) Run(() => LinkObjects(product, Selection.objects));
        }
        private void DrawLinkGroups(IEnumerable<LinkDisplayGroup> groups)
        {
            foreach (var group in groups)
            {
                var targetPath = string.IsNullOrEmpty(group.customFolderGuid) ? group.path : AssetDatabase.GUIDToAssetPath(group.customFolderGuid);
                var target = string.IsNullOrEmpty(targetPath) ? null : AssetDatabase.LoadMainAssetAtPath(targetPath);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField((target == null ? "リンク先なし: " : "") + group.path, EditorStyles.wordWrappedLabel);
                    var revisions = group.links.Select(l => l.revisionId ?? "").Distinct().ToArray();
                    var revision = revisions.Length == 1 ? ledger.revisions.FirstOrDefault(r => r.id == revisions[0]) : null;
                    var label = revisions.Length > 1 ? "複数の導入記録" : RevisionLabel(revision);
                    var methods = string.Join("・", group.links.Select(l => l.method == "Manual" ? "手動" : l.method == "Scan" ? "既存照合" : l.method == "Import" ? "インポート" : PrefabConverterRegistry.LabelFor(l.method)).Distinct());
                    EditorGUILayout.LabelField(RoleNames[(int)group.role] + " · " + methods +
                        (group.groupedImport ? " · " + group.links.Count + "項目" : "") + " · 導入時の記録: " + label, EditorStyles.miniLabel);
                    if (group.missingCount > 0)
                        EditorGUILayout.LabelField("リンク切れ: " + group.missingCount + "項目", EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(group.overrideId)) EditorGUILayout.LabelField("表示フォルダ: 手動指定", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(target == null))
                            if (GUILayout.Button("Project内で選択"))
                            {
                                Selection.activeObject = target; EditorGUIUtility.PingObject(target);
                            }
                        if (GUILayout.Button("リンク解除", GUILayout.Width(85)))
                        {
                            var ids = new HashSet<string>(group.links.Select(l => l.id));
                            Run(() => { ledger = BridgeStorage.Load(); ledger.links.RemoveAll(l => ids.Contains(l.id)); BridgeStorage.Save(ledger); });
                            GUIUtility.ExitGUI();
                        }
                    }
                    using (new EditorGUI.DisabledScope(ImportCoordinator.HasPending))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("リンク先フォルダを変更")) LinkFolderWindow.Open(group);
                        if (!string.IsNullOrEmpty(group.overrideId) && GUILayout.Button("自動判定に戻す", GUILayout.Width(115)))
                        {
                            Run(() => { ledger = BridgeStorage.Load(); ledger.displayFolders.RemoveAll(r => r.id == group.overrideId); BridgeStorage.Save(ledger); });
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }
        private void DrawPackages(Product product)
        {
            EditorGUILayout.LabelField("保存ファイルから導入（自動リンク）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("この画面から導入すると、Unityが取り込んだ項目を自動で商品にリンクします。既存リンクの版は上書きせず、導入履歴に記録します。", MessageType.None);
            versionLabel = EditorGUILayout.TextField("導入版の表記（任意）", versionLabel);
            using (new EditorGUI.DisabledScope(ImportCoordinator.HasPending || library == null || string.IsNullOrEmpty(product.directory)))
            {
                foreach (var package in packages)
                {
                    EditorGUILayout.LabelField(package.Substring(product.directory.Length).TrimStart('\\', '/'), EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("このファイルを選んで導入")) ChoosePackage(product, package);
                }
                if (packages.Count == 0) EditorGUILayout.LabelField("保存先にunitypackage / ZIPが見つかりません。", EditorStyles.wordWrappedLabel);
                if (GUILayout.Button("別のunitypackageを指定して導入"))
                {
                    var file = EditorUtility.OpenFilePanel("この商品に対応するunitypackage", product.directory, "unitypackage");
                    if (file.Length > 0) ChoosePackage(product, file);
                }
            }
            EditorGUILayout.Space();
        }
        private static string RevisionLabel(ArtifactRevision revision) => revision == null ? "不明" :
            (string.IsNullOrWhiteSpace(revision.label) ? "版表記なし" : revision.label) + " [" + revision.sha256.Substring(0, Math.Min(8, revision.sha256.Length)) + "]";
        private void LinkObjects(Product product, Object[] objects)
        {
            var paths = objects.Select(AssetDatabase.GetAssetPath).Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)).Distinct().ToArray();
            if (paths.Length == 0) throw new ArgumentException("Project内のAssets配下を選択してください。");
            // Reload from disk so an import callback cannot be overwritten by a stale window.
            ledger = BridgeStorage.Load();
            BridgeStorage.EnsureProduct(ledger, ProductReference.From(product));
            foreach (var path in paths) BridgeStorage.AddLink(ledger, product.key, path, role, "Manual");
            BridgeStorage.Save(ledger);
        }
        private void HandleDrop(Rect rect, Product product)
        {
            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition) || (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
            if (ledger == null || !DragAndDrop.objectReferences.Any(o => AssetDatabase.GetAssetPath(o).StartsWith("Assets/", StringComparison.Ordinal))) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (evt.type == EventType.DragPerform)
            {
                var objects = DragAndDrop.objectReferences.ToArray();
                DragAndDrop.AcceptDrag(); Run(() => LinkObjects(product, objects)); selectedKey = product.key; Run(RefreshPackages);
            }
            evt.Use();
        }
        private void ChoosePackage(Product product, string file)
        {
            if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { Run(() => ImportCoordinator.Begin(product, file, versionLabel)); return; }
            Run(() =>
            {
                var entries = UnityPackageReader.ZipPackages(file);
                if (entries.Count == 0) throw new InvalidDataException("ZIP内にunitypackageがありません。");
                var menu = new GenericMenu();
                foreach (var entry in entries)
                {
                    var captured = entry;
                    menu.AddItem(new GUIContent(entry), false, () => Run(() =>
                        ImportCoordinator.Begin(product, UnityPackageReader.ExtractZipPackage(file, captured),
                            string.IsNullOrEmpty(versionLabel) ? Path.GetFileNameWithoutExtension(captured) : versionLabel)));
                }
                menu.ShowAsContext();
            });
        }
    }
}
