using System;
using System.Diagnostics;
using System.IO;
using bamu;

// All application data and fixtures belong to this invocation. No Unity Editor
// assemblies, registry settings, purchased assets or real BLM database are used.
internal static class TestEnvironment
{
    public static string RunRoot { get; private set; }
    public static string Fixtures => Path.Combine(RunRoot, "fixtures");

    public static void Initialize(string repository)
    {
        RunRoot = Path.GetFullPath(Path.Combine(repository, "..", "bamu_development", "artifacts",
            "core-test-synthetic-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(RunRoot)) throw new IOException("Test directory already exists.");
        Directory.CreateDirectory(RunRoot);
        BridgeStorage.UserFolder = UserFolder;
        using (var process = new Process { StartInfo = new ProcessStartInfo("python")
            { UseShellExecute = false, CreateNoWindow = true } })
        {
            process.StartInfo.ArgumentList.Add(Path.Combine(repository, "scripts", "create-fixtures.py"));
            process.StartInfo.ArgumentList.Add("--output");
            process.StartInfo.ArgumentList.Add(Fixtures);
            process.Start();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Synthetic fixture generation failed. Python 3 is required.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(BridgeStorage.DefaultDatabase));
        File.Copy(Path.Combine(Fixtures, "fixture.db"), BridgeStorage.DefaultDatabase);
    }

    private static string UserFolder(Environment.SpecialFolder folder)
    {
        if (folder != Environment.SpecialFolder.LocalApplicationData && folder != Environment.SpecialFolder.ApplicationData)
            throw new InvalidOperationException("Unexpected user folder in core tests: " + folder);
        return Path.Combine(RunRoot, "user", folder.ToString());
    }

    public static string RequireInsideRun(string path)
    {
        var full = Path.GetFullPath(path);
        if (RunRoot == null || !full.StartsWith(RunRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Core tests may only use their isolated project directory.");
        return full;
    }
}
