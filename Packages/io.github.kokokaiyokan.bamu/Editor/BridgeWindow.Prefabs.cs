using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace bamu
{
    public sealed partial class BridgeWindow
    {
        private readonly Dictionary<string, List<PrefabCandidate>> prefabCandidates = new Dictionary<string, List<PrefabCandidate>>();
        private bool prefabChoicesOpen;
        private GameObject prefabToAdd, dressAvatar;

        private List<PrefabCandidate> PrefabCandidates(Product product)
        {
            if (!prefabCandidates.TryGetValue(product.key, out var candidates))
                prefabCandidates[product.key] = candidates = PrefabCatalog.Candidates(ledger, product.key, AssetDatabase.GUIDToAssetPath,
                    folder => AssetDatabase.IsValidFolder(folder) ? AssetDatabase.FindAssets("t:Prefab", new[] { folder }) : Array.Empty<string>());
            return candidates;
        }

        private void SavePrefabSelection(Product product, IEnumerable<string> guids)
        {
            var selected = guids.ToList();
            Run(() =>
            {
                ledger = BridgeStorage.Load();
                PrefabCatalog.SetSelected(ledger, product.key, selected);
                BridgeStorage.Save(ledger);
            });
            GUIUtility.ExitGUI();
        }

        private void DrawPrefabs(Product product)
        {
            var converters = PrefabConverterRegistry.All;
            var candidates = PrefabCandidates(product);
            var selected = PrefabCatalog.Selected(ledger, product.key, candidates);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("使用するprefab（" + selected.Count + "）", EditorStyles.boldLabel);
            if (selected.Count == 0) EditorGUILayout.LabelField("表示するprefabがありません。候補から選ぶか、手動で追加してください。", EditorStyles.wordWrappedMiniLabel);
            dressAvatar = (GameObject)EditorGUILayout.ObjectField(new GUIContent("配置先アバター", "Hierarchyのアバターのルートを指定してください。"), dressAvatar, typeof(GameObject), true);
            EditorGUILayout.LabelField("「アバターに配置」はアバターの子として追加します。衣装側の設定を引き継ぎ、Ctrl+Zで戻せます。", EditorStyles.wordWrappedMiniLabel);
            foreach (var guid in selected)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = PrefabCatalog.IsPrefab(path) ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(prefab, typeof(GameObject), false);
                        if (GUILayout.Button("外す", GUILayout.Width(50))) SavePrefabSelection(product, selected.Where(g => g != guid));
                    }
                    if (prefab == null) EditorGUILayout.LabelField("リンク先のprefabが見つかりません。GUID: " + guid, EditorStyles.wordWrappedMiniLabel);
                    else EditorGUILayout.LabelField(path, EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(prefab == null))
                            if (GUILayout.Button("Projectで選択")) { Selection.activeObject = prefab; EditorGUIUtility.PingObject(prefab); }
                        using (new EditorGUI.DisabledScope(prefab == null || !CanPlacePrefab(dressAvatar) || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
                            if (GUILayout.Button("アバターに配置")) Run(() => PlacePrefab(prefab, dressAvatar));
                    }
                    foreach (var converter in converters)
                        using (new EditorGUI.DisabledScope(prefab == null || !converter.Available || converter.HasPending || EditorApplication.isPlayingOrWillChangePlaymode || ImportCoordinator.HasPending))
                            if (GUILayout.Button(converter.DisplayName + "で変換")) Run(() => converter.Open(product, prefab));
                }
            }
            foreach (var converter in converters)
            {
                if (!converter.Available)
                    EditorGUILayout.LabelField(converter.DisplayName + "連携: 対応するツールが導入されている場合に利用できます。", EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(converter.Notice)) EditorGUILayout.HelpBox(converter.Notice, MessageType.Info);
                if (converter.HasPending && GUILayout.Button(converter.DisplayName + "の自動リンク待ちを解除")) converter.Cancel();
            }
            prefabChoicesOpen = EditorGUILayout.Foldout(prefabChoicesOpen, "表示するprefabを追加・変更", true);
            if (!prefabChoicesOpen) return;
            EditorGUILayout.LabelField("未設定時は導入元を優先し、default等の名前を持つものから代表1個を表示します。カラーバリエーションは自動で全件表示しません。", EditorStyles.wordWrappedMiniLabel);
            // The candidate list is opt-in and paged to avoid rendering thousands of controls.
            DrawPrefabChoices(product, candidates, selected);
            using (new EditorGUILayout.HorizontalScope())
            {
                prefabToAdd = (GameObject)EditorGUILayout.ObjectField("手動で追加", prefabToAdd, typeof(GameObject), false);
                using (new EditorGUI.DisabledScope(prefabToAdd == null || !PrefabCatalog.IsPrefab(AssetDatabase.GetAssetPath(prefabToAdd))))
                    if (GUILayout.Button("追加", GUILayout.Width(50)))
                    {
                        var path = AssetDatabase.GetAssetPath(prefabToAdd);
                        var guid = AssetDatabase.AssetPathToGUID(path);
                        Run(() =>
                        {
                            ledger = BridgeStorage.Load();
                            BridgeStorage.EnsureProduct(ledger, ProductReference.From(product));
                            BridgeStorage.AddLink(ledger, product.key, path, LinkRole.Reference, "Manual");
                            PrefabCatalog.SetSelected(ledger, product.key, selected.Concat(new[] { guid }));
                            BridgeStorage.Save(ledger); prefabToAdd = null;
                        });
                        GUIUtility.ExitGUI();
                    }
            }
            if (GUILayout.Button("代表1個の自動選択に戻す"))
            {
                Run(() => { ledger = BridgeStorage.Load(); ledger.prefabSelections.RemoveAll(s => s.productKey == product.key); BridgeStorage.Save(ledger); });
                GUIUtility.ExitGUI();
            }
        }

        private string prefabFilter = "";
        private int prefabPage;
        private void DrawPrefabChoices(Product product, List<PrefabCandidate> candidates, List<string> selected)
        {
            var filter = EditorGUILayout.TextField("prefab検索", prefabFilter);
            if (filter != prefabFilter) { prefabFilter = filter; prefabPage = 0; }
            var filtered = candidates.Where(c => prefabFilter.Length == 0 || c.path.IndexOf(prefabFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            const int pageSize = 15;
            var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
            prefabPage = Mathf.Clamp(prefabPage, 0, pages - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(filtered.Count + "候補 · " + (prefabPage + 1) + " / " + pages);
                using (new EditorGUI.DisabledScope(prefabPage == 0)) if (GUILayout.Button("前", GUILayout.Width(40))) prefabPage--;
                using (new EditorGUI.DisabledScope(prefabPage >= pages - 1)) if (GUILayout.Button("次", GUILayout.Width(40))) prefabPage++;
            }
            foreach (var candidate in filtered.Skip(prefabPage * pageSize).Take(pageSize))
            {
                var enabled = selected.Contains(candidate.guid);
                if (EditorGUILayout.ToggleLeft(candidate.path, enabled) != enabled)
                    SavePrefabSelection(product, enabled ? selected.Where(g => g != candidate.guid) : selected.Concat(new[] { candidate.guid }));
            }
        }

        private static bool CanPlacePrefab(GameObject avatar) => avatar != null && !EditorUtility.IsPersistent(avatar) &&
            avatar.scene.IsValid() && avatar.scene.isLoaded && PrefabStageUtility.GetPrefabStage(avatar) == null;

        private static void PlacePrefab(GameObject prefab, GameObject avatar)
        {
            if (!CanPlacePrefab(avatar) || !PrefabCatalog.IsPrefab(AssetDatabase.GetAssetPath(prefab)))
                throw new InvalidOperationException("Projectのprefabと、Hierarchyの配置先アバターを指定してください。");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform);
            Undo.RegisterCreatedObjectUndo(instance, "BOOTH prefabをアバターに配置");
            EditorSceneManager.MarkSceneDirty(avatar.scene);
            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
        }
    }
}
