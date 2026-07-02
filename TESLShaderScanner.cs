using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.IO;
#endif

// ─────────────────────────────────────────────────────────────
// РАНТАЙМ-ПРОГРІВ (компілюється У ВСІХ платформах, зокрема у білді!)
// Раніше цей метод був під #if UNITY_EDITOR разом зі сканером, тому
// в білді він не існував → варіанти шейдерів рубашок не прогрівались
// і частина рубашок ставала рожевою. Тепер він завжди активний.
// Використовує лише рантайм-безпечні API (Resources.Load + WarmUp).
// ─────────────────────────────────────────────────────────────
public static class TESLShaderWarmup
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void WarmupShaders()
    {
        var svc = Resources.Load<ShaderVariantCollection>("TESLShaderVariants");
        if (svc != null)
        {
            svc.WarmUp();
            Debug.Log("[TESLViewer] ShaderVariantCollection прогріто: "
                      + svc.variantCount + " варіантів");
        }
        else
        {
            Debug.LogWarning("[TESLViewer] Resources/TESLShaderVariants не знайдено — "
                + "запустіть 'TESL Viewer/Scan TESL Shaders' у Editor перед білдом, "
                + "інакше частина шейдерів рубашок може бути рожевою.");
        }
    }
}

#if UNITY_EDITOR
/// <summary>
/// Збирає ShaderVariantCollection з усіх матеріалів у StreamingAssets.
/// Запустіть TESL Viewer → Scan TESL Shaders перед білдом.
/// </summary>
public class TESLShaderScanner
{
    [MenuItem("TESL Viewer/Scan TESL Shaders (run before build)")]
    static void ScanShaders()
    {
        string saPath = EditorPrefs.GetString("tesl_sa_path", "");
        if (string.IsNullOrEmpty(saPath) || !Directory.Exists(saPath))
        {
            saPath = EditorUtility.OpenFolderPanel(
                "Select game StreamingAssets folder", "", "");
            if (string.IsNullOrEmpty(saPath)) return;
            EditorPrefs.SetString("tesl_sa_path", saPath);
        }

        var svc = new ShaderVariantCollection();
        int count = 0;
        var bundles = new List<AssetBundle>();

        string[] bundleFiles = Directory.GetFiles(saPath, "*", SearchOption.AllDirectories);
        EditorUtility.DisplayProgressBar("Scanning TESL Shaders", "Loading bundles...", 0);

        for (int i = 0; i < bundleFiles.Length; i++)
        {
            string f = bundleFiles[i];
            if (f.EndsWith(".manifest") || f.EndsWith(".meta")) continue;
            EditorUtility.DisplayProgressBar("Scanning TESL Shaders",
                Path.GetFileName(f), (float)i / bundleFiles.Length);
            try
            {
                var bundle = AssetBundle.LoadFromFile(f);
                if (bundle == null) continue;
                bundles.Add(bundle);
                foreach (var mat in bundle.LoadAllAssets<Material>())
                {
                    if (mat == null || mat.shader == null) continue;

                    // 1) Базовий варіант (без keywords)
                    try {
                        var baseVar = new ShaderVariantCollection.ShaderVariant();
                        baseVar.shader = mat.shader;
                        baseVar.passType = PassType.Normal;
                        svc.Add(baseVar); count++;
                    } catch {}

                    // 2) Варіант з активними keywords матеріалу — САМЕ ЦЕ рятує
                    //    анімовані рубашки, чиї варіанти інакше вирізаються у білді.
                    string[] kw = mat.shaderKeywords;
                    if (kw != null && kw.Length > 0)
                    {
                        try {
                            var kwVar = new ShaderVariantCollection.ShaderVariant();
                            kwVar.shader = mat.shader;
                            kwVar.passType = PassType.Normal;
                            kwVar.keywords = kw;
                            svc.Add(kwVar); count++;
                        } catch {}
                    }
                }
            }
            catch {}
        }

        foreach (var b in bundles) b.Unload(true);
        EditorUtility.ClearProgressBar();

        // Save the collection
        const string path = "Assets/Resources/TESLShaderVariants.shadervariants";
        if (!Directory.Exists("Assets/Resources")) Directory.CreateDirectory("Assets/Resources");
        AssetDatabase.CreateAsset(svc, path);
        AssetDatabase.SaveAssets();

        // Set as preloaded (щоб варіанти НЕ вирізались зі збірки)
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (gs != null && gs.Length > 0)
        {
            var so = new SerializedObject(gs[0]);
            var preloaded = so.FindProperty("m_PreloadedShaders");
            if (preloaded != null)
            {
                bool already = false;
                for (int i = 0; i < preloaded.arraySize; i++)
                    if (preloaded.GetArrayElementAtIndex(i).objectReferenceValue == svc)
                    { already = true; break; }
                if (!already)
                {
                    preloaded.InsertArrayElementAtIndex(preloaded.arraySize);
                    preloaded.GetArrayElementAtIndex(preloaded.arraySize - 1).objectReferenceValue = svc;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssets();
                }
            }
        }

        Debug.Log("[TESLViewer] Scanned " + count + " shader variants → " + path);
        EditorUtility.DisplayDialog("Done", "Знайдено " + count + " shader variants.\nЗбережено до " + path + "\n\nТепер робіть білд.", "OK");
    }
}
#endif
