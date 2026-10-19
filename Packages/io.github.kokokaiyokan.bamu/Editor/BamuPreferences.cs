using UnityEditor;

namespace bamu
{
    internal static class BamuPreferences
    {
        private static string Previous(string key) => key.StartsWith("bamu.") ? "BoothUnityBridge." + key.Substring(5) : key;
        internal static string GetString(string key, string fallback)
        {
            if (!EditorPrefs.HasKey(key) && EditorPrefs.HasKey(Previous(key)))
                EditorPrefs.SetString(key, EditorPrefs.GetString(Previous(key), fallback));
            return EditorPrefs.GetString(key, fallback);
        }
        internal static int GetInt(string key, int fallback)
        {
            if (!EditorPrefs.HasKey(key) && EditorPrefs.HasKey(Previous(key)))
                EditorPrefs.SetInt(key, EditorPrefs.GetInt(Previous(key), fallback));
            return EditorPrefs.GetInt(key, fallback);
        }
        internal static bool GetBool(string key, bool fallback)
        {
            if (!EditorPrefs.HasKey(key) && EditorPrefs.HasKey(Previous(key)))
                EditorPrefs.SetBool(key, EditorPrefs.GetBool(Previous(key), fallback));
            return EditorPrefs.GetBool(key, fallback);
        }
    }
}
