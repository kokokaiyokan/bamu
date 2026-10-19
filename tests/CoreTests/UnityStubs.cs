// Only the test runner uses these stand-ins. Shipping code is separately compiled
// against the real Unity 2022.3 DLLs; no native Editor behaviour is claimed here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public sealed class GameObject { }
    public static class Application
    {
        private static string currentDataPath;
        public static string dataPath
        {
            get => currentDataPath;
            set => currentDataPath = TestEnvironment.RequireInsideRun(value);
        }
    }
    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
            { IncludeFields = true, IgnoreReadOnlyProperties = true, WriteIndented = true };
        public static string ToJson(object value, bool pretty) => JsonSerializer.Serialize(value, value.GetType(), Options);
        public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
    }
    public static class Debug { public static void LogError(object value) => Console.Error.WriteLine(value); }
}
namespace UnityEditor
{
    public sealed class InitializeOnLoadAttribute : Attribute { }
    public static class EditorPrefs
    {
        private static readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        public static bool HasKey(string key) => Data.ContainsKey(key);
        public static int GetInt(string key, int fallback) => Data.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : fallback;
        public static void SetInt(string key, int value) => Data[key] = value.ToString();
        public static bool GetBool(string key, bool fallback) => Data.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) ? flag : fallback;
        public static void SetBool(string key, bool value) => Data[key] = value.ToString();
        public static string GetString(string key, string fallback) => Data.TryGetValue(key, out var value) ? value : fallback;
        public static void SetString(string key, string value) => Data[key] = value;
    }
    public static class EditorUtility
    {
        public static bool DisplayCancelableProgressBar(string title, string info, float progress) => false;
        public static void ClearProgressBar() { }
    }
    public static class EditorApplication
    {
        public static bool isCompiling, isUpdating, isPlayingOrWillChangePlaymode;
        public static double timeSinceStartup;
        public static event Action update;
        public static event Action delayCall;
        public static void Tick(double seconds = 2)
        {
            timeSinceStartup += seconds;
            var delayed = delayCall; delayCall = null; delayed?.Invoke();
            update?.Invoke();
        }
    }
    public static class AssetDatabase
    {
        private static readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static event Action<string> importPackageStarted, importPackageCompleted, importPackageCancelled;
        public static event Action<string, string> importPackageFailed;
        public static Action<string[]> onImportPackageItemsCompleted;
        public static string GUIDToAssetPath(string guid) => guid != null && Paths.TryGetValue(guid, out var path) ? path : "";
        public static string AssetPathToGUID(string path) => Paths.FirstOrDefault(p => p.Value == path).Key ?? "";
        public static bool IsValidFolder(string path) => Directory.Exists(Path.Combine(bamu.BridgeStorage.Root, path));
        public static string[] FindAssets(string filter, string[] folders) => Paths.Where(p => folders.Any(f => p.Value.StartsWith(f + "/", StringComparison.Ordinal))).Select(p => p.Key).ToArray();
        public static void Register(string guid, string path) => Paths[guid] = path;
        public static void Forget(string guid) => Paths.Remove(guid);
        public static void ImportPackage(string path, bool interactive) => importPackageStarted?.Invoke(Path.GetFileNameWithoutExtension(path));
        public static void Start(string name) => importPackageStarted?.Invoke(name);
        public static void Complete(string name, params string[] paths)
        {
            onImportPackageItemsCompleted?.Invoke(paths); importPackageCompleted?.Invoke(name);
        }
        public static void CompleteWithoutItems(string name) => importPackageCompleted?.Invoke(name);
        public static void Cancel(string name) => importPackageCancelled?.Invoke(name);
        public static void Fail(string name) => importPackageFailed?.Invoke(name, "Test failure");
    }
}
