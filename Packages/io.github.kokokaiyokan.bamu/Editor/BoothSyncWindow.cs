using UnityEditor;
namespace bamu
{
    // Redirect saved layouts and old callers to the integrated Manager.
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "BoothUnityBridge", "BoothUnityBridge.Editor", "BoothSyncWindow")]
    public sealed class BoothSyncWindow : EditorWindow
    {
        public static void Open() => BridgeWindow.Open();
        private void OnEnable() { EditorApplication.delayCall += Redirect; }
        private void OnDisable() { EditorApplication.delayCall -= Redirect; }
        private void Redirect() { if (this == null) return; BridgeWindow.Open(); Close(); }
    }
}
