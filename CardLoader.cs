using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// CardLoader — Dual mode:
///   Standalone : baseUrl = "C:/…/StreamingAssets"  → uses file system (original behaviour)
///   WebGL      : baseUrl = "https://pub-xxx.r2.dev" → uses UnityWebRequest + manifest.txt
///
/// Drop-in replacement for CardLoader.cs — same public API.
/// </summary>
public class CardLoader : MonoBehaviour
{
    public static CardLoader Instance { get; private set; }

    // ── Events (unchanged) ───────────────────────────────────
    public System.Action<string> OnLoadStatus;
    public System.Action<float>  OnLoadProgress;
    public System.Action         OnLoadComplete;

    // ── Loaded data (unchanged) ──────────────────────────────
    public List<Material> ArtMaterials  = new List<Material>();
    public List<string>   ArtNames      = new List<string>();
    public List<Material> BackMaterials = new List<Material>();
    public List<string>   BackNames     = new List<string>();

    // ── Exclusions (unchanged) ───────────────────────────────
    private static readonly HashSet<string> EXCLUDED_BACKS = new HashSet<string>
    {
        "tesl_06_002_cb_saint", "tesl_07_001_cb_25thanniversary",
        "tesl_07_rubythrone_card_back", "tesl_07_rubythrone_cb_anim",
        "tesl_07_guildsworn_cb_anim",  "tesl_07_imperial_cb_anim",
        "tesl_07_daggerfall_cb_anim",  "tesl_07_ebonheart_cb_anim",
        "tesl_07_aldmeri_cb_anim",     "tesl_08_001_cb_ancestormoth",
        "tesl_08_002_cb_baandari",     "tesl_08_002_cb_necromancer",
        "tesl_08_002_cb_seasonofthedragon", "tesl_09_001_cb_akatosh",
        "tesl_09_001_cb_tiberseptim",  "tesl_10_001_cb_coldharbour",
        "tesl_10_001_cb_fightersguild","tesl_10_001_cb_magesguild",
        "tesl_10_001_cb_theblades",    "tesl_10_001_cb_thievesguild",
        "tesl_10_001_cb_xanmeer",
        "card_back_atr_01", "card_back_atr_02", "card_back_dbh_01",
        "card_back_eld_01", "card_back_log_01", "card_back_mwd_02",
        "card_back_mwd_03", "card_back_mwd_05", "card_back_mwd_06",
        "card_back_mwd_07", "card_back_newcl_01", "card_back_rnk_01",
        "card_back_mwd_04",
    };

    private List<Material> rawBackMats  = new List<Material>();
    private List<string>   rawBackNames = new List<string>();
    private Texture2D blackTex;
    private int totalBundles = 0;
    private int loadedBundles = 0;
    private readonly List<AssetBundle> _bundleRefs = new List<AssetBundle>();
    // Двофазне завантаження (file-режим): спершу ВСІ бандли в пам'ять, потім
    // витяг матеріалів. Інакше у білді крос-бандл посилання не резолвляться:
    // матеріал рубашки з cp0XX_assets_0 залежить від шейдера (appbase/cardshaders),
    // масок (cp0XX_texture_*) і спільних асетів (cp010/cp011). Якщо витягти матеріал
    // до завантаження цих бандлів, Unity привʼяже порожнє посилання → рожева рубашка.
    private struct PendingBundle { public AssetBundle bundle; public bool arts; public bool backs; public string label; }
    private readonly List<PendingBundle> _pending = new List<PendingBundle>();
    private bool _useHttp;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        blackTex = new Texture2D(1,1);
        blackTex.SetPixel(0,0,Color.clear);
        blackTex.Apply();
    }

    // ── Public entry point (same signature as before) ────────
    public void StartLoading(string baseUrl)
    {
        // Auto-detect: if starts with http → WebGL/web mode
        _useHttp = baseUrl.StartsWith("http://") || baseUrl.StartsWith("https://");
        if (_useHttp)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            StartCoroutine(LoadAllPng(baseUrl.TrimEnd('/')));
#else
            StartCoroutine(LoadAllWeb(baseUrl.TrimEnd('/')));
#endif
        }
        else
        {
            StartCoroutine(LoadAllFile(baseUrl));
        }
    }

    // ════════════════════════════════════════════════════════
    // WEB MODE (WebGL + any HTTP source)
    // ════════════════════════════════════════════════════════
    IEnumerator LoadAllWeb(string root)
    {
        QualitySettings.masterTextureLimit = 0;

        // 1. Download manifest.txt
        OnLoadStatus?.Invoke("Fetching manifest...");
        var mReq = UnityWebRequest.Get(root + "/manifest.txt");
        mReq.timeout = 20;
        yield return mReq.SendWebRequest();

        if (mReq.isNetworkError || mReq.isHttpError)
        {
            OnLoadStatus?.Invoke("❌ manifest.txt not found: " + mReq.error);
            OnLoadComplete?.Invoke();
            yield break;
        }

        // 2. Parse manifest into per-directory file lists
        string[] lines = mReq.downloadHandler.text.Split('\n');
        var byDir = new Dictionary<string, List<string>>(); // dir → [filename, …]
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line == "manifest.txt") continue;
            int slash = line.IndexOf('/');
            if (slash < 0) continue;
            string dir  = line.Substring(0, slash);
            string file = line.Substring(slash + 1);
            if (!byDir.ContainsKey(dir)) byDir[dir] = new List<string>();
            byDir[dir].Add(file);
        }

        // Count total bundles for progress
        totalBundles = 0;
        foreach (var kv in byDir) totalBundles += kv.Value.Count;

        // 3. appbase — backs (shaders/materials for card backs)
        OnLoadStatus?.Invoke("Loading shaders...");
        yield return StartCoroutine(LoadWebDir(root, "appbase", byDir, null, false, true));

        // 4. common000 — shared assets (no arts/backs)
        yield return StartCoroutine(LoadWebDir(root, "common000", byDir, null, false, false));

        // 5. contentpack000 textures first
        yield return StartCoroutine(LoadWebDir(root, "contentpack000", byDir,
            "cardtextures", false, false));

        // 6. contentpack000 arts
        OnLoadStatus?.Invoke("Loading arts cp000...");
        if (!byDir.ContainsKey("contentpack000"))
        {
            OnLoadStatus?.Invoke("❌ contentpack000 missing from manifest");
            OnLoadComplete?.Invoke(); yield break;
        }
        foreach (string fn in byDir["contentpack000"])
        {
            if (fn.StartsWith("cardtextures")) continue;
            yield return StartCoroutine(LoadWebBundle(
                root + "/contentpack000/" + fn, true, false));
        }

        // Collect DLC directories (sorted)
        var dlcDirs = new List<string>();
        foreach (string dir in byDir.Keys)
        {
            string low = dir.ToLower();
            if (low.StartsWith("contentpack") && low != "contentpack000")
                dlcDirs.Add(dir);
        }
        dlcDirs.Sort();

        // 7. PASS 1: ALL DLC textures first (cross-pack dep fix)
        OnLoadStatus?.Invoke("Loading Expansion textures...");
        foreach (string dir in dlcDirs)
        {
            string cp = DlcPrefix(dir);
            yield return StartCoroutine(LoadWebDir(root, dir, byDir,
                cp + "_cardtextures_", false, false));
            yield return StartCoroutine(LoadWebDir(root, dir, byDir,
                cp + "_texture_",      false, false));
        }

        // 8. PASS 2: ALL DLC materials + arts
        OnLoadStatus?.Invoke("Loading Expansion materials...");
        foreach (string dir in dlcDirs)
        {
            string cp = DlcPrefix(dir);
            OnLoadStatus?.Invoke("Materials: " + dir + "...");
            yield return StartCoroutine(LoadWebDir(root, dir, byDir,
                cp + "_assets_",        true, true));
            yield return StartCoroutine(LoadWebDir(root, dir, byDir,
                cp + "_cardmaterials_", true, false));
        }

        // ── Фаза 2: усі бандли завантажені з сервера — тепер витягуємо матеріали
        // (усі крос-бандл залежності вже в памʼяті, як у file-режимі).
        OnLoadStatus?.Invoke("Extracting materials...");
        for (int i = 0; i < _pending.Count; i++)
        {
            var pb = _pending[i];
            yield return StartCoroutine(ExtractMaterials(pb.bundle, pb.arts, pb.backs, pb.label));
        }

        HandleAyrenn();
        DeduplicateBacks();
        OnLoadStatus?.Invoke("Done!");
        OnLoadProgress?.Invoke(1f);
        OnLoadComplete?.Invoke();
    }

    IEnumerator LoadWebDir(string root, string dir,
        Dictionary<string, List<string>> byDir,
        string prefix, bool arts, bool backs)
    {
        if (!byDir.ContainsKey(dir)) yield break;
        foreach (string fn in byDir[dir])
        {
            if (prefix != null && !fn.ToLower().StartsWith(prefix.ToLower())) continue;
            yield return StartCoroutine(LoadWebBundle(root + "/" + dir + "/" + fn, arts, backs));
        }
    }

    IEnumerator LoadWebBundle(string url, bool arts, bool backs)
    {
        var req = UnityWebRequest.GetAssetBundle(url);
        req.timeout = 30;
        yield return req.SendWebRequest();
        loadedBundles++;
        OnLoadProgress?.Invoke((float)loadedBundles / Mathf.Max(1, totalBundles));

        if (req.isNetworkError || req.isHttpError)
        {
            Debug.LogWarning("[CardLoader] HTTP error " + url + " : " + req.error);
            yield break;
        }

        AssetBundle bundle = DownloadHandlerAssetBundle.GetContent(req);
        if (bundle == null)
        {
            Debug.LogWarning("[CardLoader] null bundle: " + url);
            yield break;
        }

        // Двофазність (як у file-режимі): НЕ витягуємо матеріали одразу, а
        // тримаємо бандл і відкладаємо витяг, поки не завантажаться всі залежності
        // (шейдери, маски, крос-DLC асети). Інакше — рожеві рубашки на сервері.
        _bundleRefs.Add(bundle);
        _pending.Add(new PendingBundle {
            bundle = bundle, arts = arts, backs = backs,
            label = System.IO.Path.GetFileName(url)
        });
    }

    // ════════════════════════════════════════════════════════
    // PNG MODE (WebGL — textures served as plain PNG + manifest.json)
    // ════════════════════════════════════════════════════════
    IEnumerator LoadAllPng(string root)
    {
        // Download manifest.json
        OnLoadStatus?.Invoke("Fetching manifest...");
        var mReq = UnityWebRequest.Get(root + "/manifest.json");
        yield return mReq.SendWebRequest();
        if (mReq.isNetworkError || mReq.isHttpError)
        {
            // Fallback to bundle mode if no manifest.json
            Debug.Log("[CardLoader] No manifest.json found, trying bundle mode");
            yield return StartCoroutine(LoadAllWeb(root));
            yield break;
        }

        // Parse JSON manually (no JsonUtility for dynamic arrays in Unity 2017)
        string json = mReq.downloadHandler.text;
        var artEntries  = ParseJsonEntries(json, "arts");
        var backEntries = ParseJsonEntries(json, "backs");

        totalBundles = artEntries.Count + backEntries.Count;
        loadedBundles = 0;

        // Load art textures
        OnLoadStatus?.Invoke("Loading arts...");
        Material unlitMat = new Material(Shader.Find("Unlit/Texture"));
        foreach (var entry in artEntries)
        {
            string url = root + "/" + entry[1]; // entry[0]=name, entry[1]=file
            var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            loadedBundles++;
            OnLoadProgress?.Invoke((float)loadedBundles / Mathf.Max(1, totalBundles));
            if (req.isNetworkError || req.isHttpError) { Debug.LogWarning("[PNG] " + url); continue; }
            Texture2D tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null) continue;
            Material mat = new Material(unlitMat);
            mat.mainTexture = tex;
            ArtMaterials.Add(mat);
            ArtNames.Add(entry[0]);
        }

        // Load back textures
        OnLoadStatus?.Invoke("Loading backs...");
        foreach (var entry in backEntries)
        {
            string url = root + "/" + entry[1];
            var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            loadedBundles++;
            OnLoadProgress?.Invoke((float)loadedBundles / Mathf.Max(1, totalBundles));
            if (req.isNetworkError || req.isHttpError) continue;
            Texture2D tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null) continue;
            Material mat = new Material(unlitMat);
            mat.mainTexture = tex;
            BackMaterials.Add(mat);
            BackNames.Add(entry[0]);
        }

        OnLoadStatus?.Invoke("Done!");
        OnLoadProgress?.Invoke(1f);
        OnLoadComplete?.Invoke();
    }

    // Minimal JSON parser for [{"name":"x","file":"y"}, ...] arrays
    static List<string[]> ParseJsonEntries(string json, string arrayKey)
    {
        var result = new List<string[]>();
        int arrStart = json.IndexOf("\"" + arrayKey + "\"");
        if (arrStart < 0) return result;
        arrStart = json.IndexOf('[', arrStart);
        int arrEnd = json.IndexOf(']', arrStart);
        if (arrStart < 0 || arrEnd < 0) return result;
        string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

        // Each entry: {"name":"...","file":"..."}
        int pos = 0;
        while (pos < arr.Length)
        {
            int nb = arr.IndexOf("\"name\"", pos); if (nb < 0) break;
            int nv1 = arr.IndexOf('"', nb + 6); if (nv1 < 0) break;
            int nv2 = arr.IndexOf('"', nv1 + 1); if (nv2 < 0) break;
            string name = arr.Substring(nv1 + 1, nv2 - nv1 - 1);

            int fb = arr.IndexOf("\"file\"", nv2); if (fb < 0) break;
            int fv1 = arr.IndexOf('"', fb + 6); if (fv1 < 0) break;
            int fv2 = arr.IndexOf('"', fv1 + 1); if (fv2 < 0) break;
            string file = arr.Substring(fv1 + 1, fv2 - fv1 - 1);

            result.Add(new string[] { name, file });
            pos = fv2 + 1;
        }
        return result;
    }

        // ════════════════════════════════════════════════════════
    // FILE MODE (Standalone — original behaviour preserved)
    // ════════════════════════════════════════════════════════
    IEnumerator LoadAllFile(string sa)
    {
        QualitySettings.masterTextureLimit = 0;

        // Count files for progress
        totalBundles = 0;
        if (Directory.Exists(sa + "/appbase"))
            totalBundles += Directory.GetFiles(sa + "/appbase").Length;
        if (Directory.Exists(sa + "/common000"))
            totalBundles += Directory.GetFiles(sa + "/common000").Length;
        foreach (string d in Directory.GetDirectories(sa))
            totalBundles += Directory.GetFiles(d).Length;

        OnLoadStatus?.Invoke("Loading shaders...");
        yield return StartCoroutine(LoadFileDir(sa+"/appbase",   null, false, true));
        yield return StartCoroutine(LoadFileDir(sa+"/common000", null, false, false));
        yield return StartCoroutine(LoadFileDir(sa+"/contentpack000", "cardtextures", false, false));

        OnLoadStatus?.Invoke("Loading arts cp000...");
        string cp000 = sa+"/contentpack000";
        if (!Directory.Exists(cp000))
        {
            OnLoadStatus?.Invoke("❌ Folder not found: " + cp000);
            OnLoadComplete?.Invoke(); yield break;
        }
        string[] _cp0f=Directory.GetFiles(cp000);System.Array.Sort(_cp0f,System.StringComparer.OrdinalIgnoreCase);
        foreach(string p in _cp0f)
        {
            string fn = Path.GetFileName(p);
            if (!string.IsNullOrEmpty(Path.GetExtension(p))) continue;
            if (fn.StartsWith("cardtextures") || !IsBundle(p)) continue;
            yield return StartCoroutine(LoadFileBundle(p, true, false));
        }

        OnLoadStatus?.Invoke("Loading Expansion textures...");
        var _ds=new System.Collections.Generic.List<string>(Directory.GetDirectories(sa));_ds.Sort(System.StringComparer.OrdinalIgnoreCase);
        foreach(string dir in _ds)
        {
            string dn=Path.GetFileName(dir).ToLower();
            if(!dn.StartsWith("contentpack")||dn=="contentpack000")continue;
            string cp=DlcPrefix(dn);
            yield return StartCoroutine(LoadFileDir(dir, cp+"_cardtextures_", false, false));
            yield return StartCoroutine(LoadFileDir(dir, cp+"_texture_",      false, false));
        }

        OnLoadStatus?.Invoke("Loading Expansion materials...");
        foreach (string dir in _ds)
        {
            string dn = Path.GetFileName(dir).ToLower();
            if (!dn.StartsWith("contentpack") || dn == "contentpack000") continue;
            string cp = DlcPrefix(dn);
            OnLoadStatus?.Invoke("Materials: " + dn + "...");
            yield return StartCoroutine(LoadFileDir(dir, cp+"_assets_",        true, true));
            yield return StartCoroutine(LoadFileDir(dir, cp+"_cardmaterials_", true, false));
        }

        // ── Фаза 2: усі бандли завантажені — тепер витягуємо матеріали.
        // На цей момент шейдери, маски й спільні асети (cp010/cp011) вже в памʼяті,
        // тож Unity коректно резолвить крос-бандл посилання (як в Editor).
        OnLoadStatus?.Invoke("Extracting materials...");
        for (int i = 0; i < _pending.Count; i++)
        {
            var pb = _pending[i];
            yield return StartCoroutine(ExtractMaterials(pb.bundle, pb.arts, pb.backs, pb.label));
        }

        HandleAyrenn();
        DeduplicateBacks();
        OnLoadStatus?.Invoke("Done!");
        OnLoadProgress?.Invoke(1f);
        OnLoadComplete?.Invoke();
    }

    IEnumerator LoadFileDir(string dir, string prefix, bool arts, bool backs)
    {
        if (!Directory.Exists(dir)) yield break;
        string[] _fps=Directory.GetFiles(dir);
        System.Array.Sort(_fps,System.StringComparer.OrdinalIgnoreCase);
        foreach (string p in _fps)
        {
            if (!string.IsNullOrEmpty(Path.GetExtension(p))) continue;
            string fn = Path.GetFileName(p);
            if (prefix != null && !fn.StartsWith(prefix)) continue;
            yield return StartCoroutine(LoadFileBundle(p, arts, backs));
        }
    }

    IEnumerator LoadFileBundle(string path, bool arts, bool backs)
    {
        if (!IsBundle(path)) yield break;
        var req = AssetBundle.LoadFromFileAsync(path); yield return req;
        loadedBundles++;
        OnLoadProgress?.Invoke((float)loadedBundles / Mathf.Max(1, totalBundles));
        if (req.assetBundle == null)
        {
            Debug.LogWarning("[CardLoader] null bundle: " + Path.GetFileName(path));
            yield break;
        }
        _bundleRefs.Add(req.assetBundle);
        // НЕ витягуємо матеріали одразу — відкладаємо до завантаження ВСІХ бандлів
        // (фаза 2 у LoadAllFile), щоб крос-бандл залежності вже були в памʼяті.
        _pending.Add(new PendingBundle {
            bundle = req.assetBundle, arts = arts, backs = backs,
            label = Path.GetFileName(path)
        });
    }

    // ════════════════════════════════════════════════════════
    // SHARED: material extraction (identical for both modes)
    // ════════════════════════════════════════════════════════
    IEnumerator ExtractMaterials(AssetBundle bundle, bool arts, bool backs, string label)
    {
        var all = bundle.LoadAllAssetsAsync(); yield return all;
        int a_ = 0, b_ = 0;
        foreach (string a in bundle.GetAllAssetNames())
        {
            bool isArt  = arts  && IsCardArtMat(a);
            bool isBack = backs && IsCardBackMat(a);
            if (!isArt && !isBack) continue;
            Material mat = bundle.LoadAsset<Material>(a); if (mat == null) continue;
            string name = Path.GetFileNameWithoutExtension(a);
            if (isArt)  { CleanMat(mat); ArtMaterials.Add(mat); ArtNames.Add(name); a_++; }
            if (isBack) { rawBackMats.Add(mat); rawBackNames.Add(name); b_++; }
        }
        if (a_ + b_ > 0)
            Debug.Log("[mat] " + label + " arts=" + a_ + " backs=" + b_);
    }

    // ════════════════════════════════════════════════════════
    // FILTERS & HELPERS (unchanged from original)
    // ════════════════════════════════════════════════════════
    static bool IsBundle(string p)
    {
        try {
            byte[] h = new byte[7];
            using (var f = File.OpenRead(p)) f.Read(h,0,7);
            return System.Text.Encoding.ASCII.GetString(h).StartsWith("UnityFS");
        } catch { return false; }
    }

    static bool IsCardArtMat(string a)
    {
        string low   = a.ToLower();
        string fnLow = Path.GetFileNameWithoutExtension(a).ToLower();
        // Exclude card backs even if they contain "premium" in filename/path
        if (fnLow.Contains("_cb_"))      return false;
        if (fnLow.Contains("card_back")) return false;
        return low.Contains("premium") && low.EndsWith(".mat")
            && !low.Contains("cardback") && !low.Contains("board")
            && !low.Contains("environ") && !low.Contains("stencil")
            && !low.Contains("/ui/")
            && !low.Contains("cardpack_premium_lock");
    }

    static bool IsCardBackMat(string a)
    {
        if (!a.ToLower().EndsWith(".mat")) return false;
        string low   = a.ToLower();
        string fnLow = Path.GetFileNameWithoutExtension(a).ToLower();
        if (low.Contains("cardback"))    return true;
        if (fnLow.Contains("_cb_"))      return true;
        if (fnLow.Contains("card_back")) return true;
        return false;
    }

    static bool IsAnimatedBack(string n)
    {
        string low = n.ToLower();
        return low.Contains("_anim") || low.Contains("_premium")
            || low.Contains("_animated") || low.EndsWith("premium");
    }

    static string BackBaseName(string name)
    {
        string low = name.ToLower()
            .Replace("_premium","").Replace("_anim","").Replace("_animated","").TrimEnd('_');
        if (low.EndsWith("premium"))
            low = low.Substring(0, low.Length - "premium".Length).TrimEnd('_');
        if (low.EndsWith("anim"))
            low = low.Substring(0, low.Length - "anim".Length).TrimEnd('_');
        return low;
    }

    static string DlcPrefix(string dirName)
    {
        return "cp" + dirName.ToLower().Replace("contentpack","").PadLeft(3,'0');
    }

    void HandleAyrenn()
    {
        int idx = -1;
        for (int i = 0; i < ArtNames.Count; i++)
        {
            string low = ArtNames[i].ToLower();
            if (low.EndsWith("ayrenn_premium") && !low.Contains("queen"))
            { idx = i; break; }
        }
        if (idx < 0) return;
        ArtNames[idx] += " [GAME-BUG]";
        Texture2D oldArt  = FindTex("ayrenn");
        Texture2D oldMask = FindTex("ayrenn_mask");
        if (oldArt == null && oldMask == null) return;
        Material orig = new Material(ArtMaterials[idx]);
        if (oldArt  != null) orig.SetTexture("_ImageTex",  oldArt);
        if (oldMask != null) orig.SetTexture("_ImageMask", oldMask);
        string baseName = ArtNames[idx].Replace(" [GAME-BUG]","");
        ArtMaterials.Insert(idx+1, orig);
        ArtNames.Insert(idx+1, baseName + " [ORIGINAL]");
    }

    static Texture2D FindTex(string name)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    // Тепер, коли двофазне завантаження резолвить крос-бандл посилання,
    // анімовані рубашки працюють і в білді. Лишаємо true скрізь.
    // Якщо десь анімована все одно рожева (залежність поза ігровою текою) —
    // постав false, і для таких рубашок візьметься статична версія (без анімації).
    const bool PREFER_ANIMATED_BACKS = true;

    void DeduplicateBacks()
    {
        var animIdx   = new Dictionary<string, int>();
        var staticIdx = new Dictionary<string, int>();
        var order     = new List<string>();          // детермінований порядок появи

        for (int i = 0; i < rawBackMats.Count; i++)
        {
            string nameLow = rawBackNames[i].ToLower();
            if (EXCLUDED_BACKS.Contains(nameLow)) continue;
            string baseName = BackBaseName(rawBackNames[i]);
            if (!animIdx.ContainsKey(baseName) && !staticIdx.ContainsKey(baseName))
                order.Add(baseName);
            if (IsAnimatedBack(rawBackNames[i]))
            { if (!animIdx.ContainsKey(baseName))   animIdx[baseName]   = i; }
            else
            { if (!staticIdx.ContainsKey(baseName)) staticIdx[baseName] = i; }
        }

        foreach (var baseName in order)
        {
            bool hasAnim   = animIdx.ContainsKey(baseName);
            bool hasStatic = staticIdx.ContainsKey(baseName);
            int matIdx, nameIdx;

            if (PREFER_ANIMATED_BACKS)
            {
                matIdx = nameIdx = hasAnim ? animIdx[baseName] : staticIdx[baseName];
            }
            else if (hasStatic && hasAnim)
            {
                // Білд: рендеримо СТАТИЧНИЙ матеріал (без рожевого FX/UVPanUI),
                // але назву лишаємо від АНІМОВАНОЇ версії — інакше зламається
                // пошук назви/колекції у viewer_config (там ключі з _anim/_premium).
                matIdx  = staticIdx[baseName];
                nameIdx = animIdx[baseName];
            }
            else if (hasStatic)
            {
                matIdx = nameIdx = staticIdx[baseName];
            }
            else
            {
                // Лише анімована версія — статичної пари немає. Лишаємо як є;
                // якщо це UV-pan рубашка, у білді вона може бути рожевою.
                matIdx = nameIdx = animIdx[baseName];
                Debug.LogWarning("[Backs] '" + rawBackNames[animIdx[baseName]]
                    + "': статичної пари немає — у білді можлива рожева (шейдер лише в бандлі).");
            }

            BackMaterials.Add(rawBackMats[matIdx]);
            BackNames.Add(rawBackNames[nameIdx]);
        }
    }

    void CleanMat(Material m)
    {
        m.SetTexture("_ColorMask", blackTex); m.SetTexture("_MaskTexture", blackTex);
        Color c = Color.clear;
        foreach (string p in new[]{"_CardColor","_CardColor2","_CardColor3",
                                   "_CardColor4","_CardColor5","_EdgeColor"})
            try { m.SetColor(p, c); } catch {}
        m.DisableKeyword("MULTICOLOR_CARD_ON"); m.DisableKeyword("INNER_BORDER_ON");
    }
}
