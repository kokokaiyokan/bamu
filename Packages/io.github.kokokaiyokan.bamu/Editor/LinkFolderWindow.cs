using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "BoothUnityBridge", "BoothUnityBridge.Editor", "LinkFolderWindow")]
    public sealed class LinkFolderWindow : EditorWindow
    {
        [SerializeField] private List<string> linkIds;
        [SerializeField] private string previousRuleId, previousPath;
        [SerializeField] private bool automatic;
        [SerializeField] private DefaultAsset folder;
        private string error = "";

        public static void Open(LinkDisplayGroup group)
        {
            var window = CreateInstance<LinkFolderWindow>();
            window.titleContent = new GUIContent("リンク先フォルダを変更");
            window.minSize = new Vector2(520, 240);
            window.linkIds = group.links.Select(l => l.id).ToList();
            window.previousRuleId = group.overrideId; window.previousPath = group.path; window.automatic = group.groupedImport;
            window.folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(group.path);
            window.ShowUtility();
        }
        private void OnGUI()
        {
            EditorGUILayout.LabelField("現在: " + previousPath, EditorStyles.wordWrappedLabel);
            EditorGUILayout.HelpBox(automatic
                ? "表示したいフォルダをProjectから指定してください。その配下の、この商品にリンク済みの項目をまとめます。狭いフォルダを選んだ場合、残りの項目は別の行に表示します。"
                : "新しく関連付けたいフォルダをProjectから指定してください。", MessageType.Info);
            folder = (DefaultAsset)EditorGUILayout.ObjectField("変更先フォルダ", folder, typeof(DefaultAsset), false);
            EditorGUILayout.LabelField("実ファイルの移動は行いません。", EditorStyles.miniLabel);
            if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(folder == null || linkIds == null || ImportCoordinator.HasPending))
                if (GUILayout.Button("保存する"))
                {
                    try
                    {
                        var ledger = BridgeStorage.Load();
                        LinkFolderEditor.Change(ledger, linkIds, AssetDatabase.GetAssetPath(folder), previousRuleId);
                        BridgeStorage.Save(ledger); BridgeWindow.RefreshOpenWindows(); Close();
                    }
                    catch (Exception ex) { error = ex.Message; }
                }
        }
    }
}
