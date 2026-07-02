using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Головний контролер застосунку.
/// Прикріпити до Main Camera — весь UI будується програмно.
/// </summary>
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(CardLoader))]
[RequireComponent(typeof(CardRecorder))]
public class TESLApp : MonoBehaviour
{
    // ── Config ─────────────────────────────────────────
    const string R2_URL = "https://pub-6184c4e7ce5e49a9a148b9e6d2462ff1.r2.dev";
    [Header("StreamingAssets path (leave empty = auto)")]
    public string streamingAssetsPath = "";

    // ── UV crop constants (Card.obj) ──────────────────────────
    const float ART_U_MIN = 0.270834f, ART_U_MAX = 0.733663f;
    const float ART_V_MIN = 0.103157f, ART_V_MAX = 0.908409f;
    const float ART_ASPECT = 0.5748f; // (U_MAX-U_MIN)/(V_MAX-V_MIN)
    // Card back outer UV boundary from Card_back.obj (outer edge of decorative ring)
    const float BACK_U_MIN = 0.270834f; // from Card_back.obj inner ring
    const float BACK_U_MAX = 0.733663f;
    const float BACK_V_MIN = 0.098250f;
    const float BACK_V_MAX = 0.913314f;

    // ── Colors — warm parchment/amber TES theme ─────────────
    static readonly Color COL_BG        = new Color(0.039f, 0.047f, 0.059f, 1f);
    static readonly Color COL_PANEL     = new Color(0.055f, 0.067f, 0.082f, 1f);
    static readonly Color COL_PANEL2    = new Color(0.071f, 0.086f, 0.106f, 1f);
    static readonly Color COL_BORDER    = new Color(0.471f, 0.353f, 0.133f, 1f);
    static readonly Color COL_ACCENT    = new Color(0.741f, 0.573f, 0.220f, 1f);
    static readonly Color COL_ACCENT_BR = new Color(0.898f, 0.725f, 0.318f, 1f);
    static readonly Color COL_TEXT      = new Color(0.906f, 0.863f, 0.784f, 1f);
    static readonly Color COL_TEXT_DIM  = new Color(0.549f, 0.475f, 0.325f, 1f);
    static readonly Color COL_SELECT    = new Color(0.471f, 0.353f, 0.133f, 0.45f);  // #7A5219 brighter selection
    static readonly Color COL_BTN_ON    = new Color(0.510f, 0.360f, 0.120f);  // #825C1F active
    static readonly Color COL_BTN_OFF   = new Color(0.190f, 0.145f, 0.090f);  // #302517 inactive
    static readonly Color COL_RED       = new Color(0.600f, 0.100f, 0.100f);  // #991A1A
    static readonly Color COL_GREEN     = new Color(0.200f, 0.500f, 0.150f);  // #338026

    // ── State ─────────────────────────────────────────────────
    private Camera           cam;
    private CardLoader       loader;
    private CardRecorder     recorder;
    private CardCollection   collection = new CardCollection();
    private ViewerConfig     viewerConfig = new ViewerConfig();
    private List<string>     backMaterialKeys;  // original material keys (for AssetBundle loading)
    private List<string>     displayBackNames;  // display names after ApplyBackNames

    private bool   isEnglish = true;  // English only
    private Text   langBtnText;
    private Image selectedItemBg;
    private float searchDebounce = 0f;
    private List<Image>  listItemImages  = new List<Image>();
    private List<string> listItemSections = new List<string>(); // for keyboard nav highlight
    private Dictionary<string, List<GameObject>> sectionItems = new Dictionary<string, List<GameObject>>();
    private ScrollRect   listScrollRect;  // delay before rebuilding list on search  // currently highlighted list item
    private Image[]    backMaskStrips;  // 4 strips for card back crop mask
    private Image         centerPanelBg;
    private RectTransform centerPanelRT; // PNG-based crop mask overlay

    // Translate helper — use as T("Ukrainian", "English")
    string T(string ua, string en) => isEnglish ? en : ua;

    private bool   showBacks = false;
    private Dictionary<string, bool> collapsedArts  = new Dictionary<string, bool>();
    private Dictionary<string, bool> collapsedBacks = new Dictionary<string, bool>();
    private bool   cropBacks = false;
    private int    curIndex  = -1;
    private string searchFilter = "";

    private List<CardCollection.CardEntry> filteredArts  = new List<CardCollection.CardEntry>();
    private List<string>                   filteredBacks = new List<string>();

    private GameObject quadObj;
    private RenderTexture previewRT;
    private int rtWidth, rtHeight;

    // ── UI references (populated in Build) ───────────────────
    private Text           loadingLabel;
    private GameObject     loadingPanel;
    private GameObject     mainPanel;

    private Button         btnArts, btnBacks;
    private InputField     searchField;
    private Toggle         cropToggle;
    private GameObject     cropRow;
    private GameObject     _artsInfoRow;
    private RectTransform  listContent;
    private RawImage       previewImage;
    private Text           previewCardName;

    // Right panel
    private InputField     resField, durField, outField;
    private GameObject     renamePanel;
    private InputField     renameField;
    private CardCollection.CardEntry renameTarget;
    private Button         recordBtn;
    private RectTransform  _leftPanelRT;
    private GameObject     _recBlockOverlay;
    private bool           _inBatch;
    private UnityEngine.UI.Image _recDimImg;
    private Coroutine      _dimCo;
    private Text           recordStatus;

    // ────────────────────────────────────────────────────────
    void Awake()
    {
        cam      = GetComponent<Camera>();
        loader   = GetComponent<CardLoader>();
        recorder = GetComponent<CardRecorder>();

        if (string.IsNullOrEmpty(streamingAssetsPath))
            streamingAssetsPath = Application.streamingAssetsPath;
    }

    void Start()
    {
        Application.targetFrameRate = 60;
        Screen.SetResolution(1920, 1080, true);
        BuildUI();
        HookEvents();
        // Loading started by button click in loading panel
    }

    // ══════════════════════════════════════════════════════════
    // EVENT HOOKS
    // ══════════════════════════════════════════════════════════
    // ── Load card back mask PNG ───────────────────────────────
        Dictionary<string, bool> collapsed { get { return showBacks ? collapsedBacks : collapsedArts; } }

    // ── Rotating tips ─────────────────────────────────────────
    static readonly string[][] TIPS_ALL = new string[][] {
        new string[] { // UA
            "Старі арти мають роздільність 512 — це не проблема вашого зору, вони дійсно виглядають мильними.",
            "Деякі Сюжетні арти можуть не мати анімації, але ми притримуємось ідеї No Art Left Behind.",
            "Деякі арти можуть повторно з'явитися у Unused — бо це дійсно Unused копія, що є у файлах.",
            "Знайшли помилку? Можете звернутися до Maxandkon... але можете і не звертатися.",
            "Так, Pony Guar дійсно має посилання на арті — але це просто китайський сайт стокових фото.",
            "Якщо у вас є найраніша Beta версія Legends, повідомте Maxandkon будь ласка.",
        },
        new string[] { // EN
            "Old arts have resolution of 512x512; that's not your eyesight, they really are blurry.",
            "Some Story arts may not have animation, but we follow the No Art Left Behind policy.\n\nApart of Memory Wand, which doesn't have a premium version, and so not apear in the list... ",
            "Arts do reappear in Unused; these are actual Unused extra-variations that exist within the game data.",
            "Animation stuck after recording? Just click on another art.",
            "Found a bug? You may contact Maxandkon... or you may not.",
            "Yeah, Pony Guar really does have a link on its art; merely some Chinese stock photo site... fun fact: in older versions, the link was cut.",
            "Did you know that the narrator Kellen is a non-blind Moth Priest who casually carries the Elder Scrolls with him? Unfortunately, his personal story was never added to the game.",
            "You can learn more here: https://en.uesp.net/wiki/Legends:Kellen",
            "If you have the earliest Beta version of Legends, please contact me.",
            "Bethesda's tech support might point you to my pages about Castles... but they never gifted me an egg with legs in ESO.",
            "This could have been your ads... or mine.",
            "As a professional localiser, I can confidently say that translating Elder Scrolls is a real pain, as the developers aren't consistent in the logic behind the structure of in-universe language.",
            "-the signpainter was here too.",
            "They once promised an art book for Legends; wonder if that’s the reason behind the usurious contracts most outsourcers are forced to sign; they aren’t even allowed to mention that they worked on this game.",
            "Did you know that there was few different versions of Legends:\nthe Dire version;\nthe Sparky version;\nthe Chinese version;\nthe TBT version;\nthe Asian version;\nand the Asian re-release.",
        },
    };
    static string[] GetTips(bool en) { return TIPS_ALL[en?1:0]; }
    int _tipIdx = 0;
    Text _tipText;
    Text _loadingTipText;
    GameObject _loadingNotesWrapper;
    Canvas _canvas;
    int _lastScreenW = -1, _lastScreenH = -1;
    GameObject _loadingStartBg;
    Coroutine _tipCoroutine;

    IEnumerator RotateTips()
    {
        while (true)
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(15f, 20f));
            var tips = GetTips(isEnglish); _tipIdx = (_tipIdx + 1) % tips.Length;
            if (_tipText != null) _tipText.text = tips[_tipIdx];
            if (_loadingTipText != null) _loadingTipText.text = tips[_tipIdx];
        }
    }

    void ShowTip(int delta)
    {
        var tips = GetTips(isEnglish); _tipIdx = (((_tipIdx + delta) % tips.Length) + tips.Length) % tips.Length;
        if (_tipText != null) _tipText.text = tips[_tipIdx];
        if (_loadingTipText != null) _loadingTipText.text = tips[_tipIdx];
        // Ручне перемикання скидає таймер авто-ротації (інакше наступна авто-зміна
        // могла статися майже одразу після ручної).
        if (_tipCoroutine != null) { StopCoroutine(_tipCoroutine); _tipCoroutine = StartCoroutine(RotateTips()); }
    }

    bool _isDragToggling = false; // set true while mouse/touch held during drag

    // Click immediately toggles + hold-and-drag toggles headers as pointer passes over them
    void SetupDragToggle(GameObject headerGo, System.Action onToggle)
    {
        var btn = headerGo.GetComponent<Button>();
        if (btn != null) btn.onClick.RemoveAllListeners();

        var et = headerGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();

        // PointerDown: toggle immediately and start drag mode
        var down = new UnityEngine.EventSystems.EventTrigger.Entry();
        down.eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown;
        down.callback.AddListener(_ => { _isDragToggling = true; onToggle(); });
        et.triggers.Add(down);

        // PointerUp: end drag mode
        var up = new UnityEngine.EventSystems.EventTrigger.Entry();
        up.eventID = UnityEngine.EventSystems.EventTriggerType.PointerUp;
        up.callback.AddListener(_ => _isDragToggling = false);
        et.triggers.Add(up);

        // PointerEnter: if dragging, toggle this header too
        var enter = new UnityEngine.EventSystems.EventTrigger.Entry();
        enter.eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter;
        enter.callback.AddListener(_ => { if (_isDragToggling) onToggle(); });
        et.triggers.Add(enter);
    }

    static Font _fontRegular, _fontBold, _fontItalic, _fontRubik;
    static Font GetFont(bool bold = false, bool italic = false)
    {
        if (bold)
        {
            if (_fontBold == null) _fontBold = Resources.Load<Font>("Fonts/Merriweather-Bold");
            if (_fontBold == null) _fontBold = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _fontBold;
        }
        if (italic)
        {
            if (_fontItalic == null) _fontItalic = Resources.Load<Font>("Fonts/Merriweather-Italic");
            if (_fontItalic == null) _fontItalic = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _fontItalic;
        }
        if (_fontRegular == null) _fontRegular = Resources.Load<Font>("Fonts/Merriweather-Regular");
        if (_fontRegular == null) _fontRegular = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return _fontRegular;
    }

    static Font GetRubikFont()
    {
        if (_fontRubik != null) return _fontRubik;
        _fontRubik = Resources.Load<Font>("Fonts/Rubik-Medium");
        if (_fontRubik == null) _fontRubik = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return _fontRubik;
    }

    void HookEvents()
    {
        loader.OnLoadStatus = msg =>
        {
            if (loadingLabel) loadingLabel.text = msg;
        };
        loader.OnLoadProgress = p =>
        {
        };
        loader.OnLoadComplete = () =>
        {
            viewerConfig.Load();
            collection.Build(loader.ArtNames, streamingAssetsPath);
            viewerConfig.ApplyToCollection(collection);
            // Rebuild order explicitly using DEFAULT
            ApplyDefaultCollectionOrder(collection);
            // Post-config redirect: move Festival overflow to Isle of Madness.
            // Передаємо І сиру, І відображувану назву — фільтр спрацює незалежно
            // від того, чи встиг ApplyToCollection перейменувати колекцію.
            ApplyCollectionFilter(collection,
                srcNames:  new string[]{"Festival of Madness", "Festival_Of_Madness"},
                keepMats:   new string[]{"crdl_06_105_cruel_cheesemancer_premium",
                                         "crdl_06_040_sheogorath_premium"},
                targetNames: new string[]{"Isle of Madness", "Isles_Of_Madness"});
            backMaterialKeys = new List<string>(loader.BackNames); // original keys for loading
            displayBackNames  = new List<string>(loader.BackNames); // copy for display
            viewerConfig.ApplyBackNames(displayBackNames); // modify only display copy
            viewerConfig.GenerateTemplate(collection, loader.BackNames);
            RefreshList();
            if (curIndex < 0 && CurrentCount > 0)
                ShowCard(0);
            if (_loadingNotesWrapper != null) _loadingNotesWrapper.SetActive(false);
            loadingPanel.SetActive(false);
            mainPanel.SetActive(true);
        };
        recorder.OnRecordProgress = (p, status) =>
        {
            if (recordBtn) recordBtn.GetComponentInChildren<Text>().text = status;
            if (recordStatus) recordStatus.text = status;
        };
        if (_maskRawImage != null) recorder.MaskImageToHide = _maskRawImage;

        // Input-blocking overlay during recording
        _recBlockOverlay = new GameObject("RecBlock");
        _recBlockOverlay.transform.SetParent(FindObjectOfType<Canvas>().transform, false);
        var _rboRT = _recBlockOverlay.AddComponent<UnityEngine.RectTransform>();
        _rboRT.anchorMin = Vector2.zero; _rboRT.anchorMax = Vector2.one;
        _rboRT.offsetMin = _rboRT.offsetMax = Vector2.zero;
        // Повноекранний блокер вводу лишається прозорим (щоб перемикання арта
        // під час запису не крашило файл), а затемнення — лише в зоні арта.
        _recBlockOverlay.AddComponent<UnityEngine.UI.Image>().color = new Color(0,0,0,0.01f);
        {
            var _dim = new GameObject("RecDimArt");
            _dim.transform.SetParent(_recBlockOverlay.transform, false);
            var _dimRT = _dim.AddComponent<UnityEngine.RectTransform>();
            _dimRT.anchorMin = new Vector2(0,0); _dimRT.anchorMax = new Vector2(1,1);
            // Ті самі відступи, що й центральна панель арта (LP=280, RP=240)
            _dimRT.offsetMin = new Vector2(280, 0); _dimRT.offsetMax = new Vector2(-240, 0);
            _recDimImg = _dim.AddComponent<UnityEngine.UI.Image>();
            _recDimImg.color = new Color(0,0,0,0f); // старт прозорий → плавне наростання
        }
        _recBlockOverlay.SetActive(false);

        recorder.OnRecordDone = folder =>
        {
            if (_recBlockOverlay && !_inBatch) _recBlockOverlay.SetActive(false);
            if (recordBtn) recordBtn.GetComponentInChildren<Text>().text = "● Record";
            if (recordStatus) recordStatus.text = "Saved:\n" + folder;
            if (recordBtn) recordBtn.colors = ColorBlock("start");
            if (curIndex >= 0) ShowCard(curIndex); // refresh animation
        };
        recorder.OnRecordError = err =>
        {
            if (_recBlockOverlay && !_inBatch) _recBlockOverlay.SetActive(false);
            if (recordStatus) recordStatus.text = "Error: " + err;
        };
    }

    // ══════════════════════════════════════════════════════════
    // CARD DISPLAY
    // ══════════════════════════════════════════════════════════
    List<object> GetCurrentList()
    {
        // returns opaque list — use index via ShowCard(i)
        return null; // not used directly
    }

    int CurrentCount =>
        showBacks ? filteredBacks.Count : filteredArts.Count;

    void ShowRecordAllConfirm()
    {
        var overlay = new GameObject("ConfirmOverlay");
        overlay.transform.SetParent(FindObjectOfType<Canvas>().transform, false);
        var oRT = overlay.AddComponent<RectTransform>(); oRT.anchorMin=Vector2.zero; oRT.anchorMax=Vector2.one; oRT.offsetMin=oRT.offsetMax=Vector2.zero;
        SetBG(overlay, new Color(0,0,0,0.75f));
        var dlg = new GameObject("Dlg"); dlg.transform.SetParent(overlay.transform, false);
        var dlgR = dlg.AddComponent<RectTransform>(); dlgR.anchorMin=new Vector2(0.15f,0.25f); dlgR.anchorMax=new Vector2(0.85f,0.75f); dlgR.offsetMin=dlgR.offsetMax=Vector2.zero;
        SetBG(dlg, new Color(0.07f,0.055f,0.115f)); dlg.AddComponent<Outline>().effectColor = COL_BORDER;
        var msgGo = new GameObject("M"); msgGo.transform.SetParent(dlg.transform, false);
        var mR = msgGo.AddComponent<RectTransform>(); mR.anchorMin=new Vector2(0.05f,0.28f); mR.anchorMax=new Vector2(0.95f,0.95f); mR.offsetMin=mR.offsetMax=Vector2.zero;
        var mT = msgGo.AddComponent<Text>(); mT.font=GetFont(); mT.fontSize=38; mT.color=COL_TEXT; mT.alignment=TextAnchor.UpperCenter; mT.lineSpacing=1.4f;
        mT.text="WARNING:\n\nYou are about to record all arts in the current active list. "+"Depending on count and format this may take a long time and output may be several GB.";
        // OK button
        var okGo = new GameObject("OK"); okGo.transform.SetParent(dlg.transform, false);
        var okR = okGo.AddComponent<RectTransform>(); okR.anchorMin=new Vector2(0.05f,0.04f); okR.anchorMax=new Vector2(0.44f,0.22f); okR.offsetMin=okR.offsetMax=Vector2.zero;
        SetBG(okGo, COL_ACCENT); var okBtn = okGo.AddComponent<Button>(); okBtn.targetGraphic=okGo.GetComponent<Image>();
        var okLGo = new GameObject("L"); okLGo.transform.SetParent(okGo.transform, false);
        var okLR = okLGo.AddComponent<RectTransform>(); okLR.anchorMin=Vector2.zero; okLR.anchorMax=Vector2.one; okLR.offsetMin=okLR.offsetMax=Vector2.zero;
        var okLT = okLGo.AddComponent<Text>(); okLT.text="OK, start"; okLT.font=GetFont(bold:true); okLT.fontSize=28; okLT.color=COL_BG; okLT.alignment=TextAnchor.MiddleCenter;
        // No button
        var noGo = new GameObject("No"); noGo.transform.SetParent(dlg.transform, false);
        var noR = noGo.AddComponent<RectTransform>(); noR.anchorMin=new Vector2(0.56f,0.04f); noR.anchorMax=new Vector2(0.95f,0.22f); noR.offsetMin=noR.offsetMax=Vector2.zero;
        SetBG(noGo, COL_PANEL2); var noBtn = noGo.AddComponent<Button>(); noBtn.targetGraphic=noGo.GetComponent<Image>();
        var noOutline = noGo.AddComponent<Outline>();
        noOutline.effectColor = COL_BORDER;
        noOutline.effectDistance = new Vector2(1.5f, -1.5f);
        var noLGo = new GameObject("L"); noLGo.transform.SetParent(noGo.transform, false);
        var noLR = noLGo.AddComponent<RectTransform>(); noLR.anchorMin=Vector2.zero; noLR.anchorMax=Vector2.one; noLR.offsetMin=noLR.offsetMax=Vector2.zero;
        var noLT = noLGo.AddComponent<Text>(); noLT.text="Cancel, I'm scared"; noLT.font=GetFont(); noLT.fontSize=28; noLT.color=COL_TEXT_DIM; noLT.alignment=TextAnchor.MiddleCenter;
        { GameObject _ov = overlay;
          noBtn.onClick.AddListener(() => Destroy(_ov));
          okBtn.onClick.AddListener(() => { Destroy(_ov); StartCoroutine(RecordAllCoroutine()); }); }
    }

        IEnumerator RecordAllCoroutine()
    {
        bool _isBack = showBacks;
        int _count = _isBack
            ? (filteredBacks != null ? filteredBacks.Count : 0)
            : (filteredArts  != null ? filteredArts.Count  : 0);
        if (recorder == null || _count == 0) yield break;
        int h = 1024; if (!string.IsNullOrEmpty(resField.text)) int.TryParse(resField.text, out h);
        float d = 5f; if (!string.IsNullOrEmpty(durField.text)) float.TryParse(durField.text, out d);
        string baseOut = (outField != null && !string.IsNullOrEmpty(outField.text))
            ? outField.text
            : System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                "TESLRecordings");
        var usedNames = new System.Collections.Generic.Dictionary<string, int>();
        // Затемнення тримаємо активним на ВЕСЬ пакет (а не лише на першому арті).
        _inBatch = true;
        if (_recBlockOverlay)
        {
            _recBlockOverlay.SetActive(true);
            if (_dimCo != null) StopCoroutine(_dimCo);
            _dimCo = StartCoroutine(FadeDim(0.85f, 4.0f));
        }
        for (int i = 0; i < _count; i++)
        {
            string col, rawName;
            if (_isBack) { col = "CardBacks"; rawName = filteredBacks[i]; }
            else { var art = filteredArts[i]; col = art.Collection ?? "Unknown"; rawName = art.DisplayName ?? ("art_" + i); }
            string key = col + "|" + rawName;
            int cnt; string safeName;
            if (usedNames.TryGetValue(key, out cnt))
            { usedNames[key] = cnt + 1; safeName = SanitizeFilename(rawName) + "_" + cnt; }
            else { usedNames[key] = 1; safeName = SanitizeFilename(rawName); }
            string colFolder = System.IO.Path.Combine(baseOut, SanitizeFilename(col));
            ShowCard(i);
            yield return new WaitForSeconds(0.4f);
            recorder.StartRecording(cam, safeName, colFolder, h, d, cropBacks, _isBack);
            while (recorder.IsRecording) yield return null;
            yield return new WaitForSeconds(0.15f);
        }
        // Пакет завершено — знімаємо затемнення
        _inBatch = false;
        if (_recBlockOverlay) _recBlockOverlay.SetActive(false);
    }

    static string SanitizeFilename(string s)
    {
        var sb = new System.Text.StringBuilder();
        char[] bad = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        foreach (char c in s) { bool skip=false; foreach(char b in bad) if(c==b){skip=true;break;} if(!skip) sb.Append(c); }
        return sb.ToString().Trim();
    }


    IEnumerator WatchRecordingEnd()
    {
        yield return null;
        while (recorder != null && recorder.IsRecording) yield return null;
        if (_recBlockOverlay) _recBlockOverlay.SetActive(false);
        if (curIndex >= 0) ShowCard(curIndex);
    }

    void ShowCard(int idx)
    {
        if (idx < 0 || idx >= CurrentCount) return;
        curIndex = idx;

        Material mat;
        string   cardName;

        if (showBacks)
        {
            int gi   = GetBackGlobalIndex(idx);
            if (gi < 0 || gi >= loader.BackMaterials.Count)
            {
                Debug.LogError("[ShowCard] Back index OOB: idx=" + idx + " gi=" + gi
                    + " total=" + loader.BackMaterials.Count);
                return;
            }
            mat      = loader.BackMaterials[gi];
            cardName = loader.BackNames[gi];
        }
        else
        {
            var entry = filteredArts[idx];
            mat      = loader.ArtMaterials[entry.MaterialIndex];
            cardName = entry.DisplayName;
        }

        SetupPreview(mat, cardName);
    }

    void SetupPreview(Material mat, string cardName)
    {
        if (mat == null) { Debug.LogError("[Preview] null material: " + cardName); return; }

        // Common camera settings (identical to TESLViewer.cs)
        cam.orthographic     = false;
        cam.fieldOfView      = 60f;
        cam.clearFlags       = CameraClearFlags.SolidColor;
        cam.backgroundColor  = Color.black;
        cam.transform.position = new Vector3(0f, 0f, -2.5f);

        bool isBack = showBacks;

        if (isBack)
        {
            // Card backs: transparent panel so camera shows through
            if (centerPanelBg != null) centerPanelBg.color = Color.clear;
            cam.transform.position = new Vector3(0f, 0f, -2.6f);
            cam.targetTexture = null;
            Rect vp = GetCenterViewport();
            cam.rect   = vp;
            cam.aspect = (Screen.width * vp.width) / (Screen.height * vp.height);
            if (previewImage != null) previewImage.color = Color.clear;
        }
        else
        {
            // Arts: solid panel covers any lingering backs from framebuffer
            if (centerPanelBg != null) centerPanelBg.color = COL_BG;
            // Card arts: render to RenderTexture → RawImage
            if (previewRT == null || previewRT.width != 1024)
            {
                if (previewRT != null) previewRT.Release();
                previewRT = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);
            }
            cam.targetTexture = previewRT;
            cam.rect          = new Rect(0f, 0f, 1f, 1f);
            cam.aspect        = 1f; // RT is 1024x1024 square
            if (previewImage != null)
            {
                previewImage.texture = previewRT;
                previewImage.color   = Color.white; // show RawImage
            }
        }

        if (quadObj != null) { quadObj.SetActive(false); Destroy(quadObj); }
        quadObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quadObj.transform.localScale = new Vector3(3f, 3f, 1f);
        quadObj.transform.position   = Vector3.zero;
        var rend = quadObj.GetComponent<MeshRenderer>();
        rend.material          = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows    = false;

        if (previewCardName) previewCardName.text = cardName;
    }

    Rect GetCenterViewport()
    {
        float scale = (_canvas != null) ? _canvas.scaleFactor : 1f;
        float left  = 280f * scale / Screen.width;
        float right = 240f * scale / Screen.width;
        float w     = Mathf.Clamp(1f - left - right, 0.1f, 1f);
        if (centerPanelRT != null)
        {
            Vector3[] corners = new Vector3[4];
            centerPanelRT.GetWorldCorners(corners);
            float x = corners[0].x / Screen.width;
            float y = corners[0].y / Screen.height;
            float cw = (corners[2].x - corners[0].x) / Screen.width;
            float ch = (corners[2].y - corners[0].y) / Screen.height;
            return new Rect(x, y, cw, ch);
        }
        return new Rect(left, 0f, w, 1f);
    }


    int GetBackGlobalIndex(int filtered)
    {
        // filteredBacks stores the material key in collection order
        // find the actual index in loader.BackNames/BackMaterials
        if (filtered < 0 || filtered >= filteredBacks.Count) return filtered;
        string matKey = filteredBacks[filtered];
        for (int i = 0; i < loader.BackNames.Count; i++)
            if (string.Equals(loader.BackNames[i], matKey,
                System.StringComparison.OrdinalIgnoreCase)) return i;
        return filtered; // fallback
    }

    // ══════════════════════════════════════════════════════════
    // LIST
    // ══════════════════════════════════════════════════════════
    void RefreshList()
    {
        // Запам'ятовуємо поточний вибір, щоб не стрибати на початок після
        // зміни/очищення пошуку чи перемикання колекцій.
        CardCollection.CardEntry keepArt = (!showBacks && curIndex >= 0 && curIndex < filteredArts.Count)
            ? filteredArts[curIndex] : null;
        string keepBack = (showBacks && curIndex >= 0 && curIndex < filteredBacks.Count)
            ? filteredBacks[curIndex] : null;

        foreach (Transform child in listContent) Destroy(child.gameObject);
        filteredArts.Clear(); filteredBacks.Clear(); listItemImages.Clear(); listItemSections.Clear(); sectionItems.Clear(); listItemImages.Clear();
        if (selectedItemBg != null) selectedItemBg = null;
        string sf = searchFilter.ToLower();

        if (!showBacks)
        {
            // Arts by collection
            cropRow.SetActive(false);
            foreach (string colName in collection.CollectionOrder)
            {
                List<CardCollection.CardEntry> entries;
                if (!collection.ByCollection.TryGetValue(colName, out entries)) continue;

                var matched = new List<CardCollection.CardEntry>();
                foreach (var e in entries)
                    if (CardCollection.MatchesSearch(e, searchFilter))
                        matched.Add(e);
                if (matched.Count == 0) continue;

                // Collapsible collection header
                bool isCollapsed = collapsed.ContainsKey(colName) && collapsed[colName];
                string arrow = isCollapsed ? "▶ " : "▼ ";
                var hdr = MakeListItem(listContent, arrow + colName, true);
                hdr.color = COL_ACCENT_BR;
                string capturedCol = colName;
                var hdrBtn = hdr.transform.parent.gameObject.AddComponent<Button>();
                hdrBtn.targetGraphic = hdr.transform.parent.gameObject.GetComponent<Image>();
                SetupDragToggle(hdr.transform.parent.gameObject, () => {
                    bool nowCollapsed = !(collapsed.ContainsKey(capturedCol) && collapsed[capturedCol]);
                    collapsed[capturedCol] = nowCollapsed;
                    hdr.text = (nowCollapsed ? "▶ " : "▼ ") + capturedCol;
                    List<GameObject> items;
                    if (sectionItems.TryGetValue(capturedCol, out items))
                        foreach (var it in items) if (it) it.SetActive(!nowCollapsed);
                });
                if (!sectionItems.ContainsKey(colName)) sectionItems[colName] = new List<GameObject>();

                foreach (var e in matched)
                {
                    int idx = filteredArts.Count;
                    filteredArts.Add(e);
                    var row = MakeListItem(listContent, e.DisplayName, false);
                    if (e.HasCustomName) row.color = new Color(0.8f, 0.95f, 0.8f);
                    var itemGo = row.transform.parent.gameObject;
                    AddListButton(itemGo, idx);
                    sectionItems[colName].Add(itemGo);
                    while (listItemSections.Count <= idx) listItemSections.Add("");
                    listItemSections[idx] = colName;
                    if (isCollapsed) itemGo.SetActive(false);
                }
            }
        }
        else
        {
            // Backs with collection grouping
            cropRow.SetActive(true);
            // Group backs by collection
            var backColMap = new Dictionary<string, List<int>>();
            var backColOrder = new List<string>();
            for (int i = 0; i < loader.BackNames.Count; i++)
            {
                if (!string.IsNullOrEmpty(sf) &&
                    !loader.BackNames[i].ToLower().Contains(sf)) continue;
                string colName = "Base Game";
                string colVal;
                // Use original material name (not display name) for collection lookup
                string backKey = (backMaterialKeys != null && i < backMaterialKeys.Count)
                    ? backMaterialKeys[i].ToLower() : loader.BackNames[i].ToLower();
                if (viewerConfig.CardBackCollections.TryGetValue(backKey, out colVal))
                    colName = colVal;
                else if (i < 3)
                    Debug.Log("[BackCol] key='" + backKey + "' not in config (" + viewerConfig.CardBackCollections.Count + " keys)");
                if (!backColMap.ContainsKey(colName))
                { backColMap[colName] = new List<int>(); backColOrder.Add(colName); }
                backColMap[colName].Add(i);
            }
            // Sort backColOrder by DEFAULT_COLLECTION_ORDER
            backColOrder.Sort(new System.Comparison<string>((a,b) => {
                var def = CardCollection.DEFAULT_COLLECTION_ORDER;
                int ia = -1, ib = -1;
                for(int x=0;x<def.Length;x++){
                    if(string.Equals(def[x],a,System.StringComparison.OrdinalIgnoreCase)) ia=x;
                    if(string.Equals(def[x],b,System.StringComparison.OrdinalIgnoreCase)) ib=x;
                }
                if(ia<0) ia=int.MaxValue-1;
                if(ib<0) ib=int.MaxValue-1;
                // Unused always last
                if(string.Equals(a,"Unused",System.StringComparison.OrdinalIgnoreCase)) ia=int.MaxValue;
                if(string.Equals(b,"Unused",System.StringComparison.OrdinalIgnoreCase)) ib=int.MaxValue;
                return ia==ib ? string.Compare(a,b,System.StringComparison.OrdinalIgnoreCase) : ia.CompareTo(ib);
            }));
            foreach (string colName in backColOrder)
            {
                bool isCollapsed = collapsed.ContainsKey(colName) && collapsed[colName];
                string arrow = isCollapsed ? "▶ " : "▼ ";
                var hdr = MakeListItem(listContent, arrow + colName, true);
                hdr.color = COL_ACCENT_BR;
                string cap = colName;
                var hBtn = hdr.transform.parent.gameObject.AddComponent<Button>();
                hBtn.targetGraphic = hdr.transform.parent.gameObject.GetComponent<Image>();
                SetupDragToggle(hdr.transform.parent.gameObject, () => {
                    bool nc = !(collapsed.ContainsKey(cap) && collapsed[cap]);
                    collapsed[cap] = nc;
                    hdr.text = (nc ? "▶ " : "▼ ") + cap;
                    List<GameObject> its;
                    if (sectionItems.TryGetValue(cap, out its))
                        foreach (var it in its) if (it) it.SetActive(!nc);
                });
                if (!sectionItems.ContainsKey(colName)) sectionItems[colName] = new List<GameObject>();
                foreach (int i in backColMap[colName])
                {
                    int idx2 = filteredBacks.Count;
                    // Store ORIGINAL material key in filteredBacks (needed for ShowCard)
                    filteredBacks.Add(backMaterialKeys != null && i < backMaterialKeys.Count ? backMaterialKeys[i] : loader.BackNames[i]);
                    // Use display name for UI label
                    string dispName = (displayBackNames != null && i < displayBackNames.Count) ? displayBackNames[i] : loader.BackNames[i];
                    var lbl = MakeListItem(listContent, dispName, false);
                    var itemGo = lbl.transform.parent.gameObject;
                    AddListButton(itemGo, idx2);
                    sectionItems[colName].Add(itemGo);
                    while (listItemSections.Count <= idx2) listItemSections.Add("");
                    listItemSections[idx2] = colName;
                    if (isCollapsed) itemGo.SetActive(false);
                }
            }
        }

        // Відновлюємо попередній вибір (якщо той самий арт/рубашка ще у списку)
        int restore = -1;
        if (!showBacks && keepArt != null) restore = filteredArts.IndexOf(keepArt);
        else if (showBacks && keepBack != null) restore = filteredBacks.IndexOf(keepBack);

        curIndex = -1;
        if (restore >= 0) ShowCard(restore);
        else if (CurrentCount > 0) ShowCard(0);
    }

    Text MakeListItem(Transform parent, string label, bool isHeader)
    {
        int rowH = isHeader ? 26 : 22;
        var go   = new GameObject(isHeader ? "H_" : "I_");
        go.transform.SetParent(parent, false);

        // RectTransform with explicit height (required for VLG childControlHeight=false)
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot     = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(0f, rowH);

        var le = go.AddComponent<LayoutElement>();
        le.minHeight = rowH; le.preferredHeight = rowH; le.flexibleWidth = 1;

        var img = go.AddComponent<Image>();
        img.color = isHeader ? new Color(0.22f, 0.15f, 0.07f, 1f) : Color.clear;
        go.AddComponent<RectMask2D>(); // clips long names at row boundary

        var textGo = new GameObject("T");
        textGo.transform.SetParent(go.transform, false);
        var tr = textGo.AddComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(isHeader ? 6 : 14, 1);
        tr.offsetMax = new Vector2(-4, -1);

        var txt = textGo.AddComponent<Text>();
        txt.text      = label;
        txt.font      = isHeader ? GetFont(bold: true) : GetRubikFont();
        txt.fontSize  = isHeader ? 16 : 14;
        txt.fontStyle = isHeader ? FontStyle.Bold : FontStyle.Normal;
        txt.color     = isHeader ? COL_ACCENT_BR : COL_TEXT;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        txt.verticalOverflow   = VerticalWrapMode.Overflow;
        txt.resizeTextForBestFit = false;
        return txt;
    }

    void AddListButton(GameObject rowGo, int idx)
    {
        var img = rowGo.GetComponent<Image>();
        // Ensure list big enough
        while (listItemImages.Count <= idx) listItemImages.Add(null);
        listItemImages[idx] = img;
        var btn = rowGo.AddComponent<Button>();
        btn.targetGraphic = img;
        var cb = new ColorBlock();
        cb.normalColor      = Color.clear;
        cb.highlightedColor = new Color(1f, 0.85f, 0.5f, 0.12f);
        cb.pressedColor     = new Color(1f, 0.85f, 0.5f, 0.22f);
        cb.colorMultiplier  = 1f;
        cb.fadeDuration     = 0.1f;
        btn.colors          = cb;

        int capture = idx;
        btn.onClick.AddListener(() => { SelectItem(img); ShowCard(capture); });
    }

    // ══════════════════════════════════════════════════════════
    // RECORDING
    // ══════════════════════════════════════════════════════════
    void ToggleRecord()
    {
        if (recorder.IsRecording)
        {
            recorder.StopRecording();
            recordBtn.GetComponentInChildren<Text>().text = "● Record";
            recordBtn.colors = ColorBlock("start");
            return;
        }

        if (curIndex < 0) return;

        int height;
        if (!int.TryParse(resField.text, out height) || height < 100) height = 1080;
        float seconds;
        if (!float.TryParse(durField.text, out seconds) || seconds < 0.5f) seconds = 5f;
        string outRoot = string.IsNullOrEmpty(outField.text)
            ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments)
            : outField.text;

        string cardName = showBacks
            ? (curIndex < loader.BackNames.Count ? loader.BackNames[GetBackGlobalIndex(curIndex)] : "card_back")
            : (curIndex < filteredArts.Count ? filteredArts[curIndex].DisplayName : "card");

        recordBtn.GetComponentInChildren<Text>().text = "■ Stop";
        recordBtn.colors = ColorBlock("stop");
        recordStatus.text = "Recording...";

        recorder.StartRecording(cam, cardName, outRoot, height, seconds, showBacks && cropBacks);
    }

    // ══════════════════════════════════════════════════════════
    // UI BUILD (programmatic)
    // ══════════════════════════════════════════════════════════
    void BuildUI()
    {
        // ── EventSystem (обов'язковий для UI взаємодії) ───────
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
            esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // ── Canvas ────────────────────────────────────────────
        var canvasGo = new GameObject("Canvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        _canvas = canvas;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode    = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        SetBG(canvasGo, Color.clear); // transparent: left/right panels have own bg, center shows camera or RawImage

        // ── Loading panel ─────────────────────────────────────
        loadingPanel = MakePanel(canvasGo.transform, "Loading",
            new Vector2(0,0), new Vector2(1,1), Vector2.zero, Vector2.zero);
        SetBG(loadingPanel, COL_BG);
        {
            var title = MakeText(loadingPanel.transform, "TES: Legends\nPremium Art Viewer",
                new Vector2(0.05f,0.64f), new Vector2(0.95f,0.78f), 36, FontStyle.Bold, COL_ACCENT_BR);
            title.alignment = TextAnchor.MiddleCenter;

            // Path input section
#if !UNITY_WEBGL || UNITY_EDITOR
            // 1. Path label
            var pathLbl = MakeText(loadingPanel.transform, "Select the game folder (any parent folder works):",
                new Vector2(0.05f,0.605f), new Vector2(0.95f,0.645f), 17, FontStyle.Normal, COL_TEXT_DIM);

            // 2. Path input box (declared before Browse button lambda needs it)
            var pathBg = MakePanel(loadingPanel.transform, "PathBg",
                new Vector2(0.05f,0.545f), new Vector2(0.77f,0.598f), Vector2.zero, Vector2.zero);
            SetBG(pathBg, new Color(0.14f,0.11f,0.22f));
            var pathInp = pathBg.AddComponent<InputField>();
            var phGo = new GameObject("Ph"); phGo.transform.SetParent(pathBg.transform, false);
            var phR = phGo.AddComponent<RectTransform>();
            phR.anchorMin=Vector2.zero; phR.anchorMax=Vector2.one;
            phR.offsetMin=new Vector2(8,0); phR.offsetMax=new Vector2(-8,0);
            var phT = phGo.AddComponent<Text>();
            phT.text=@"e.g. S:\SteamLibrary\...\The Elder Scrolls Legends_Data\StreamingAssets";
            phT.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
            phT.fontSize=20; phT.color=COL_TEXT_DIM; phT.fontStyle=FontStyle.Italic;
            phT.alignment=TextAnchor.MiddleLeft; pathInp.placeholder=phT;
            var tGo = new GameObject("T"); tGo.transform.SetParent(pathBg.transform, false);
            var tR = tGo.AddComponent<RectTransform>();
            tR.anchorMin=Vector2.zero; tR.anchorMax=Vector2.one;
            tR.offsetMin=new Vector2(8,0); tR.offsetMax=new Vector2(-8,0);
            var tT = tGo.AddComponent<Text>();
            tT.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
            tT.fontSize=20; tT.color=COL_TEXT; tT.alignment=TextAnchor.MiddleLeft;
            pathInp.textComponent=tT;
            string lastPath = PlayerPrefs.GetString("sa_path", streamingAssetsPath);
            pathInp.text = lastPath;

            // 3. Browse button — AFTER pathInp declared
            var browseBg = MakePanel(loadingPanel.transform, "BrowseBg",
                new Vector2(0.78f,0.548f), new Vector2(0.92f,0.596f), Vector2.zero, Vector2.zero);
            SetBG(browseBg, COL_PANEL2);
            browseBg.AddComponent<Outline>().effectColor = COL_BORDER;
            var browseBtnC = browseBg.AddComponent<Button>(); browseBtnC.targetGraphic=browseBg.GetComponent<Image>();
            var browseLblGo = new GameObject("L"); browseLblGo.transform.SetParent(browseBg.transform, false);
            var browseLR = browseLblGo.AddComponent<RectTransform>(); browseLR.anchorMin=Vector2.zero; browseLR.anchorMax=Vector2.one; browseLR.offsetMin=browseLR.offsetMax=Vector2.zero;
            var browseLbl = browseLblGo.AddComponent<Text>(); browseLbl.text="..."; browseLbl.font=GetFont(bold:true); browseLbl.fontSize=22; browseLbl.color=COL_TEXT_DIM; browseLbl.alignment=TextAnchor.MiddleCenter;
            { InputField _pi = pathInp; browseBtnC.onClick.AddListener(() => BrowseFolder(_pi, "Select StreamingAssets folder")); }

            // 4. Server button — AFTER pathBg, pathLbl declared (referenced in lambda)
            var serverBg = MakePanel(loadingPanel.transform, "ServerBg",
                new Vector2(0.22f,0.345f), new Vector2(0.78f,0.40f), Vector2.zero, Vector2.zero);
            SetBG(serverBg, new Color(0.07f,0.055f,0.115f,1f));
            serverBg.AddComponent<Outline>().effectColor = new Color(COL_ACCENT.r, COL_ACCENT.g, COL_ACCENT.b, 0.65f);
            var serverBtnC = serverBg.AddComponent<Button>(); serverBtnC.targetGraphic=serverBg.GetComponent<Image>();
            var serverLblGo = new GameObject("L"); serverLblGo.transform.SetParent(serverBg.transform, false);
            var serverLR = serverLblGo.AddComponent<RectTransform>(); serverLR.anchorMin=Vector2.zero; serverLR.anchorMax=Vector2.one; serverLR.offsetMin=serverLR.offsetMax=Vector2.zero;
            var serverLbl = serverLblGo.AddComponent<Text>(); serverLbl.text="No game? ↗  Stream arts (~850 MB, no install)"; serverLbl.font=GetFont(); serverLbl.fontSize=16; serverLbl.color=new Color(COL_ACCENT.r,COL_ACCENT.g,COL_ACCENT.b,0.95f); serverLbl.alignment=TextAnchor.MiddleCenter;
            // capture locals for lambda
            { GameObject _pb=pathBg; GameObject _plGo=pathLbl.gameObject; GameObject _sb2=serverBg; GameObject _bb=browseBg;
              serverBtnC.onClick.AddListener(() => {
                _pb.SetActive(false); _plGo.SetActive(false); _sb2.SetActive(false); _bb.SetActive(false);
                if (_loadingStartBg != null) _loadingStartBg.SetActive(false); // ховаємо й кнопку Load
                if (loadingLabel != null) loadingLabel.text = "Connecting to server...";
                if (_loadingNotesWrapper != null) _loadingNotesWrapper.SetActive(true);
                streamingAssetsPath = R2_URL;
                loader.StartLoading(R2_URL);
            }); }
#endif // !UNITY_WEBGL

            // Start button
            _loadingStartBg = MakePanel(loadingPanel.transform, "StartBtn",
                new Vector2(0.25f,0.44f), new Vector2(0.75f,0.52f), Vector2.zero, Vector2.zero);
            SetBG(_loadingStartBg, COL_ACCENT);
            var startBtn = _loadingStartBg.AddComponent<Button>();
            startBtn.targetGraphic = _loadingStartBg.GetComponent<Image>();
            var startLblGo = new GameObject("L"); startLblGo.transform.SetParent(_loadingStartBg.transform,false);
            var startLblR = startLblGo.AddComponent<RectTransform>();
            startLblR.anchorMin=Vector2.zero; startLblR.anchorMax=Vector2.one;
            var startLblT = startLblGo.AddComponent<Text>();
#if UNITY_WEBGL && !UNITY_EDITOR
            startLblT.text="\u25b6  Launch Viewer";
#else
            startLblT.text="\u25b6  Load";
#endif
            startLblT.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
            startLblT.fontSize=20; startLblT.fontStyle=FontStyle.Bold;
            startLblT.color=COL_TEXT; startLblT.alignment=TextAnchor.MiddleCenter;
            startBtn.onClick.AddListener(() =>
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                streamingAssetsPath = R2_URL;
                if (_loadingStartBg != null) _loadingStartBg.SetActive(false);
                if (loadingLabel != null) loadingLabel.text = "Connecting to server...";
                if (_loadingNotesWrapper != null) _loadingNotesWrapper.SetActive(true);
                loader.StartLoading(R2_URL);
#else
                string path = ResolveSAPath(pathInp.text);
                if (!System.IO.Directory.Exists(path) ||
                    !System.IO.Directory.Exists(System.IO.Path.Combine(path,"contentpack000")))
                { loadingLabel.text = "⚠ contentpack000 not found in: " + path; return; }
                PlayerPrefs.SetString("sa_path", path);
                streamingAssetsPath = path;
                if (_loadingStartBg != null) _loadingStartBg.SetActive(false);
#if !UNITY_WEBGL || UNITY_EDITOR
                pathBg.SetActive(false);
                pathLbl.gameObject.SetActive(false);
                browseBg.SetActive(false);
                serverBg.SetActive(false);
#endif
                if (_loadingNotesWrapper != null) _loadingNotesWrapper.SetActive(true);
                loader.StartLoading(streamingAssetsPath);
#endif
            });

            loadingLabel = MakeText(loadingPanel.transform, "Press the button to load assets; this may take a moment",
                new Vector2(0.1f,0.27f), new Vector2(0.9f,0.32f), 17, FontStyle.Normal, COL_TEXT_DIM);
            loadingLabel.alignment = TextAnchor.MiddleCenter;

            // Language toggle — hidden
            if (false) { var langBtn = MakePanel(loadingPanel.transform, "LangBtn",
                new Vector2(0.85f,0.02f), new Vector2(0.98f,0.08f), Vector2.zero, Vector2.zero);
            SetBG(langBtn, COL_BTN_OFF);
            var langBtnComp = langBtn.AddComponent<Button>();
            langBtnComp.targetGraphic = langBtn.GetComponent<Image>();
            var langLblGo = new GameObject("L"); langLblGo.transform.SetParent(langBtn.transform, false);
            var langLblR = langLblGo.AddComponent<RectTransform>();
            langLblR.anchorMin = Vector2.zero; langLblR.anchorMax = Vector2.one;
            langBtnText = langLblGo.AddComponent<Text>();
            langBtnText.text = "UA | EN"; langBtnText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            langBtnText.fontSize = 11; langBtnText.color = COL_TEXT_DIM;
            langBtnText.alignment = TextAnchor.MiddleCenter;
            langBtnComp.onClick.AddListener(() =>
            {
                isEnglish = !isEnglish;
                langBtnText.text = isEnglish ? "EN | UA" : "UA | EN";
                RefreshUIText();
            }); } // end hidden

            // Notes section — appears after Load is pressed
            _loadingNotesWrapper = MakePanel(loadingPanel.transform, "NotesWrap",
                new Vector2(0.05f,0.02f), new Vector2(0.95f,0.19f),
                Vector2.zero, Vector2.zero);
            SetBG(_loadingNotesWrapper, new Color(COL_BORDER.r, COL_BORDER.g, COL_BORDER.b, 0.5f));
            var nInner = MakePanel(_loadingNotesWrapper.transform, "NInner",
                new Vector2(0,0), new Vector2(1,1),
                new Vector2(1,1), new Vector2(-1,-1));
            SetBG(nInner, COL_BG);
            MakeText(nInner.transform, "NOTES",
                new Vector2(0.02f,0.62f), new Vector2(0.98f,0.97f),
                18, FontStyle.Bold, COL_TEXT_DIM).alignment = TextAnchor.MiddleLeft;
            _loadingTipText = MakeText(nInner.transform,
                GetTips(isEnglish).Length > 0 ? GetTips(isEnglish)[0] : "",
                new Vector2(0.02f,0.05f), new Vector2(0.98f,0.62f),
                22, FontStyle.Italic, COL_TEXT);
            _loadingTipText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _loadingTipText.verticalOverflow   = VerticalWrapMode.Overflow;
            _loadingTipText.alignment          = TextAnchor.UpperLeft;
            _loadingNotesWrapper.SetActive(false);
        }

        // ── Main panel ────────────────────────────────────────
        mainPanel = MakePanel(canvasGo.transform, "Main",
            new Vector2(0,0), new Vector2(1,1), Vector2.zero, Vector2.zero);
        SetBG(mainPanel, Color.clear); // transparent: backs render through to screen
        mainPanel.SetActive(false);

        BuildLeftPanel(mainPanel.transform);
        BuildCenterPanel(mainPanel.transform);
        BuildRightPanel(mainPanel.transform);
    }

    // ── LEFT PANEL ────────────────────────────────────────────
    void BuildLeftPanel(Transform parent)
    {
        const float W = 280f;
        var panel = MakePanel(parent, "LeftPanel",
            new Vector2(0,0), new Vector2(0,1),
            new Vector2(0,0), new Vector2(W,0));
        _leftPanelRT = panel.GetComponent<RectTransform>();
        SetBG(panel, COL_PANEL);

        float y = 0;

        // Mode buttons
        var modeRow = MakeHRow(panel.transform, y, 40);
        y += 40;
        btnArts  = MakeButton(modeRow.transform, "Card Arts",  () => SetMode(false), COL_BTN_ON);
        btnBacks = MakeButton(modeRow.transform, "Card Backs", () => SetMode(true),  COL_BTN_OFF);

        // Search
        var searchRow = MakeHRow(panel.transform, y, 36);
        y += 36;
        searchField = MakeInputField(searchRow.transform, "Search...", txt =>
        {
            searchFilter = txt;
            searchDebounce = 0.15f;
        });

        // Info hint (arts mode only)
          _artsInfoRow = MakeHRow(panel.transform, y, 28);
        _artsInfoRow.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(8,8,2,2);
        var artsInfoGo = new GameObject("T"); artsInfoGo.transform.SetParent(_artsInfoRow.transform, false);
        var aile = artsInfoGo.AddComponent<LayoutElement>(); aile.flexibleWidth=1f; aile.preferredHeight=22f;
        var artsInfoTxt = artsInfoGo.AddComponent<Text>();
        artsInfoTxt.text = "Search includes internal asset names";
        artsInfoTxt.font = GetRubikFont();
        artsInfoTxt.fontSize = 15;
        artsInfoTxt.color = COL_TEXT;
        artsInfoTxt.alignment = TextAnchor.MiddleLeft;
        artsInfoTxt.horizontalOverflow = HorizontalWrapMode.Overflow;

        // Crop toggle (backs only)
        cropRow = MakeHRow(panel.transform, y, 28); // same y as artsInfoRow
        y += 28; // increment once for both
        cropRow.SetActive(false);
        cropToggle = cropRow.AddComponent<Toggle>(); // label removed
        // Simple toggle setup
        BuildSimpleToggle(cropRow, ref cropToggle, val =>
        {
            cropBacks = val;
            UpdateBackMask();
            if (curIndex >= 0) ShowCard(curIndex);
        });

        // List scroll
        var scrollGo = MakePanel(panel.transform, "Scroll",
            new Vector2(0,0), new Vector2(1,1),
            new Vector2(0,0), new Vector2(0,-y));
        SetBG(scrollGo, new Color(0,0,0,0));

        var sr = scrollGo.AddComponent<ScrollRect>();
        listScrollRect = sr;
        sr.horizontal     = false;
        sr.vertical       = true;
        sr.scrollSensitivity = 20;



        var viewportGo = MakePanel(scrollGo.transform, "Viewport",
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        var vpImg = viewportGo.GetComponent<Image>() ?? viewportGo.AddComponent<Image>();
        vpImg.color = Color.white;  // alpha=1 required for stencil mask to work
        var vpMask = viewportGo.AddComponent<Mask>();
        vpMask.showMaskGraphic = false;  // invisible but mask active
        var vpRect = viewportGo.GetComponent<RectTransform>();
        vpRect.offsetMax = new Vector2(-11f, 0f); // room for scrollbar
        sr.viewport = vpRect;

        // Vertical scrollbar — parented to scrollGo, right edge
        var sbGo  = new GameObject("VScrollbar");
        sbGo.transform.SetParent(scrollGo.transform, false);
        var sbR   = sbGo.AddComponent<RectTransform>();
        sbR.anchorMin = new Vector2(1f, 0f); sbR.anchorMax = new Vector2(1f, 1f);
        sbR.pivot     = new Vector2(1f, 0.5f);
        sbR.offsetMin = new Vector2(-10f, 0f); sbR.offsetMax = Vector2.zero;
        SetBG(sbGo, new Color(0.10f, 0.07f, 0.04f, 1f));
        var sb    = sbGo.AddComponent<Scrollbar>();
        sb.direction = Scrollbar.Direction.BottomToTop;
        var hGo   = new GameObject("Handle"); hGo.transform.SetParent(sbGo.transform, false);
        var hR    = hGo.AddComponent<RectTransform>();
        hR.anchorMin = Vector2.zero; hR.anchorMax = Vector2.one;
        hR.offsetMin = new Vector2(1f, 1f); hR.offsetMax = new Vector2(-1f, -1f);
        var hImg  = hGo.AddComponent<Image>(); hImg.color = COL_ACCENT;
        sb.handleRect    = hR;
        sb.targetGraphic = hImg;
        sr.verticalScrollbar           = sb;
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;

        var contentGo = new GameObject("Content");
        contentGo.transform.SetParent(viewportGo.transform, false);
        listContent = contentGo.AddComponent<RectTransform>();
        listContent.anchorMin  = new Vector2(0,1);
        listContent.anchorMax  = new Vector2(1,1);
        listContent.pivot      = new Vector2(0,1);
        listContent.offsetMin  = Vector2.zero;
        listContent.offsetMax  = new Vector2(0,0);
        var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth    = true;
        vlg.childControlHeight   = true;   // VLG sets height from LayoutElement
        vlg.childForceExpandWidth = true;
        vlg.spacing = 2;
        var csf = contentGo.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        sr.content = listContent;
    }

    // ── CENTER PANEL ──────────────────────────────────────────
    void BuildCenterPanel(Transform parent)
    {
        const float LP = 280f, RP = 240f; // matched to right panel W
        var panel = MakePanel(parent, "Center",
            new Vector2(0,0), new Vector2(1,1),
            new Vector2(LP,0), new Vector2(-RP,0));
        SetBG(panel, Color.clear); // starts transparent; toggled by mode
        centerPanelBg = panel.GetComponent<Image>();
        centerPanelRT = panel.GetComponent<RectTransform>();

        // Create mask strips for card back crop (created once, toggled by UpdateBackMask)
        CreateBackMaskStrips(panel.transform);

        // (card name created after preview — renders on top)

        // Preview image — simple RawImage, no ARF, no uvRect manipulation
        var imgGo = MakePanel(panel.transform, "Preview",
            new Vector2(0,0.03f), new Vector2(1,0.92f),
            new Vector2(8,0), new Vector2(-8,0));
        previewImage = imgGo.AddComponent<RawImage>();
        previewImage.color = Color.white;
        var arf = imgGo.AddComponent<AspectRatioFitter>();
        arf.aspectMode  = AspectRatioFitter.AspectMode.FitInParent;
        arf.aspectRatio = 1f; // RT is 1024x1024 square


        // Card name — zero-height placeholder (name shown via list highlight)
        var nameGo = new GameObject("NamePlaceholder");
        nameGo.transform.SetParent(panel.transform, false);
        var nameR = nameGo.AddComponent<RectTransform>();
        nameR.anchorMin = new Vector2(0,0.95f); nameR.anchorMax = Vector2.one;
        previewCardName = nameGo.AddComponent<Text>();
        previewCardName.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        previewCardName.color = Color.clear; // hidden
    }

    // ── RIGHT PANEL ───────────────────────────────────────────
    void BuildRightPanel(Transform parent)
    {
        const float W = 240f;
        var panel = MakePanel(parent, "RightPanel",
            new Vector2(1,0), new Vector2(1,1), new Vector2(-W,0), new Vector2(0,0));
        SetBG(panel, COL_PANEL);
        var pRT = panel.GetComponent<RectTransform>();

        MakeText(panel.transform, "RECORD",
            new Vector2(0.05f,0.957f), new Vector2(0.95f,0.982f),
            20, FontStyle.Bold, COL_TEXT_DIM).alignment = TextAnchor.MiddleCenter;

#if !UNITY_WEBGL || UNITY_EDITOR
        MakeText(panel.transform, "Output folder:",
            new Vector2(0.04f,0.920f), new Vector2(0.96f,0.945f),
            16, FontStyle.Normal, COL_TEXT_DIM).alignment = TextAnchor.MiddleLeft;
        RPanel_Input(panel.transform, "C:\\Users\\...\\Videos",
            new Vector2(0.04f,0.887f), new Vector2(0.80f,0.912f), ref outField);
        var obGo=new GameObject("OB"); obGo.transform.SetParent(panel.transform,false);
        var obR=obGo.AddComponent<RectTransform>(); obR.anchorMin=new Vector2(0.82f,0.888f); obR.anchorMax=new Vector2(0.97f,0.911f); obR.offsetMin=obR.offsetMax=Vector2.zero;
        SetBG(obGo,COL_PANEL2); obGo.AddComponent<Outline>().effectColor=COL_BORDER;
        var obBtn=obGo.AddComponent<Button>(); obBtn.targetGraphic=obGo.GetComponent<Image>();
        var obLGo=new GameObject("L"); obLGo.transform.SetParent(obGo.transform,false);
        var obLR=obLGo.AddComponent<RectTransform>(); obLR.anchorMin=Vector2.zero; obLR.anchorMax=Vector2.one; obLR.offsetMin=obLR.offsetMax=Vector2.zero;
        var obLT=obLGo.AddComponent<Text>(); obLT.text="..."; obLT.font=GetFont(bold:true); obLT.fontSize=14; obLT.color=COL_TEXT_DIM; obLT.alignment=TextAnchor.MiddleCenter;
        { InputField _of=outField; obBtn.onClick.AddListener(()=>CardRecorder.BrowseOutputFolder(_of)); }

        MakeText(panel.transform,"Height px",new Vector2(0.04f,0.851f),new Vector2(0.48f,0.876f),17,FontStyle.Normal,COL_TEXT_DIM).alignment=TextAnchor.MiddleLeft;
        MakeText(panel.transform,"Seconds",new Vector2(0.52f,0.851f),new Vector2(0.96f,0.876f),17,FontStyle.Normal,COL_TEXT_DIM).alignment=TextAnchor.MiddleLeft;
        RPanel_Input(panel.transform,"1024",new Vector2(0.04f,0.817f),new Vector2(0.48f,0.843f),ref resField);
        RPanel_Input(panel.transform,"5",new Vector2(0.52f,0.817f),new Vector2(0.96f,0.843f),ref durField);

        MakeText(panel.transform,"Format",new Vector2(0.04f,0.781f),new Vector2(0.48f,0.806f),17,FontStyle.Normal,COL_TEXT_DIM).alignment=TextAnchor.MiddleLeft;
        var fmtNames=new string[]{"MP4","AVI","MOV","WebM","GIF"};
        int[] fmtIdx=new int[]{0};
        var fmtGo=new GameObject("FmtBtn"); fmtGo.transform.SetParent(panel.transform,false);
        var fmtR=fmtGo.AddComponent<RectTransform>(); fmtR.anchorMin=new Vector2(0.52f,0.781f); fmtR.anchorMax=new Vector2(0.96f,0.806f); fmtR.offsetMin=fmtR.offsetMax=Vector2.zero;
        SetBG(fmtGo,new Color(0.10f,0.08f,0.18f)); fmtGo.AddComponent<Outline>().effectColor=COL_BORDER;
        var fmtBtn=fmtGo.AddComponent<Button>(); fmtBtn.targetGraphic=fmtGo.GetComponent<Image>();
        var fmtLGo=new GameObject("L"); fmtLGo.transform.SetParent(fmtGo.transform,false);
        var fmtLR=fmtLGo.AddComponent<RectTransform>(); fmtLR.anchorMin=Vector2.zero; fmtLR.anchorMax=Vector2.one; fmtLR.offsetMin=fmtLR.offsetMax=Vector2.zero;
        var fmtLT=fmtLGo.AddComponent<Text>(); fmtLT.text="MP4"; fmtLT.font=GetFont(); fmtLT.fontSize=15; fmtLT.color=COL_TEXT; fmtLT.alignment=TextAnchor.MiddleCenter;
        { Text _lt=fmtLT; int[] _fi=fmtIdx; string[] _fn=fmtNames;
          fmtBtn.onClick.AddListener(()=>{ _fi[0]=(_fi[0]+1)%_fn.Length; _lt.text=_fn[_fi[0]]; if(recorder!=null) recorder.Format=(CardRecorder.RecordFormat)_fi[0]; }); }

        var recWrap=new GameObject("RecWrap"); recWrap.transform.SetParent(panel.transform,false);
        var rwR=recWrap.AddComponent<RectTransform>(); rwR.anchorMin=new Vector2(0.04f,0.700f); rwR.anchorMax=new Vector2(0.96f,0.736f); rwR.offsetMin=rwR.offsetMax=Vector2.zero;
        recWrap.AddComponent<Image>().color=COL_BORDER;
        var recInner=new GameObject("I"); recInner.transform.SetParent(recWrap.transform,false);
        var riR=recInner.AddComponent<RectTransform>(); riR.anchorMin=Vector2.zero; riR.anchorMax=Vector2.one; riR.offsetMin=new Vector2(1,1); riR.offsetMax=new Vector2(-1,-1);
        recInner.AddComponent<Image>().color=COL_ACCENT;
        var recBtn=recInner.AddComponent<Button>(); recBtn.targetGraphic=recInner.GetComponent<Image>(); recBtn.colors=ColorBlock("start");
        recordBtn = recBtn;
        var rLblGo=new GameObject("L"); rLblGo.transform.SetParent(recInner.transform,false);
        var rLR=rLblGo.AddComponent<RectTransform>(); rLR.anchorMin=Vector2.zero; rLR.anchorMax=Vector2.one; rLR.offsetMin=rLR.offsetMax=Vector2.zero;
        var rLbl=rLblGo.AddComponent<Text>(); rLbl.text="▶  Record"; rLbl.font=GetFont(bold:true); rLbl.fontSize=13; rLbl.color=COL_BG; rLbl.alignment=TextAnchor.MiddleCenter;
        recBtn.onClick.AddListener(()=>{
            if(recorder==null||curIndex<0||recorder.IsRecording) return;
            int h=1024; if(!string.IsNullOrEmpty(resField.text)) int.TryParse(resField.text,out h);
            float d=5f; if(!string.IsNullOrEmpty(durField.text)) float.TryParse(durField.text,out d);
            string outP=(outField!=null&&!string.IsNullOrEmpty(outField.text))
                ?outField.text
                :System.IO.Path.Combine(System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.Desktop),"TESLRecordings");
            if (_recBlockOverlay) { _recBlockOverlay.SetActive(true);
                if (_dimCo != null) StopCoroutine(_dimCo);
                _dimCo = StartCoroutine(FadeDim(0.85f, 4.0f)); }
            recorder.StartRecording(cam,showBacks?filteredBacks[curIndex]:filteredArts[curIndex].DisplayName,
                outP,h,d,cropBacks,showBacks);
            StartCoroutine(WatchRecordingEnd());
        });

        var raGo=new GameObject("RABtn"); raGo.transform.SetParent(panel.transform,false);
        var raR=raGo.AddComponent<RectTransform>(); raR.anchorMin=new Vector2(0.04f,0.658f); raR.anchorMax=new Vector2(0.96f,0.692f); raR.offsetMin=raR.offsetMax=Vector2.zero;
        SetBG(raGo,new Color(0.08f,0.06f,0.14f)); raGo.AddComponent<Outline>().effectColor=COL_BORDER;
        var raBtn=raGo.AddComponent<Button>(); raBtn.targetGraphic=raGo.GetComponent<Image>();
        var raLGo=new GameObject("L"); raLGo.transform.SetParent(raGo.transform,false);
        var raLR=raLGo.AddComponent<RectTransform>(); raLR.anchorMin=Vector2.zero; raLR.anchorMax=Vector2.one; raLR.offsetMin=raLR.offsetMax=Vector2.zero;
        var raLT=raLGo.AddComponent<Text>(); raLT.text="▼  Record All (current list)"; raLT.font=GetFont(); raLT.fontSize=14; raLT.color=COL_TEXT_DIM; raLT.alignment=TextAnchor.MiddleCenter;
        raBtn.onClick.AddListener(()=>ShowRecordAllConfirm());
#endif

        var sepGo=new GameObject("Sep"); sepGo.transform.SetParent(panel.transform,false);
        var sepR=sepGo.AddComponent<RectTransform>(); sepR.anchorMin=new Vector2(0.04f,0.645f); sepR.anchorMax=new Vector2(0.96f,0.646f); sepR.offsetMin=sepR.offsetMax=Vector2.zero;
        sepGo.AddComponent<Image>().color=new Color(COL_BORDER.r,COL_BORDER.g,COL_BORDER.b,0.3f);

        MakeText(panel.transform,"NOTES",new Vector2(0.04f,0.618f),new Vector2(0.96f,0.640f),16,FontStyle.Bold,COL_TEXT_DIM).alignment=TextAnchor.MiddleLeft;

        var notesBg=new GameObject("NotesBg"); notesBg.transform.SetParent(panel.transform,false);
        var nbR=notesBg.AddComponent<RectTransform>(); nbR.anchorMin=new Vector2(0f,0.090f); nbR.anchorMax=new Vector2(1f,0.614f); nbR.offsetMin=nbR.offsetMax=Vector2.zero;
        SetBG(notesBg,new Color(0.028f,0.035f,0.044f));
        var tipGo=new GameObject("Tip"); tipGo.transform.SetParent(notesBg.transform,false);
        var tipR=tipGo.AddComponent<RectTransform>(); tipR.anchorMin=new Vector2(0.03f,0.20f); tipR.anchorMax=new Vector2(0.97f,0.97f); tipR.offsetMin=tipR.offsetMax=Vector2.zero;
        _tipText=tipGo.AddComponent<Text>(); _tipText.font=GetRubikFont(); _tipText.fontSize=16;
        _tipText.color=COL_TEXT; _tipText.alignment=TextAnchor.UpperLeft; _tipText.lineSpacing=1.2f;
        _tipText.horizontalOverflow=HorizontalWrapMode.Wrap; _tipText.verticalOverflow=VerticalWrapMode.Overflow;
        _tipText.text=GetTips(isEnglish).Length>0?GetTips(isEnglish)[0]:"";
        var arRow=new GameObject("Arrows"); arRow.transform.SetParent(notesBg.transform,false);
        var arR2=arRow.AddComponent<RectTransform>(); arR2.anchorMin=Vector2.zero; arR2.anchorMax=new Vector2(1f,0.19f); arR2.offsetMin=arR2.offsetMax=Vector2.zero;
        var arHLG=arRow.AddComponent<HorizontalLayoutGroup>(); arHLG.childControlWidth=true; arHLG.childForceExpandWidth=true; arHLG.childControlHeight=true; arHLG.childForceExpandHeight=true;
        var prevBtn=MakeButton(arRow.transform,"◄",()=>ShowTip(-1),new Color(0.05f,0.07f,0.09f)); prevBtn.gameObject.AddComponent<LayoutElement>().preferredWidth=28f;
        var arSp=new GameObject("S"); arSp.transform.SetParent(arRow.transform,false); arSp.AddComponent<LayoutElement>().flexibleWidth=1f;
        var nextBtn=MakeButton(arRow.transform,"►",()=>ShowTip(+1),new Color(0.05f,0.07f,0.09f)); nextBtn.gameObject.AddComponent<LayoutElement>().preferredWidth=28f;
        if(GetTips(isEnglish).Length>1) _tipCoroutine=StartCoroutine(RotateTips());

        var fsGo=new GameObject("FsBtn"); fsGo.transform.SetParent(panel.transform,false);
        var fsR=fsGo.AddComponent<RectTransform>(); fsR.anchorMin=new Vector2(0.10f,0.012f); fsR.anchorMax=new Vector2(0.90f,0.080f); fsR.offsetMin=fsR.offsetMax=Vector2.zero;
        SetBG(fsGo,new Color(0.10f,0.08f,0.18f)); fsGo.AddComponent<Outline>().effectColor=new Color(COL_BORDER.r,COL_BORDER.g,COL_BORDER.b,0.5f);
        var fsBtnC=fsGo.AddComponent<Button>(); fsBtnC.targetGraphic=fsGo.GetComponent<Image>();
        fsBtnC.onClick.AddListener(()=>{
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying=false;
#else
            // Application.Quit чекає вивантаження всіх бандлів (довго висить).
            // Kill завершує процес миттєво — застосунку нема чого зберігати.
            Application.Quit();
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch {}
#endif
        });
        fsGo.SetActive(Application.platform!=RuntimePlatform.WebGLPlayer);
        var fsLGo=new GameObject("L"); fsLGo.transform.SetParent(fsGo.transform,false);
        var fsLR=fsLGo.AddComponent<RectTransform>(); fsLR.anchorMin=Vector2.zero; fsLR.anchorMax=Vector2.one; fsLR.offsetMin=fsLR.offsetMax=Vector2.zero;
        var fsLbl=fsLGo.AddComponent<Text>(); fsLbl.text="Exit"; fsLbl.font=GetFont(); fsLbl.fontSize=16; fsLbl.color=COL_TEXT; fsLbl.alignment=TextAnchor.MiddleCenter;
    }

    // Simple input field anchored within parent
    void RPanel_Input(Transform parent, string placeholder,
        Vector2 ancMin, Vector2 ancMax, ref InputField field)
    {
        var go = new GameObject("Inp"); go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin=ancMin; rt.anchorMax=ancMax; rt.offsetMin=rt.offsetMax=Vector2.zero;
        var bg = go.AddComponent<Image>(); bg.color = new Color(0.10f,0.08f,0.18f);
        field = go.AddComponent<InputField>(); field.targetGraphic = bg;
        field.caretColor = COL_TEXT; field.caretWidth = 2;
        field.selectionColor = new Color(COL_ACCENT.r,COL_ACCENT.g,COL_ACCENT.b,0.4f);
        var tGo = new GameObject("T"); tGo.transform.SetParent(go.transform, false);
        var tr = tGo.AddComponent<RectTransform>();
        tr.anchorMin=Vector2.zero; tr.anchorMax=Vector2.one;
        tr.offsetMin=new Vector2(5,1); tr.offsetMax=new Vector2(-4,-1);
        var t = tGo.AddComponent<Text>(); t.font=GetFont(); t.fontSize=14; t.color=COL_TEXT;
        t.alignment=TextAnchor.MiddleLeft; field.textComponent=t;
        var phGo = new GameObject("PH"); phGo.transform.SetParent(go.transform, false);
        var phR = phGo.AddComponent<RectTransform>();
        phR.anchorMin=Vector2.zero; phR.anchorMax=Vector2.one;
        phR.offsetMin=new Vector2(5,1); phR.offsetMax=new Vector2(-4,-1);
        var ph = phGo.AddComponent<Text>(); ph.font=GetFont(); ph.fontSize=14; ph.color=COL_TEXT_DIM;
        ph.fontStyle=FontStyle.Italic; ph.text=placeholder; ph.alignment=TextAnchor.MiddleLeft;
        field.placeholder=ph;
    }


    Text AddSectionLabel(Transform parent, string text)
    {
        var go = new GameObject("SecLabel"); go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 22f;
        var t  = go.AddComponent<Text>();
        t.font      = GetFont(bold: true);
        t.fontSize  = 20;
        t.color     = COL_TEXT_DIM;
        t.alignment = TextAnchor.MiddleLeft;
        t.text      = text;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }

    void AddSeparator(Transform parent)
    {
        var go = new GameObject("Sep"); go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 1f; le.flexibleWidth = 1f;
        var img = go.AddComponent<Image>(); img.color = new Color(COL_BORDER.r, COL_BORDER.g, COL_BORDER.b, 0.4f);
    }

    InputField MakeCompactInput(Transform parent, string placeholder)
    {
        var go = new GameObject("CInput"); go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.flexibleWidth = 1f; le.preferredHeight = 18f; le.minHeight = 18f;
        SetBG(go, Color.black);
        var inp = go.AddComponent<InputField>();
        var tGo = new GameObject("T"); tGo.transform.SetParent(go.transform, false);
        var tr = tGo.AddComponent<RectTransform>();
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(6,2); tr.offsetMax = new Vector2(-4,-2);
        var t = tGo.AddComponent<Text>();
        t.font = GetFont(); t.fontSize = 13; t.color = COL_TEXT;
        t.alignment = TextAnchor.MiddleLeft;
        inp.textComponent = t;
        var phGo = new GameObject("PH"); phGo.transform.SetParent(go.transform, false);
        var phR = phGo.AddComponent<RectTransform>();
        phR.anchorMin = Vector2.zero; phR.anchorMax = Vector2.one;
        phR.offsetMin = new Vector2(6,2); phR.offsetMax = new Vector2(-4,-2);
        var ph = phGo.AddComponent<Text>();
        ph.font = GetFont(); ph.fontSize = 13; ph.color = COL_TEXT_DIM;
        ph.fontStyle = FontStyle.Italic;
        ph.text = placeholder; ph.alignment = TextAnchor.MiddleLeft;
        inp.placeholder = ph;
        inp.targetGraphic = go.GetComponent<Image>();
        return inp;
    }

    // Anchor-based: no LayoutGroup fighting, explicit percentage anchors
    // ── Auto-detect StreamingAssets from any parent folder ──────
    string ResolveSAPath(string input)
    {
        input = input.Trim().Trim('"');
        if (string.IsNullOrEmpty(input)) return input;
        // Already StreamingAssets?
        if (System.IO.Directory.Exists(System.IO.Path.Combine(input,"contentpack000")))
            return input;
        // _Data/StreamingAssets level
        string sa1 = System.IO.Path.Combine(input,"StreamingAssets");
        if (System.IO.Directory.Exists(System.IO.Path.Combine(sa1,"contentpack000")))
            return sa1;
        // Game root level
        string sa2 = System.IO.Path.Combine(input,
            "The Elder Scrolls Legends_Data","StreamingAssets");
        if (System.IO.Directory.Exists(System.IO.Path.Combine(sa2,"contentpack000")))
            return sa2;
        return input; // return as-is; error shown on load attempt
    }

    // ── Folder dialog via PowerShell ───────────────────────────────────
    static string RunFolderDialog(string initPath, string title)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string outFile    = System.IO.Path.GetTempFileName();
        string scriptFile = outFile + ".ps1";
        string safeTitle  = title.Replace("'", "''");
        string safePath   = System.IO.Directory.Exists(initPath)
                            ? initPath.Replace("'", "''") : "";
        string safeOut    = outFile.Replace("\\", "\\\\");
        var sb = new System.Text.StringBuilder();
        sb.Append("Add-Type -AssemblyName System.Windows.Forms; ");
        sb.Append("$d=New-Object System.Windows.Forms.FolderBrowserDialog; ");
        sb.Append("$d.Description='" + safeTitle + "'; ");
        if (safePath.Length > 0)
            sb.Append("$d.SelectedPath='" + safePath + "'; ");
        sb.Append("if($d.ShowDialog()-eq'OK'){$d.SelectedPath|");
        sb.Append("Out-File -FilePath '" + safeOut + "' -NoNewline -Encoding utf8}");
        System.IO.File.WriteAllText(scriptFile, sb.ToString(),
            System.Text.Encoding.UTF8);
        var psi = new System.Diagnostics.ProcessStartInfo();
        psi.FileName        = "powershell.exe";
        psi.Arguments       = "-ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                              + scriptFile + "\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow  = true;
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null) proc.WaitForExit();
        string picked = "";
        if (System.IO.File.Exists(outFile))
            picked = System.IO.File.ReadAllText(outFile).Trim();
        try { System.IO.File.Delete(scriptFile); System.IO.File.Delete(outFile); }
        catch (System.Exception) {}
        return picked;
#else
        return "";
#endif
    }

    void BrowseFolder(InputField target, string title)
    {
        bool modernOk = false;
        string picked = "";
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try { picked = WinFolderPicker.Pick(title, target.text); modernOk = true; }
        catch (System.Exception e)
        { Debug.LogWarning("[Browse] сучасний діалог недоступний, фолбек на PowerShell: " + e.Message); }
#endif
        // PowerShell-діалог лише якщо сучасний недоступний (не при скасуванні)
        if (!modernOk) picked = RunFolderDialog(target.text, title);
        if (!string.IsNullOrEmpty(picked)) target.text = picked;
    }
    void AddRecordInput(Transform parent, string label, string def, ref InputField field)
    {
        var row = new GameObject("RecInput"); row.transform.SetParent(parent, false);
        var le  = row.AddComponent<LayoutElement>();
        le.preferredHeight = 26f; le.minHeight = 22f; le.flexibleHeight = 0f;
        // Label left 46%
        var lblGo = new GameObject("L"); lblGo.transform.SetParent(row.transform, false);
        var lR = lblGo.AddComponent<RectTransform>();
        lR.anchorMin = new Vector2(0f,0f); lR.anchorMax = new Vector2(0.46f,1f);
        lR.offsetMin = new Vector2(2,0); lR.offsetMax = Vector2.zero;
        var lt = lblGo.AddComponent<Text>(); lt.font=GetFont(); lt.fontSize=14;
        lt.color=COL_TEXT_DIM; lt.alignment=TextAnchor.MiddleLeft; lt.text=label;
        // Input right 52%
        var inpGo = new GameObject("I"); inpGo.transform.SetParent(row.transform, false);
        var iR = inpGo.AddComponent<RectTransform>();
        iR.anchorMin = new Vector2(0.48f,0.05f); iR.anchorMax = new Vector2(1f,0.95f);
        iR.offsetMin = iR.offsetMax = Vector2.zero;
        var ibg = inpGo.AddComponent<Image>(); ibg.color = new Color(0.10f,0.08f,0.18f);
        field = inpGo.AddComponent<InputField>(); field.targetGraphic = ibg;
        var tGo = new GameObject("T"); tGo.transform.SetParent(inpGo.transform, false);
        var tr = tGo.AddComponent<RectTransform>(); tr.anchorMin=Vector2.zero; tr.anchorMax=Vector2.one;
        tr.offsetMin=new Vector2(4,1); tr.offsetMax=new Vector2(-3,-1);
        var t = tGo.AddComponent<Text>(); t.font=GetFont(); t.fontSize=14; t.color=COL_TEXT;
        t.alignment=TextAnchor.MiddleLeft; field.textComponent=t;
        var phGo = new GameObject("PH"); phGo.transform.SetParent(inpGo.transform, false);
        var phR = phGo.AddComponent<RectTransform>(); phR.anchorMin=Vector2.zero; phR.anchorMax=Vector2.one;
        phR.offsetMin=new Vector2(4,1); phR.offsetMax=new Vector2(-3,-1);
        var ph = phGo.AddComponent<Text>(); ph.font=GetFont(); ph.fontSize=14; ph.color=COL_TEXT_DIM;
        ph.fontStyle=FontStyle.Italic; ph.text=def; ph.alignment=TextAnchor.MiddleLeft;
        field.placeholder=ph;
    }
    
    void AddCompactLabel(Transform parent, string text)
    {
        var go = new GameObject("L"); go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredWidth = 70f; le.preferredHeight = 18f;
        var t  = go.AddComponent<Text>();
        t.font = GetFont(); t.fontSize = 14; t.color = COL_TEXT_DIM;
        t.alignment = TextAnchor.MiddleLeft;
        t.text = text;
    }


    void OpenRename()
    {
        if (curIndex < 0 || showBacks || curIndex >= filteredArts.Count) return;
        renameTarget = filteredArts[curIndex];
        renameField.text = renameTarget.DisplayName;
        renamePanel.SetActive(true);
        renameField.Select(); renameField.ActivateInputField();
    }

    void ConfirmRename()
    {
        if (renameTarget == null || string.IsNullOrEmpty(renameField.text.Trim())) return;
        collection.SetCustomName(renameTarget, renameField.text.Trim());
        renamePanel.SetActive(false);
        // Update the list item text in-place
        RefreshList();
    }

    void ApplyCollectionFilter(CardCollection col,
        string[] srcNames, string[] keepMats, string[] targetNames)
    {
        // Знаходимо реальний ключ джерела серед кандидатів (case-insensitive)
        string srcKey = ResolveCollectionKey(col, srcNames);
        List<CardCollection.CardEntry> src;
        if (srcKey == null || !col.ByCollection.TryGetValue(srcKey, out src))
        {
            Debug.Log("[Filter] джерело не знайдено серед: " + string.Join(", ", srcNames));
            return;
        }

        var keepSet = new HashSet<string>(keepMats, System.StringComparer.OrdinalIgnoreCase);
        var overflow = new List<CardCollection.CardEntry>();
        var keep     = new List<CardCollection.CardEntry>();
        foreach (var e in src)
        {
            if (keepSet.Contains(e.MaterialName)) keep.Add(e);
            else overflow.Add(e);
        }
        col.ByCollection[srcKey] = keep;

        // Реальний ключ цілі: перший наявний кандидат, інакше створюємо перший
        string realTarget = ResolveCollectionKey(col, targetNames);
        if (realTarget == null)
        {
            realTarget = targetNames[0];
            col.ByCollection[realTarget] = new List<CardCollection.CardEntry>();
            if (!col.CollectionOrder.Contains(realTarget))
                col.CollectionOrder.Add(realTarget);
        }
        col.ByCollection[realTarget].AddRange(overflow);
        col.ByCollection[realTarget].Sort(new System.Comparison<CardCollection.CardEntry>(
            CardCollection.CompareEntryName));
        Debug.Log("[Filter] " + srcKey + " → " + realTarget + ": перенесено " + overflow.Count);
    }

    // Повертає перший ключ ByCollection, що збігається з будь-яким кандидатом
    // (без урахування регістру), або null.
    string ResolveCollectionKey(CardCollection col, string[] candidates)
    {
        foreach (string cand in candidates)
            foreach (string key in col.ByCollection.Keys)
                if (string.Equals(key, cand, System.StringComparison.OrdinalIgnoreCase))
                    return key;
        return null;
    }

    void ApplyDefaultCollectionOrder(CardCollection col)
    {
        var desired = CardCollection.DEFAULT_COLLECTION_ORDER;
        // Map actual names case-insensitively
        var actual = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string c in col.CollectionOrder)
            if (!actual.ContainsKey(c)) actual[c] = c;

        var result = new List<string>();
        // Step 1: add desired collections in order
        var added = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < desired.Length; i++)
        {
            string realName;
            if (actual.TryGetValue(desired[i], out realName))
            {
                result.Add(realName); added.Add(realName);
            }
        }
        // Step 2: add any unknown collections (sorted) before Unused
        var extra = new List<string>();
        foreach (string c in col.CollectionOrder)
            if (!added.Contains(c)) extra.Add(c);
        extra.Sort(new System.Comparison<string>((a,b) =>
            string.Compare(a, b, System.StringComparison.OrdinalIgnoreCase)));

        // Find Unused position and insert extras before it
        int ui = -1;
        for (int i = 0; i < result.Count; i++)
            if (string.Equals(result[i], "Unused", System.StringComparison.OrdinalIgnoreCase))
            { ui = i; break; }
        if (ui >= 0) result.InsertRange(ui, extra);
        else { result.AddRange(extra); }

        // Step 3: ensure Unused is last (always)
        string unusedEntry = null;
        for (int i = 0; i < result.Count; i++)
            if (string.Equals(result[i], "Unused", System.StringComparison.OrdinalIgnoreCase))
            { unusedEntry = result[i]; result.RemoveAt(i); break; }
        if (unusedEntry != null) result.Add(unusedEntry);
        else { string uv; if (actual.TryGetValue("Unused", out uv)) result.Add(uv); }

        col.CollectionOrder = result;
        Debug.Log("[Order] " + string.Join(" > ", result.ToArray()));
    }

    void CreateBackMaskStrips(Transform centerPanel)
    {
        var go = new GameObject("BackMaskOverlay");
        go.transform.SetParent(centerPanel, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        _maskRawImage = go.AddComponent<RawImage>();
        _maskRawImage.color = Color.white;
        go.SetActive(false);
    }

    RawImage _maskRawImage;
    Texture2D _maskTex;

    // SDF rounded rectangle: negative = inside, positive = outside
    static float SdfRoundRect(float px, float py,
                               float x0, float y0, float x1, float y1, float cr)
    {
        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
        float hw = (x1 - x0) * 0.5f - cr;
        float hh = (y1 - y0) * 0.5f - cr;
        float dx = Mathf.Max(Mathf.Abs(px - cx) - hw, 0f);
        float dy = Mathf.Max(Mathf.Abs(py - cy) - hh, 0f);
        return Mathf.Sqrt(dx * dx + dy * dy) - cr;
    }

    void RebuildMaskTexture()
    {
        // Backs → crop to full card (UV 0..1); Arts → inner art UV
        float artL, artR, artB, artT;
        if (showBacks)
        {   // Exact outer boundary from Card_back.obj — no inset
            artL = -1.5f + BACK_U_MIN * 3f;
            artR = -1.5f + BACK_U_MAX * 3f;
            artB = -1.5f + BACK_V_MIN * 3f;
            artT = -1.5f + BACK_V_MAX * 3f;
        }
        else
        { artL = -1.5f + ART_U_MIN*3f; artR = -1.5f + ART_U_MAX*3f;
          artB = -1.5f + ART_V_MIN*3f; artT = -1.5f + ART_V_MAX*3f; }

        Vector3 vpBL = cam.WorldToViewportPoint(new Vector3(artL, artB, 0f));
        Vector3 vpTR = cam.WorldToViewportPoint(new Vector3(artR, artT, 0f));
        float x0 = vpBL.x, y0 = vpBL.y;
        float x1 = vpTR.x, y1 = vpTR.y;

        int sz = 512;
        // Corner radius: backs ~0.014 UV units from Card_back.obj; arts use 0 (sharp)
        float cr      = showBacks ? 0.006f : 0.0f; // from Card_back.obj ring corner geometry
        float feather = 1.5f / sz;        // anti-alias: 1.5 pixel wide smooth edge

        if (_maskTex != null) Destroy(_maskTex);
        _maskTex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        _maskTex.filterMode = FilterMode.Bilinear;
        Color[] px = new Color[sz * sz];
        Color bg = COL_BG;

        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float u = (x + 0.5f) / sz;
            float v = (y + 0.5f) / sz;
            // SDF: negative inside art area, positive outside
            float d = SdfRoundRect(u, v, x0, y0, x1, y1, cr);
            // alpha=0 inside (transparent=show card), alpha=1 outside (opaque=hide)
            float alpha = Mathf.Clamp01(d / feather + 0.5f);
            px[y * sz + x] = new Color(bg.r, bg.g, bg.b, alpha);
        }
        _maskTex.SetPixels(px);
        _maskTex.Apply();
        if (_maskRawImage != null) _maskRawImage.texture = _maskTex;
    }

    void UpdateBackMask()
    {
        bool show = showBacks && cropBacks && _maskRawImage != null;
        if (_maskRawImage != null)
        {
            if (show) RebuildMaskTexture(); // recalculate with current camera
            _maskRawImage.gameObject.SetActive(show);
        }
    }

    void SetMode(bool backs)
    {
        showBacks = backs;
        if (_artsInfoRow != null) _artsInfoRow.SetActive(!backs);
        if (cropRow      != null) cropRow.SetActive(backs);
        SetButtonState(btnArts,  !backs);
        SetButtonState(btnBacks, backs);
        searchField.text = "";
        searchFilter     = "";
        curIndex         = -1;
        RefreshList();
        UpdateBackMask(); // after RefreshList→ShowCard sets camera for backs
    }

    void SetButtonState(Button btn, bool on)
    {
        btn.colors = ColorBlock(on ? "on" : "off");
        var txt = btn.GetComponentInChildren<Text>();
        if (txt) txt.color = on ? COL_TEXT : COL_TEXT_DIM;
    }

    // ══════════════════════════════════════════════════════════
    // UI HELPERS
    // ══════════════════════════════════════════════════════════
    GameObject MakePanel(Transform parent, string name,
        Vector2 ancMin, Vector2 ancMax, Vector2 offMin, Vector2 offMax)
    {
        var go   = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin  = ancMin;
        rect.anchorMax  = ancMax;
        rect.offsetMin  = offMin;
        rect.offsetMax  = offMax;
        return go;
    }

    void SetBG(GameObject go, Color col)
    {
        var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        img.color = col;
    }

    Text MakeText(Transform parent, string txt,
        Vector2 ancMin, Vector2 ancMax,
        int size, FontStyle style, Color col)
    {
        var go = new GameObject("Text_"+txt.Substring(0,Mathf.Min(8,txt.Length)));
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = ancMin; rect.anchorMax = ancMax;
        rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        var t  = go.AddComponent<Text>();
        t.text = txt; t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize  = size; t.fontStyle = style; t.color = col;
        t.supportRichText = false;
        return t;
    }

    Text MakeText(Transform parent, string txt, float ax0, float ay0, int size, FontStyle st, Color col)
        => MakeText(parent, txt, new Vector2(ax0,ay0), new Vector2(1f,ay0+0.05f), size, st, col);

    GameObject MakeHRow(Transform parent, float topOffset, float height)
    {
        var go   = new GameObject("Row_"+topOffset);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0,1); rect.anchorMax = new Vector2(1,1);
        rect.pivot     = new Vector2(0,1);
        rect.anchoredPosition = new Vector2(0, -topOffset);
        rect.sizeDelta        = new Vector2(0, height);
        SetBG(go, new Color(0,0,0,0));
        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlWidth   = true;
        hlg.childControlHeight  = true;
        hlg.childForceExpandWidth = true;
        hlg.padding = new RectOffset(8,8,4,4);
        hlg.spacing = 4;
        return go;
    }

    Button MakeButton(Transform parent, string label, UnityEngine.Events.UnityAction action, Color bg)
    {
        var go = new GameObject("Btn_"+label);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.colors = ColorBlock("normal");
        btn.onClick.AddListener(action);

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var trect = textGo.AddComponent<RectTransform>();
        trect.anchorMin = Vector2.zero; trect.anchorMax = Vector2.one;
        trect.offsetMin = Vector2.zero; trect.offsetMax  = Vector2.zero;
        var txt = textGo.AddComponent<Text>();
        txt.text      = label;
        txt.font      = GetFont(bold: true);
        txt.fontSize  = 15;
        txt.color     = COL_TEXT;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.fontStyle = FontStyle.Bold;

        return btn;
    }

    Button MakeStandaloneButton(Transform parent, string label,
        UnityEngine.Events.UnityAction action, Color bg, float height)
    {
        var go = new GameObject("Btn_"+label);
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.flexibleWidth   = 1;

        var img = go.AddComponent<Image>();
        img.color = bg;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.colors = ColorBlock("start");
        btn.onClick.AddListener(action);

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var trect = textGo.AddComponent<RectTransform>();
        trect.anchorMin = Vector2.zero; trect.anchorMax = Vector2.one;
        trect.offsetMin = Vector2.zero; trect.offsetMax  = Vector2.zero;
        var txt = textGo.AddComponent<Text>();
        txt.text      = label;
        txt.font      = GetFont(bold: true);
        txt.fontSize  = 14;
        txt.color     = COL_TEXT;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.fontStyle = FontStyle.Bold;

        return btn;
    }

    InputField MakeInputField(Transform parent, string placeholder,
        UnityEngine.Events.UnityAction<string> onChange)
    {
        var go = new GameObject("Input");
        go.transform.SetParent(parent, false);
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.06f, 0.05f, 0.12f);

        var field = go.AddComponent<InputField>();
        field.targetGraphic = bg;

        // Placeholder
        var phGo = new GameObject("Placeholder");
        phGo.transform.SetParent(go.transform, false);
        var phR = phGo.AddComponent<RectTransform>();
        phR.anchorMin = Vector2.zero; phR.anchorMax = Vector2.one;
        phR.offsetMin = new Vector2(8,0); phR.offsetMax = new Vector2(-8,0);
        var phT = phGo.AddComponent<Text>();
        phT.text = placeholder; phT.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        phT.fontSize = 13; phT.color = COL_TEXT_DIM;
        phT.fontStyle = FontStyle.Italic;
        phT.alignment = TextAnchor.MiddleLeft;
        field.placeholder = phT;

        // Text
        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform, false);
        var txtR = txtGo.AddComponent<RectTransform>();
        txtR.anchorMin = Vector2.zero; txtR.anchorMax = Vector2.one;
        txtR.offsetMin = new Vector2(8,0); txtR.offsetMax = new Vector2(-8,0);
        var txt = txtGo.AddComponent<Text>();
        txt.font = GetFont();
        txt.fontSize = 13; txt.color = COL_TEXT;
        txt.alignment = TextAnchor.MiddleLeft;
        field.textComponent = txt;

        field.onValueChanged.AddListener(onChange);
        return field;
    }

    void AddCompactInput(Transform parent, string label, string def, ref InputField field)
    {
        var rowGo = new GameObject("CR");
        rowGo.transform.SetParent(parent, false);
        var rle = rowGo.AddComponent<LayoutElement>();
        rle.preferredHeight = 32; rle.minHeight = 32;

        var rhlg = rowGo.AddComponent<HorizontalLayoutGroup>();
        rhlg.childControlWidth    = true;
        rhlg.childControlHeight   = true;
        rhlg.childForceExpandWidth = false;
        rhlg.spacing = 8;

        // Label — short, right-aligned
        var lblGo = new GameObject("L");
        lblGo.transform.SetParent(rowGo.transform, false);
        var lle = lblGo.AddComponent<LayoutElement>();
        lle.preferredWidth = 90; lle.flexibleWidth = 0;
        var lt = lblGo.AddComponent<Text>();
        lt.text = label; lt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        lt.fontSize = 13; lt.color = COL_TEXT; lt.alignment = TextAnchor.MiddleRight;

        // Input — takes remaining width
        var inpGo = new GameObject("I");
        inpGo.transform.SetParent(rowGo.transform, false);
        var ile = inpGo.AddComponent<LayoutElement>();
        ile.flexibleWidth = 1; ile.minWidth = 60;
        var ibg = inpGo.AddComponent<Image>();
        ibg.color = new Color(0.06f, 0.04f, 0.02f);
        field = inpGo.AddComponent<InputField>();
        field.targetGraphic = ibg;
        BuildInputTexts(inpGo, ref field, def, 14);
        field.text = def;
    }

    void BuildInputTexts(GameObject go, ref InputField field, string placeholder, int size)
    {
        var ph = new GameObject("Ph"); ph.transform.SetParent(go.transform, false);
        var phR = ph.AddComponent<RectTransform>(); phR.anchorMin=Vector2.zero; phR.anchorMax=Vector2.one;
        phR.offsetMin=new Vector2(4,0); phR.offsetMax=new Vector2(-4,0);
        var phT = ph.AddComponent<Text>(); phT.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
        phT.fontSize=size; phT.color=COL_TEXT_DIM; phT.fontStyle=FontStyle.Italic;
        phT.alignment=TextAnchor.MiddleLeft; field.placeholder=phT;
        var t = new GameObject("T"); t.transform.SetParent(go.transform, false);
        var tR = t.AddComponent<RectTransform>(); tR.anchorMin=Vector2.zero; tR.anchorMax=Vector2.one;
        tR.offsetMin=new Vector2(4,0); tR.offsetMax=new Vector2(-4,0);
        var tT = t.AddComponent<Text>(); tT.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
        tT.fontSize=size; tT.color=COL_TEXT; tT.alignment=TextAnchor.MiddleLeft;
        field.textComponent=tT;
    }

    void AddSectionHeader(Transform parent, string title)
    {
        var go = new GameObject("Header_"+title);
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 22;
        var txt = go.AddComponent<Text>();
        txt.text = title; txt.font = GetFont();
        txt.fontSize = 13; txt.fontStyle = FontStyle.Bold;
        txt.color = COL_ACCENT; txt.alignment = TextAnchor.MiddleLeft;
    }

    void AddLabeledInput(Transform parent, string label, string defVal, ref InputField field)
    {
        var lbl = new GameObject("Lbl_"+label);
        lbl.transform.SetParent(parent, false);
        var lle = lbl.AddComponent<LayoutElement>(); lle.preferredHeight = 18;
        var lt  = lbl.AddComponent<Text>();
        lt.text = label; lt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        lt.fontSize = 11; lt.color = COL_TEXT_DIM;

        var inpGo = new GameObject("Inp_"+label);
        inpGo.transform.SetParent(parent, false);
        var ile = inpGo.AddComponent<LayoutElement>(); ile.preferredHeight = 28;

        var bg = inpGo.AddComponent<Image>(); bg.color = new Color(0.06f,0.05f,0.12f);
        var f  = inpGo.AddComponent<InputField>(); f.targetGraphic = bg;

        var phGo = new GameObject("Ph"); phGo.transform.SetParent(inpGo.transform,false);
        var phR = phGo.AddComponent<RectTransform>();
        phR.anchorMin=Vector2.zero; phR.anchorMax=Vector2.one;
        phR.offsetMin=new Vector2(6,0); phR.offsetMax=new Vector2(-6,0);
        var phT = phGo.AddComponent<Text>();
        phT.text=defVal; phT.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
        phT.fontSize=12; phT.color=COL_TEXT_DIM; phT.fontStyle=FontStyle.Italic;
        phT.alignment=TextAnchor.MiddleLeft; f.placeholder=phT;

        var tGo = new GameObject("T"); tGo.transform.SetParent(inpGo.transform,false);
        var tR = tGo.AddComponent<RectTransform>();
        tR.anchorMin=Vector2.zero; tR.anchorMax=Vector2.one;
        tR.offsetMin=new Vector2(6,0); tR.offsetMax=new Vector2(-6,0);
        var t = tGo.AddComponent<Text>();
        t.font=Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize=12; t.color=COL_TEXT; t.alignment=TextAnchor.MiddleLeft;
        f.textComponent=t; f.text=defVal;

        field = f;
    }

    Text AddLabel(Transform parent, string text, int size, Color col)
    {
        var go = new GameObject("Lbl"); go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = Mathf.Max(20, text.Split('\n').Length * (size+4));
        var t  = go.AddComponent<Text>();
        t.text = text; t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = size; t.color = col;
        t.alignment = TextAnchor.UpperLeft;
        return t;
    }


    void AddBorderRight(GameObject panel)
    {
        var go = MakePanel(panel.transform, "Border",
            new Vector2(1,0), new Vector2(1,1), new Vector2(-1,0), Vector2.zero);
        SetBG(go, COL_BORDER);
    }

    void AddBorderLeft(GameObject panel)
    {
        var go = MakePanel(panel.transform, "Border",
            new Vector2(0,0), new Vector2(0,1), Vector2.zero, new Vector2(1,0));
        SetBG(go, COL_BORDER);
    }

    void BuildSimpleToggle(GameObject row, ref Toggle toggle, UnityEngine.Events.UnityAction<bool> onChange)
    {
        // Toggle MUST be on its own child GO — row already has HLG + Image
        var tglGo = new GameObject("ToggleContainer");
        tglGo.transform.SetParent(row.transform, false);
        var tglR = tglGo.AddComponent<RectTransform>();
        tglR.anchorMin = Vector2.zero; tglR.anchorMax = Vector2.one;
        tglR.offsetMin = Vector2.zero; tglR.offsetMax = Vector2.zero;
        tglGo.AddComponent<Image>().color = Color.clear;

        var tgl = tglGo.AddComponent<Toggle>();
        toggle = tgl;

        // Checkbox background (20x20)
        var bgGo = new GameObject("Bg"); bgGo.transform.SetParent(tglGo.transform, false);
        var bgR = bgGo.AddComponent<RectTransform>();
        bgR.anchorMin = bgR.anchorMax = new Vector2(0f, 0.5f);
        bgR.pivot = new Vector2(0f, 0.5f);
        bgR.anchoredPosition = new Vector2(6f, 0f);
        bgR.sizeDelta = new Vector2(18f, 18f);
        var bgImg = bgGo.AddComponent<Image>(); bgImg.color = COL_BORDER;

        // Checkmark
        var chkGo = new GameObject("Check"); chkGo.transform.SetParent(bgGo.transform, false);
        var chkR = chkGo.AddComponent<RectTransform>();
        chkR.anchorMin = Vector2.zero; chkR.anchorMax = Vector2.one;
        chkR.offsetMin = new Vector2(3,3); chkR.offsetMax = new Vector2(-3,-3);
        var chkImg = chkGo.AddComponent<Image>(); chkImg.color = COL_ACCENT_BR;

        tgl.targetGraphic = bgImg;
        tgl.graphic        = chkImg;
        tgl.isOn           = false;
        tgl.onValueChanged.AddListener(onChange);

        // Label
        var lblGo = new GameObject("Lbl"); lblGo.transform.SetParent(tglGo.transform, false);
        var lblR = lblGo.AddComponent<RectTransform>();
        lblR.anchorMin = Vector2.zero; lblR.anchorMax = Vector2.one;
        lblR.offsetMin = new Vector2(30, 0); lblR.offsetMax = new Vector2(-4, 0);
        var lblT = lblGo.AddComponent<Text>();
        lblT.text = "Crop by in-game UV"; lblT.font = GetRubikFont();
        lblT.fontSize = 14; lblT.color = COL_TEXT; lblT.alignment = TextAnchor.MiddleLeft;
    }

    static ColorBlock ColorBlock(string mode)
    {
        var cb = new ColorBlock();
        cb.colorMultiplier = 1f;
        cb.fadeDuration    = 0.1f;
        switch (mode)
        {
            case "on":
                cb.normalColor      = COL_BTN_ON;
                cb.highlightedColor = COL_ACCENT_BR;
                cb.pressedColor     = COL_ACCENT;
                break;
            case "off":
                cb.normalColor      = COL_BTN_OFF;
                cb.highlightedColor = new Color(0.25f,0.2f,0.35f);
                cb.pressedColor     = COL_BORDER;
                break;
            case "start":
                cb.normalColor      = COL_ACCENT;
                cb.highlightedColor = COL_ACCENT_BR;
                cb.pressedColor     = new Color(0.5f,0.35f,0.05f);
                break;
            case "stop":
                cb.normalColor      = COL_RED;
                cb.highlightedColor = new Color(0.7f,0.2f,0.2f);
                cb.pressedColor     = new Color(0.45f,0.05f,0.05f);
                break;
            default:
                cb.normalColor      = new Color(0,0,0,0);
                cb.highlightedColor = new Color(1,1,1,0.08f);
                cb.pressedColor     = new Color(1,1,1,0.15f);
                break;
        }
        return cb;
    }

    // ── Language refresh ─────────────────────────────────────
    void RefreshUIText()
    {
        if (btnArts)  btnArts.GetComponentInChildren<Text>().text  = T("Карт арти",   "Card Arts");
        if (btnBacks) btnBacks.GetComponentInChildren<Text>().text = T("Рубашки",     "Card Backs");
        if (loadingLabel && !loader.ArtMaterials.Count.Equals(0))
            loadingLabel.text = T("Loading...", "Loading...");
        if (searchField) searchField.placeholder.GetComponent<Text>().text =
            T("Search...", "Search...");
        RefreshList();
    }

    // ──────────────────────────────────────────────────────────
    // KEYBOARD NAVIGATION
    // Navigate within the FILTERED list (respects search)
    // ──────────────────────────────────────────────────────────
    void Update()
    {
        // Search debounce: rebuild list 0.3s after last keypress
        if (searchDebounce > 0f)
        {
            searchDebounce -= UnityEngine.Time.deltaTime;
            if (searchDebounce <= 0f) { searchDebounce = 0f; RefreshList(); }
        }

        // Crop by UV: rebuild on resize
        if (Screen.width != _lastScreenW || Screen.height != _lastScreenH)
        {
            _lastScreenW = Screen.width; _lastScreenH = Screen.height;
            if (_leftPanelRT != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_leftPanelRT);
            UnityEngine.Canvas.ForceUpdateCanvases();
        }

        // Keep viewport synced — all modes
        if (cam != null && cam.targetTexture == null)
        {
            Rect vp = GetCenterViewport();
            cam.rect   = vp;
            cam.aspect = (Screen.width * vp.width) / (Screen.height * vp.height);
        }

        // F11 = fullscreen toggle
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F11))
            Screen.fullScreen = !Screen.fullScreen;

        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.RightArrow) ||
            UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.DownArrow))
            Navigate(+1);
        if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.LeftArrow) ||
            UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.UpArrow))
            Navigate(-1);
    }

    System.Collections.IEnumerator SearchDebounce()
    {
        yield return new WaitForSeconds(0.25f);
        RefreshList();
    }

    void SelectItem(Image img)
    {
        if (selectedItemBg != null)
        {
            selectedItemBg.color = Color.clear;
            var bar = selectedItemBg.transform.Find("AccentBar");
            if (bar != null) Destroy(bar.gameObject);
            // Reset text scroll position

        }
        selectedItemBg = img;
        if (img != null)
        {
            img.color = COL_SELECT;
            var barGo = new GameObject("AccentBar");
            barGo.transform.SetParent(img.transform, false);
            var barR = barGo.AddComponent<RectTransform>();
            barR.anchorMin = Vector2.zero; barR.anchorMax = new Vector2(0f, 1f);
            barR.offsetMin = Vector2.zero; barR.offsetMax = new Vector2(3f, 0f);
            barGo.AddComponent<Image>().color = COL_ACCENT_BR;
            // Start text scroll if name is long
            var tGo = img.transform.Find("T");
            if (tGo != null) StartCoroutine(ScrollText(tGo.GetComponent<Text>()));
        }
    }

    // Плавне наростання затемнення зони арта при старті запису.
    // Використовуємо реальний час (не накопичення deltaTime): старт запису дає
    // лаг-кадр із величезним deltaTime, через що раніше alpha стрибала одразу.
    IEnumerator FadeDim(float target, float dur)
    {
        if (_recDimImg == null) yield break;
        var c0 = _recDimImg.color; c0.a = 0f; _recDimImg.color = c0;
        yield return null;                       // пропускаємо важкий стартовий кадр
        float start = UnityEngine.Time.realtimeSinceStartup;
        while (_recDimImg != null)
        {
            float t = (UnityEngine.Time.realtimeSinceStartup - start) / dur;
            if (t >= 1f) break;
            float e = Mathf.SmoothStep(0f, 1f, t); // плавний початок і кінець
            var cc = _recDimImg.color; cc.a = e * target; _recDimImg.color = cc;
            yield return null;
        }
        if (_recDimImg != null)
        { var cc = _recDimImg.color; cc.a = target; _recDimImg.color = cc; }
    }

    IEnumerator ScrollText(Text txt)
    {
        if (txt == null) yield break;
        Image owner = selectedItemBg;
        yield return null;
        yield return null; // second frame: layout settled
        if (txt == null) yield break;
        var rt       = txt.transform as RectTransform;
        var parentRT = txt.transform.parent as RectTransform;
        if (rt == null || parentRT == null) yield break;
        LayoutRebuilder.ForceRebuildLayoutImmediate(parentRT);

        float textW = txt.preferredWidth;
        float viewW = rt.rect.width;            // видима ширина
        if (textW <= viewW + 2f) yield break;   // вміщається — прокрутка не потрібна

        // Зберігаємо початкові параметри, щоб відновити після завершення
        var oAnchorMin = rt.anchorMin; var oAnchorMax = rt.anchorMax;
        var oPivot     = rt.pivot;     var oSize      = rt.sizeDelta;
        var oPos       = rt.anchoredPosition;

        // Робимо обʼєкт фіксованої ширини (== довжині тексту), прикріпленим ліворуч.
        // Так його rect ЗАВЖДИ покриває текст і перетинає маску → Unity не відсікає
        // (culling) його, і текст не стає прозорим. Рух робимо через anchoredPosition.
        float leftInset = rt.offsetMin.x; // початковий лівий відступ (з MakeListItem = 14)
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot     = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(textW, 0f);
        rt.anchoredPosition = new Vector2(leftInset, 0f);

        float dist  = textW - viewW + 4f;
        float speed = 35f;
        while (selectedItemBg == owner && txt != null)
        {
            yield return new WaitForSeconds(1.2f);
            if (selectedItemBg != owner) break;
            float x = 0f;
            while (x < dist && selectedItemBg == owner)
            { x += UnityEngine.Time.deltaTime * speed; rt.anchoredPosition = new Vector2(leftInset - Mathf.Min(x,dist), 0f); yield return null; }
            yield return new WaitForSeconds(1.2f);
            if (selectedItemBg != owner) break;
            x = dist;
            while (x > 0f && selectedItemBg == owner)
            { x -= UnityEngine.Time.deltaTime * speed; rt.anchoredPosition = new Vector2(leftInset - Mathf.Max(x,0f), 0f); yield return null; }
        }
        // Відновлюємо початковий layout
        if (txt != null)
        {
            rt.anchorMin = oAnchorMin; rt.anchorMax = oAnchorMax;
            rt.pivot = oPivot; rt.sizeDelta = oSize; rt.anchoredPosition = oPos;
        }
    }

    void Navigate(int dir)
    {
        int count = showBacks ? filteredBacks.Count : filteredArts.Count;
        if (count == 0) return;
        int next = curIndex < 0 ? 0 : (curIndex + dir + count) % count;
        // Auto-expand collapsed section if needed
        if (next < listItemSections.Count)
        {
            string sec = listItemSections[next];
            if (!string.IsNullOrEmpty(sec) && collapsed.ContainsKey(sec) && collapsed[sec])
            {
                collapsed[sec] = false;
                List<GameObject> items;
                if (sectionItems.TryGetValue(sec, out items))
                    foreach (var it in items) if (it) it.SetActive(true);
                // Update header arrow
                foreach (Transform t in listContent)
                {
                    var txt = t.Find("T")?.GetComponent<Text>();
                    if (txt != null && txt.text.EndsWith(" " + sec))
                        txt.text = "▼ " + sec;
                }
            }
        }
        if (next < listItemImages.Count && listItemImages[next] != null)
        {
            SelectItem(listItemImages[next]);
            ScrollToItem(next);
        }
        ShowCard(next);
    }

    void ScrollToItem(int idx)
    {
        if (listScrollRect == null || listContent == null) return;
        if (idx >= listItemImages.Count || listItemImages[idx] == null) return;
        StartCoroutine(DoScrollToItem(listItemImages[idx].transform as RectTransform));
    }

    IEnumerator DoScrollToItem(RectTransform itemRT)
    {
        yield return null; // wait one frame for layout to update
        if (itemRT == null || listScrollRect == null) yield break;

        float contentH = listContent.rect.height;
        float viewH    = listScrollRect.viewport != null
                         ? listScrollRect.viewport.rect.height
                         : listScrollRect.GetComponent<RectTransform>().rect.height;
        if (contentH <= viewH) yield break;

        // anchoredPosition.y is negative (downward) in VLG content
        float itemTop = -itemRT.anchoredPosition.y;
        float itemBot = itemTop + itemRT.rect.height;
        float curScrollY = (1f - listScrollRect.verticalNormalizedPosition) * (contentH - viewH);

        if (itemTop < curScrollY)
            listScrollRect.verticalNormalizedPosition = Mathf.Clamp01(1f - itemTop / (contentH - viewH));
        else if (itemBot > curScrollY + viewH)
            listScrollRect.verticalNormalizedPosition = Mathf.Clamp01(1f - (itemBot - viewH) / (contentH - viewH));
    }
}
