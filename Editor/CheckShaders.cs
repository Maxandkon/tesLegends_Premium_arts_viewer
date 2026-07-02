#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

/// Assets/Editor/CheckShaders.cs
/// Menu: Tools -> TESL -> Check Shaders in Appbase
/// Tells you exactly what shaders the game uses and whether
/// Unity can see their source code (needed for WebGL recompile).
public class CheckShaders
{
    [MenuItem("Tools/TESL/Check Shaders in Appbase...")]
    static void Run()
    {
        string src = EditorPrefs.GetString("TESL_LastSA", "");
        src = EditorUtility.OpenFolderPanel("Select StreamingAssets", src, "");
        if (string.IsNullOrEmpty(src)) return;
        EditorPrefs.SetString("TESL_LastSA", src);

        string appbasePath = Path.Combine(src, "appbase", "cardshaders");
        if (!File.Exists(appbasePath))
        {
            // Try to find any file in appbase that might contain shaders
            string appbaseDir = Path.Combine(src, "appbase");
            if (!Directory.Exists(appbaseDir))
            { EditorUtility.DisplayDialog("Error", "appbase/ not found", "OK"); return; }
            // Find likely shader bundle
            foreach (string f in Directory.GetFiles(appbaseDir))
                if (Path.GetFileName(f).ToLower().Contains("shader"))
                { appbasePath = f; break; }
        }

        if (!File.Exists(appbasePath))
        {
            EditorUtility.DisplayDialog("Not found",
                "Could not find cardshaders bundle in appbase/.\n" +
                "Files in appbase/:\n" +
                string.Join("\n", Directory.GetFiles(Path.Combine(src,"appbase"))),
                "OK");
            return;
        }

        AssetBundle bundle = AssetBundle.LoadFromFile(appbasePath);
        if (bundle == null)
        { EditorUtility.DisplayDialog("Error", "Could not load: " + appbasePath, "OK"); return; }

        var results = new List<string>();
        results.Add("Bundle: " + Path.GetFileName(appbasePath));
        results.Add("All assets inside:");
        results.Add("");

        int shaderCount = 0;
        foreach (string name in bundle.GetAllAssetNames())
        {
            results.Add("  " + name);
            if (name.EndsWith(".shader")) shaderCount++;
        }

        results.Add("");
        results.Add("--- Shader objects ---");
        Shader[] shaders = bundle.LoadAllAssets<Shader>();
        results.Add("Found " + shaders.Length + " Shader objects");
        foreach (Shader s in shaders)
        {
            bool hasSource = !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(s));
            results.Add("  [" + (hasSource ? "SOURCE OK" : "binary only") + "] " + s.name);
        }

        bundle.Unload(false);

        string report = string.Join("\n", results.ToArray());
        Debug.Log("[CheckShaders]\n" + report);

        // Save report to file
        string reportPath = Path.Combine(Application.dataPath, "..", "shader_report.txt");
        File.WriteAllText(reportPath, report);

        EditorUtility.DisplayDialog("Done",
            "Found " + shaders.Length + " shaders.\n\n" +
            "Full report saved to:\n" + reportPath + "\n\n" +
            "Check the Console for details.\n\n" +
            (shaders.Length > 0
                ? "✓ Shaders found — WebGL recompile may be possible!"
                : "✗ No Shader objects — only compiled binary (harder path)"),
            "OK");

        EditorUtility.RevealInFinder(reportPath);
    }
}
#endif
