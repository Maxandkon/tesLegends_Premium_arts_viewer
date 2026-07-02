#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

/// TES: Legends — Bundle Extractor
/// Place in: Assets/Editor/TESLBundleExtractor.cs
/// Menu: Tools → TESL → Extract Bundles to Folder...
public class TESLBundleExtractor
{
    [MenuItem("Tools/TESL/Extract Bundles to Folder...")]
    static void Run()
    {
        // 1. Pick source (game's StreamingAssets)
        string src = EditorPrefs.GetString("TESL_LastSA", "");
        src = EditorUtility.OpenFolderPanel("Select game StreamingAssets folder", src, "");
        if (string.IsNullOrEmpty(src)) return;
        EditorPrefs.SetString("TESL_LastSA", src);

        if (!Directory.Exists(Path.Combine(src, "contentpack000")))
        {
            EditorUtility.DisplayDialog("Not found",
                "contentpack000 not found in selected folder.\n" +
                "Select the StreamingAssets folder of the game.", "OK");
            return;
        }

        // 2. Pick destination
        string defaultOut = Path.Combine(Application.dataPath, "..", "tesl_bundles");
        string dst = EditorPrefs.GetString("TESL_LastOut", defaultOut);
        dst = EditorUtility.OpenFolderPanel("Select output folder", dst, "");
        if (string.IsNullOrEmpty(dst)) return;
        EditorPrefs.SetString("TESL_LastOut", dst);

        // 3. Collect files the same way CardLoader does
        // Using Tuple<string,string> for C# 6 compatibility
        var jobs = new List<System.Tuple<string, string>>();

        // appbase/ and common000/ — all extension-less bundles
        foreach (string dirName in new[] { "appbase", "common000" })
        {
            string dir = Path.Combine(src, dirName);
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.GetFiles(dir))
                if (string.IsNullOrEmpty(Path.GetExtension(f)) && IsBundle(f))
                    jobs.Add(System.Tuple.Create(f, dirName + "/" + Path.GetFileName(f)));
        }

        // contentpack000/ — all extension-less bundles
        string cp000 = Path.Combine(src, "contentpack000");
        foreach (string f in Directory.GetFiles(cp000))
            if (string.IsNullOrEmpty(Path.GetExtension(f)) && IsBundle(f))
                jobs.Add(System.Tuple.Create(f, "contentpack000/" + Path.GetFileName(f)));

        // DLC: contentpack001 … contentpackNNN
        foreach (string dlcDir in Directory.GetDirectories(src))
        {
            string dn = Path.GetFileName(dlcDir).ToLower();
            if (!dn.StartsWith("contentpack") || dn == "contentpack000") continue;
            string num    = dn.Replace("contentpack", "").PadLeft(3, '0');
            string prefix = "cp" + num + "_";
            foreach (string f in Directory.GetFiles(dlcDir))
            {
                if (!string.IsNullOrEmpty(Path.GetExtension(f))) continue;
                string fn = Path.GetFileName(f).ToLower();
                bool keep = fn.StartsWith(prefix + "cardtextures_")
                         || fn.StartsWith(prefix + "texture_")
                         || fn.StartsWith(prefix + "assets_")
                         || fn.StartsWith(prefix + "cardmaterials_");
                if (keep && IsBundle(f))
                    jobs.Add(System.Tuple.Create(f,
                        Path.GetFileName(dlcDir) + "/" + Path.GetFileName(f)));
            }
        }

        if (jobs.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing found",
                "No AssetBundle files found. Check the source path.", "OK");
            return;
        }

        // 4. Copy with progress bar
        long totalBytes = 0;
        int  copied = 0, skipped = 0;
        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                string srcFile = jobs[i].Item1;
                string rel     = jobs[i].Item2;

                if (EditorUtility.DisplayCancelableProgressBar(
                    "TESL Bundle Extractor", rel, (float)i / jobs.Count))
                    break;

                string sep     = Path.DirectorySeparatorChar.ToString();
                string dstFile = Path.Combine(dst, rel.Replace("/", sep));
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile));

                if (!File.Exists(dstFile))
                {
                    File.Copy(srcFile, dstFile);
                    totalBytes += new FileInfo(dstFile).Length;
                    copied++;
                }
                else skipped++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // 5. Write manifest.txt
        var lines = new List<string>();
        foreach (string f in Directory.GetFiles(dst, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(dst.Length).TrimStart('/', '\\').Replace('\\', '/');
            if (rel != "manifest.txt") lines.Add(rel);
        }
        lines.Sort();
        File.WriteAllLines(Path.Combine(dst, "manifest.txt"), lines.ToArray());

        // 6. Done
        float gb = totalBytes / 1073741824f;
        EditorUtility.DisplayDialog("Done",
            "Extracted: " + copied + " files  (" + gb.ToString("F2") + " GB)\n" +
            "Skipped (already existed): " + skipped + "\n\n" +
            "Output: " + dst + "\n\n" +
            "Upload the entire folder to a static host with CORS.",
            "OK");

        EditorUtility.RevealInFinder(dst);
    }

    static bool IsBundle(string path)
    {
        try
        {
            using (FileStream f = File.OpenRead(path))
            {
                byte[] buf = new byte[7];
                return f.Read(buf, 0, 7) == 7
                    && System.Text.Encoding.ASCII.GetString(buf) == "UnityFS";
            }
        }
        catch { return false; }
    }
}
#endif
