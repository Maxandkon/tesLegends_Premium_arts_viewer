#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Налаштування збірки TES: Legends Art Viewer.
/// Запустіть TESL Viewer → Configure Build перед компіляцією.
/// </summary>
public class TESLViewerBuildSetup : IActiveBuildTargetChanged
{
    public int callbackOrder => 0;

    // ─── Автоматично при зміні target ────────────────────────────────────
    public void OnActiveBuildTargetChanged(BuildTarget prev, BuildTarget next)
        => Configure(quiet: true);

    // ─── Ручний запуск через меню ────────────────────────────────────────
    [MenuItem("TESL Viewer/Configure Build Settings")]
    public static void ConfigureMenu() => Configure(quiet: false);

    static void Configure(bool quiet)
    {
        // 1. Collect shaders that should always be included
        var toInclude = new []
        {
            "Standard",
            "Standard (Specular setup)",
            "Unlit/Texture",
            "Unlit/Color",
            "Sprites/Default",
            "Legacy Shaders/Diffuse",
            "Legacy Shaders/Transparent/Diffuse",
            "UI/Default",
        };

        // Add to always-included via SerializedObject
        var graphicsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (graphicsAsset != null && graphicsAsset.Length > 0)
        {
            var so = new SerializedObject(graphicsAsset[0]);
            var shaderArr = so.FindProperty("m_AlwaysIncludedShaders");
            if (shaderArr != null)
            {
                foreach (var name in toInclude)
                {
                    var s = Shader.Find(name);
                    if (s == null) continue;
                    // Check if already added
                    bool found = false;
                    for (int i = 0; i < shaderArr.arraySize; i++)
                    {
                        var el = shaderArr.GetArrayElementAtIndex(i);
                        if (el.objectReferenceValue == s) { found = true; break; }
                    }
                    if (!found)
                    {
                        shaderArr.InsertArrayElementAtIndex(shaderArr.arraySize);
                        shaderArr.GetArrayElementAtIndex(shaderArr.arraySize - 1).objectReferenceValue = s;
                    }
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
        }

        // 2. Remind about Resources/Collections
        bool collectionsOk = System.IO.Directory.Exists("Assets/Resources/Collections");
        bool ffmpegOk      = System.IO.File.Exists("Assets/StreamingAssets/Tools/ffmpeg.exe");

        if (!quiet)
        {
            string msg = "TESL Viewer Build Setup\n\n";
            msg += collectionsOk
                ? "✓ Assets/Resources/Collections/ — знайдено\n"
                : "✗ Assets/Resources/Collections/ — ВІДСУТНЯ!\n  Скопіюйте .json з гри: StreamingAssets/collections/\n  → Assets/Resources/Collections/\n\n";
            msg += ffmpegOk
                ? "✓ Assets/StreamingAssets/Tools/ffmpeg.exe — знайдено\n"
                : "⚠ Assets/StreamingAssets/Tools/ffmpeg.exe — відсутній (запис лише AVI)\n  Завантажте: gyan.dev/ffmpeg/builds → release essentials → ffmpeg.exe\n\n";
            msg += "\n✓ Shaders added to Always Included list.\n\nГотово!";
            EditorUtility.DisplayDialog("TESL Viewer Build Setup", msg, "OK");
        }

        // 3. Force D3D11 only — TESL shaders compiled for D3D11
        //    Without this, Auto Graphics API may use OpenGL → pink backs
        try
        {
            var apisDX11 = new UnityEngine.Rendering.GraphicsDeviceType[]
                { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 };
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows,   apisDX11);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, apisDX11);
            PlayerSettings.colorSpace = ColorSpace.Gamma; // match TESL
            if (!quiet) Debug.Log("[TESLViewer] Graphics API forced to D3D11, Color Space = Gamma");
        }
        catch (System.Exception ex) { Debug.LogWarning("[TESLViewer] Could not set graphics API: " + ex.Message); }

        if (!quiet)
            Debug.Log("[TESLViewer] Build setup completed. Collections: " + collectionsOk + ", FFmpeg: " + ffmpegOk);
    }

    // ─── Перевірка перед білдом ───────────────────────────────────────────
    [UnityEditor.Callbacks.PostProcessScene]
    static void OnPostProcessScene()
    {
        if (!BuildPipeline.isBuildingPlayer) return;
        Configure(quiet: true);
    }
}
#endif
