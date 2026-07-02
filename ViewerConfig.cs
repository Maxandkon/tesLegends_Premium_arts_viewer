using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Завантажує viewer_config.json:
///   1. Resources/viewer_config  — вбудований у проект (лише читання)
///   2. Application.persistentDataPath/viewer_config.json — перевизначення
///
/// ГЕНЕРАЦІЯ: при першому запуску в Unity Editor файл записується прямо в
///   Assets/Resources/viewer_config.json → стає частиною проекту назавжди.
/// У standalone build — записується у persistentDataPath.
/// </summary>
public class ViewerConfig
{
    public List<string>              CollectionsOrder    = new List<string>();
    public Dictionary<string,string> CollectionLabels    = new Dictionary<string,string>();
    public Dictionary<string,string> CardNames           = new Dictionary<string,string>();
    public Dictionary<string,string> CardBackNames       = new Dictionary<string,string>();
    public Dictionary<string,string> CardBackCollections = new Dictionary<string,string>();

    const string FILE = "viewer_config";

    // ── Шлях де писати генерований файл ──────────────────────
    static string WritePath()
    {
#if UNITY_EDITOR
        // В Editor — прямо у Assets/Resources/ (вбудовується у проект)
        string dir = Path.Combine(Application.dataPath, "Resources");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, FILE + ".json");
#else
        // У білді пишемо у persistentDataPath (завжди доступний для запису).
        // StreamingAssets у білді може бути read-only (напр. у Program Files) —
        // запис туди кидав би виняток і обривав решту ініціалізації.
        string dir = Application.persistentDataPath;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, FILE + ".json");
#endif
    }

    // ── Load ─────────────────────────────────────────────────
    // ЄДИНЕ ДЖЕРЕЛО ІСТИНИ — вбудований Resources/viewer_config.json.
    // У білді читаємо ВИКЛЮЧНО з Resources. persistentDataPath НЕ читаємо:
    // саме туди GenerateTemplate писав автошаблон (з лейблами k==v та
    // дефолтними назвами), і Load підхоплював його замість Resources —
    // через це з 2-го запуску лейбли/порядок/рубашки "зникали".
    public void Load()
    {
        string json = null;

#if UNITY_EDITOR
        // В Editor: читаємо напряму з диска (щоб правки застосовувались одразу)
        string editorPath = Path.Combine(Application.dataPath, "Resources", FILE + ".json");
        if (File.Exists(editorPath))
        {
            json = File.ReadAllText(editorPath, Encoding.UTF8);
            Debug.Log("[Config] Editor direct read: " + editorPath);
        }
#endif
        // Основне джерело (єдине у білді, фолбек у Editor) — вбудований Resources
        if (json == null)
        {
            var ta = Resources.Load<TextAsset>(FILE);
            if (ta != null)
            {
                json = ta.text;
                Debug.Log("[Config] Завантажено з Resources: " + FILE);
            }
        }

        if (json != null)
        {
            json = StripBom(json);
            Parse(json);
            Debug.Log("[Config] labels=" + CollectionLabels.Count
                + " card_names=" + CardNames.Count
                + " back_names=" + CardBackNames.Count
                + " back_cols=" + CardBackCollections.Count
                + " order=" + CollectionsOrder.Count);
        }
        else Debug.LogWarning("[Config] viewer_config не знайдено у Resources — дефолтні значення.");
    }

    // JsonUtility спотикається на BOM/пробілах на початку — прибираємо
    static string StripBom(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s[0] == '\uFEFF') s = s.Substring(1);
        return s.TrimStart('\uFEFF', '\u200B', ' ', '\t', '\r', '\n');
    }

    void Parse(string json)
    {
        try
        {
            var r = JsonUtility.FromJson<RawConfig>(json);
            if (r == null) { Debug.LogWarning("[Config] JsonUtility повернув null — файл не розпарсено"); return; }
            if (r.collections_order != null)
                CollectionsOrder = new List<string>(r.collections_order);
            Fill(r.collection_labels,    CollectionLabels);
            Fill(r.card_names,           CardNames,  lower:true);
            Fill(r.card_back_names,      CardBackNames, lower:true);
            Fill(r.card_back_collections,CardBackCollections, lower:true);
        }
        catch (System.Exception ex) { Debug.LogWarning("[Config] Parse: " + ex.Message); }
    }

    static void Fill(KV[] src, Dictionary<string,string> dst, bool lower=false)
    {
        if (src == null) return;
        foreach (var e in src) dst[lower ? e.k.ToLower() : e.k] = e.v;
    }

    // ── Генерація шаблону ─────────────────────────────────────
    public void GenerateTemplate(CardCollection col, List<string> backNames)
    {
#if !UNITY_EDITOR
        // У білді шаблон НЕ генеруємо: Resources — єдине джерело істини.
        // Запис автошаблону поруч отруював би конфіг при наступному запуску.
        return;
#else
        string path = WritePath();
        // Regenerate if collections_order might be stale (first-run or rebuild)
        // To preserve manual card name edits, we only regenerate the order section
        // For simplicity: always regenerate on load (user can lock by removing this)
        if (File.Exists(path))
        {
            // Only regenerate if existing file has wrong/old collections_order
            // Simple check: does it start correctly? If not, regenerate.
            try {
                string existing = File.ReadAllText(path, Encoding.UTF8);
                // If file looks valid and user has made edits (card_names changed), keep it
                if (existing.Length > 500) return; // has meaningful content
            } catch {}
        }

        var sb = new StringBuilder();
        sb.AppendLine("{");

        // Use DEFAULT sort order for template
        var order = new System.Collections.Generic.List<string>(col.CollectionOrder);
        order.Sort(new System.Comparison<string>(CardCollection.CompareCollectionOrder));
        // collections_order
        sb.Append("  \"collections_order\": [");
        for (int i=0;i<order.Count;i++) sb.Append("\""+E(order[i])+"\"" + (i<order.Count-1?",":""));
        sb.AppendLine("],");

        // collection_labels
        AppendKV(sb, "collection_labels", order, s => s, s => s);
        sb.AppendLine(",");

        // card_names
        var arts = new List<CardCollection.CardEntry>();
        foreach (var c in order) { List<CardCollection.CardEntry> el; if(col.ByCollection.TryGetValue(c,out el)) arts.AddRange(el); }
        AppendKVList(sb, "card_names",
            arts, a => a.MaterialName.ToLower(), a => a.DisplayName);
        sb.AppendLine(",");

        // card_back_names
        AppendKVList(sb, "card_back_names",
            backNames, s => s.ToLower(), s => s);
        sb.AppendLine(",");

        // card_back_collections
        AppendKVList(sb, "card_back_collections",
            backNames, s => s.ToLower(), s => "Base Game");
        sb.AppendLine();

        sb.AppendLine("}");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

#if UNITY_EDITOR
        // Оновлюємо AssetDatabase щоб Unity побачив новий файл
        UnityEditor.AssetDatabase.Refresh();
        Debug.Log("[Config] Вбудовано у проект: " + path + "\nПерезапусти Editor — файл з'явиться у Assets/Resources/");
#else
        Debug.Log("[Config] Шаблон збережено: " + path);
#endif
#endif
    }

    // ── Apply ─────────────────────────────────────────────────
    public void ApplyToCollection(CardCollection col)
    {
        if (CollectionsOrder.Count > 0)
        {
            var n = new List<string>(CollectionsOrder);
            foreach (var c in col.CollectionOrder) if (!n.Contains(c)) n.Add(c);
            col.CollectionOrder = n;
        }
        foreach (var kv in CollectionLabels)
        {
            if (kv.Key==kv.Value || !col.ByCollection.ContainsKey(kv.Key)) continue;
            if (!col.ByCollection.ContainsKey(kv.Value))
            {
                col.ByCollection[kv.Value] = col.ByCollection[kv.Key];
                col.ByCollection.Remove(kv.Key);
                int idx = col.CollectionOrder.IndexOf(kv.Key);
                if (idx >= 0) col.CollectionOrder[idx] = kv.Value;
            }
        }
        foreach (var kv in CardNames)
            foreach (var list in col.ByCollection.Values)
                foreach (var e in list)
                    if (e.MaterialName.ToLower()==kv.Key)
                    {
                        // Значення з конфігу беремо ЯК Є: у багатьох записах
                        // " [Alternative]" зашитий прямо у v (config-driven альтернативи).
                        e.DisplayName = kv.Value;
                        // Для Murkwater-типу (дублікат матеріалу, позначений прапорцем
                        // через ALT_IN_SAME_COLLECTION) суфікса в конфігу немає — додаємо
                        // його тут, але лише якщо він ще відсутній (без подвоєння).
                        if (e.IsAlternative && (e.DisplayName == null
                            || !e.DisplayName.EndsWith(" [Alternative]")))
                            e.DisplayName += " [Alternative]";
                    }
    }

    public void ApplyBackNames(List<string> names)
    {
        for (int i=0;i<names.Count;i++)
        { string v; if(CardBackNames.TryGetValue(names[i].ToLower(),out v)) names[i]=v; }
    }

    // ── Helpers ───────────────────────────────────────────────
    static string E(string s) => s.Replace("\\","\\\\").Replace("\"","\\\"");

    static void AppendKV(StringBuilder sb, string name,
        List<string> keys, System.Func<string,string> kFn, System.Func<string,string> vFn)
    {
        sb.AppendLine("  \"" + name + "\": [");
        for (int i=0;i<keys.Count;i++)
            sb.AppendLine("    {\"k\":\""+E(kFn(keys[i]))+"\",\"v\":\""+E(vFn(keys[i]))+"\"}"+(i<keys.Count-1?",":""));
        sb.Append("  ]");
    }

    static void AppendKVList<T>(StringBuilder sb, string name,
        List<T> items, System.Func<T,string> kFn, System.Func<T,string> vFn)
    {
        sb.AppendLine("  \"" + name + "\": [");
        for (int i=0;i<items.Count;i++)
            sb.AppendLine("    {\"k\":\""+E(kFn(items[i]))+"\",\"v\":\""+E(vFn(items[i]))+"\"}"+(i<items.Count-1?",":""));
        sb.Append("  ]");
    }

    [System.Serializable] class KV { public string k; public string v; }
    [System.Serializable] class RawConfig
    {
        public string[] collections_order;
        public KV[] collection_labels, card_names, card_back_names, card_back_collections;
    }
}
