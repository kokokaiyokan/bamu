using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "BoothUnityBridge", "BoothUnityBridge.Editor", "ConnectionSettingsWindow")]
    internal sealed class ConnectionSettingsWindow : EditorWindow
    {
        [SerializeField] private int connectionTab;
        private Vector2 scroll;
        private string dbInput, rootInput, blmInput;
        private string error = "";
        private BlmLibrary library;
        private BoothSyncPanel syncPanel;

        internal static void Open()
        {
            GetWindow<ConnectionSettingsWindow>(true, "BAMU 接続設定", true).Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("BAMU 接続設定");
            minSize = new Vector2(560, 360);
            dbInput = BridgeStorage.DatabasePath;
            rootInput = BridgeStorage.LibraryOverride;
            blmInput = BridgeStorage.BlmExecutable;
            connectionTab = Mathf.Clamp(connectionTab, 0, 1);
            syncPanel = new BoothSyncPanel(Repaint);
            BoothSyncStatusCache.Changed += Repaint;
            ReloadLibrary();
        }

        private void OnDisable() { BoothSyncStatusCache.Changed -= Repaint; }

        private void Run(Action action)
        {
            try { action(); error = ""; }
            catch (Exception ex) { error = ex.Message; }
            Repaint();
        }

        private void ReloadLibrary()
        {
            library = null;
            Run(() => library = BlmLibrary.Load(BridgeStorage.DatabasePath, BridgeStorage.LibraryOverride));
        }

        private void OnGUI()
        {
            connectionTab = GUILayout.Toolbar(connectionTab, new[] { "BOOTH Library Manager", "Chrome（BOOTH）" });
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (connectionTab == 0)
            {
                DrawBlmSettings();
                if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Warning);
            }
            else
            {
                syncPanel.DrawSettings();
                syncPanel.DrawNotice();
            }
            EditorGUILayout.EndScrollView();
        }
        private void DrawBlmSettings()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("BOOTH Library Manager 接続設定", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    blmInput = EditorGUILayout.TextField("BLMのパス", blmInput);
                    if (GUILayout.Button("参照", GUILayout.Width(50)))
                    {
                        var path = EditorUtility.OpenFilePanel("BLMの実行ファイル", Path.GetDirectoryName(blmInput), "exe");
                        if (path.Length > 0) blmInput = path;
                    }
                    using (new EditorGUI.DisabledScope(!File.Exists(blmInput)))
                        if (GUILayout.Button("起動", GUILayout.Width(50))) Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(blmInput) { UseShellExecute = true }));
                }
                EditorGUILayout.LabelField("保存先", library?.root ?? "未接続");
                rootInput = EditorGUILayout.TextField("保存先の指定（任意）", rootInput);
                using (new EditorGUILayout.HorizontalScope())
                {
                    dbInput = EditorGUILayout.TextField("BLMデータベース", dbInput);
                    if (GUILayout.Button("参照", GUILayout.Width(50)))
                    {
                        var path = EditorUtility.OpenFilePanel("BLMのdata.db", Path.GetDirectoryName(dbInput), "db");
                        if (path.Length > 0) dbInput = path;
                    }
                }
                if (GUILayout.Button("BLM接続設定を保存"))
                {
                    BridgeStorage.BlmExecutable = blmInput;
                    BridgeStorage.DatabasePath = dbInput; BridgeStorage.LibraryOverride = rootInput; UnityBridgeReceiver.Restart(); ReloadLibrary(); BridgeWindow.ReloadOpenWindows();
                }
            }
        }
    }
}
