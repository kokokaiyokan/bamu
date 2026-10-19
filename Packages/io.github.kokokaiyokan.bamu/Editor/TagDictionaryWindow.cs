using System;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "BoothUnityBridge", "BoothUnityBridge.Editor", "TagDictionaryWindow")]
    internal sealed class TagDictionaryWindow : EditorWindow
    {
        private TagDictionary tagDictionary;
        [SerializeField] private TagDictionaryData dictionaryEdit;
        private Vector2 scroll;
        private string error = "";

        internal static void Open()
        {
            GetWindow<TagDictionaryWindow>(true, "BAMU 辞書設定", true).Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("BAMU 辞書設定");
            minSize = new Vector2(560, 360);
            try
            {
                tagDictionary = new TagDictionary(BridgeStorage.TagDictionaryPath);
                error = "";
            }
            catch (Exception ex) { error = ex.Message; }
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            try { DrawDictionary(); }
            finally { EditorGUILayout.EndScrollView(); }
        }

        private void SaveDictionary()
        {
            try
            {
                tagDictionary.Save(dictionaryEdit);
                new ProductTags(BridgeStorage.TagsPath).SyncAutomatic(BoothSyncStatusCache.Latest, tagDictionary);
                BridgeWindow.RefreshTagWindows();
                error = "";
                ShowNotification(new GUIContent("辞書・採用方法を保存しました"));
            }
            catch (Exception ex) { error = ex.Message; }
        }

        private void DrawDictionary()
        {
            EditorGUILayout.LabelField("BOOTHタグの採用辞書", EditorStyles.boldLabel);
            if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Warning);
            if (tagDictionary == null) return;
            if (dictionaryEdit == null) dictionaryEdit = JsonUtility.FromJson<TagDictionaryData>(JsonUtility.ToJson(tagDictionary.Data));
            var modes = new[] { "automatic", "suggest", "off" };
            var mode = Math.Max(0, Array.IndexOf(modes, dictionaryEdit.mode));
            dictionaryEdit.mode = modes[EditorGUILayout.Popup("採用方法", mode, new[] { "自動採用", "候補表示のみ", "辞書参照なし" })];
            EditorGUILayout.LabelField("BOOTHの商品タグは辞書の語だけを完全一致で照合します。サブカテゴリは辞書外でも候補に表示し、クリックした場合だけ追加します。「辞書参照なし」では両方の候補を隠します。手動タグはどのモードでも保持します。設定は全プロジェクト共通です。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("採用タグと表記ゆれを編集できます。表記ゆれはカンマ区切りで入力してください。", EditorStyles.wordWrappedMiniLabel);
            for (var i = 0; i < dictionaryEdit.rules.Count; i++)
            {
                var rule = dictionaryEdit.rules[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        rule.tag = EditorGUILayout.TextField("採用タグ", rule.tag);
                        if (GUILayout.Button("削除", GUILayout.Width(50))) { dictionaryEdit.rules.RemoveAt(i); GUIUtility.ExitGUI(); }
                    }
                    rule.aliases = EditorGUILayout.TextField("表記ゆれ（完全一致）", rule.aliases);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("辞書に語を追加")) { dictionaryEdit.rules.Add(new TagRule()); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("プリセットを編集欄へ戻す"))
                {
                    var selectedMode = dictionaryEdit.mode;
                    dictionaryEdit = TagDictionary.Preset(); dictionaryEdit.mode = selectedMode; GUIUtility.ExitGUI();
                }
            }
            if (GUILayout.Button("辞書・採用方法を保存して適用"))
            {
                SaveDictionary();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.LabelField("自動採用から他のモードへ切り替えると自動タグだけを外します。辞書から語を削除した場合も同様です。自動タグを商品側で削除すると、その商品では再採用しません。必要なら手動で追加できます。", EditorStyles.wordWrappedMiniLabel);
        }
    }
}
