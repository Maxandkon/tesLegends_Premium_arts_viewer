using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Зіставляє матеріали з колекціями (з JSON) та керує кастомними назвами.
/// Колекції: StreamingAssets/collections/*.json
/// </summary>
public class CardCollection
{
    // Default collection display order (viewer_config.json can override)
    public static readonly string[] DEFAULT_COLLECTION_ORDER = new string[]
    {
        // Actual collection names from game JSON files (spaces, not underscores)
        "Core",
        "Madhouse Collection",
        "Dark Brotherhood",
        "Heroes of Skyrim",
        "Clockwork City",
        "Forgotten Hero Collection",
        "Houses of Morrowind",
        "FrostSpark Collection",
        "Isle of Madness",
        "Alliance War",
        "Jaws of Oblivion",
        "Moons of Elsweyr",
        "Tamriel Collection",
        "Realms",
        "Festival of Madness",
        "Monthly",
        "New_Cards",
        "Old_Cards",
        "Unused"
    };
    public class CardEntry
    {
        public string       MaterialName;    // "circle_outcast_premium"
        public string       DisplayName;     // "Pact Outcast" або кастомна
        public string       Collection;
        public int          MaterialIndex;
        public bool         HasCustomName;
        public bool         IsAlternative;   // true лише для DLC-копії ALT_IN_SAME_COLLECTION
        public List<string> AllCardNames = new List<string>(); // для дублікатів артів
    }

    public List<string> CollectionOrder = new List<string>();
    public Dictionary<string, List<CardEntry>> ByCollection
        = new Dictionary<string, List<CardEntry>>();

    private Dictionary<string, string> customNames = new Dictionary<string, string>();
    private string namesPath;

    // viewer_config overrides (loaded in Build)
    public List<string> ConfigCollectionOrder = new List<string>();
    public Dictionary<string, string> ConfigCollectionLabels = new Dictionary<string, string>();
    public List<CardBackEntry> ConfigCardBackEntries = new List<CardBackEntry>();
    public class CardBackEntry { public string key; public string name; public string collection; }

    // Materials where the FIRST (base game) version is canonical
    // DLC versions of these are unused variants, not animated replacements
    // Arts to exclude from all collections (known duplicates/broken entries)
    private static readonly HashSet<string> EXCLUDED_ARTS = new HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "swims-at-night_a_premium", // duplicate of existing art
    };


    private static readonly HashSet<string> FIRST_WINS_MATS = new HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "crushing_grasp_premium", // cp000 = official card, DLC = unused variant
    };

    // Materials where FIRST (Core) stays in collection AND SECOND (DLC) is also kept as [Alternative]
    private static readonly HashSet<string> ALT_IN_SAME_COLLECTION = new HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "murkwater_shaman_premium",
        "murkwater_shaman_mask",
    };

    // Materials where LAST (DLC) version is canonical, FIRST (Core) → Unused
    private static readonly HashSet<string> LAST_WINS_CORE_TO_UNUSED = new HashSet<string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "dbh_brotherhood_sanctuary_premium", // DLC shader = canonical, Core → Unused
    };

    public void Build(List<string> artNames, string streamingAssetsPath)
    {
        namesPath = Path.Combine(Application.persistentDataPath, "card_names.json");
        LoadCustomNames();
        LoadViewerConfig(streamingAssetsPath);

        var matched = new Dictionary<string, CardEntry>(System.StringComparer.OrdinalIgnoreCase);

        string colDir = Path.Combine(streamingAssetsPath, "collections");
        if (Directory.Exists(colDir))
        {
            if (ConfigCollectionOrder.Count > 0)
            {
                foreach (string name in ConfigCollectionOrder)
                {
                    string file = Path.Combine(colDir, name + ".json");
                    if (File.Exists(file)) ParseCollection(file, name, matched);
                }
            }
            else
            {
                foreach (string file in Directory.GetFiles(colDir, "*.json").OrderBy(f => f))
                    ParseCollection(file, Path.GetFileNameWithoutExtension(file), matched);
            }
        }
        else
        {
            // Resources/Collections/ — embedded JSONs for server/offline mode
            var ra = UnityEngine.Resources.LoadAll<UnityEngine.TextAsset>("Collections");
            if (ra != null)
            {
                System.Array.Sort(ra, (a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
                foreach (var t in ra)
                {
                    var col2 = JsonUtility.FromJson<JSONCollection>(t.text);
                    if (col2 == null || col2.Cards == null) continue;
                    ParseCollection(t.text, t.name, matched, true);
                }
            }
        }

        var usedIdx = new HashSet<int>();
        foreach (string col in CollectionOrder)
            if (!ByCollection.ContainsKey(col))
                ByCollection[col] = new List<CardEntry>();

        var addedToCollection = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < artNames.Count; i++)
        {
            string key = artNames[i].ToLower();
            CardEntry e;
            if (!matched.TryGetValue(key, out e)) continue;
            if (EXCLUDED_ARTS.Contains(key)) { usedIdx.Add(i); continue; }

            bool alreadyAdded = addedToCollection.Contains(key);

            if (alreadyAdded && FIRST_WINS_MATS.Contains(key))
            {
                var dup = new CardEntry {
                    MaterialName = artNames[i], DisplayName = e.DisplayName,
                    Collection = "Unused", MaterialIndex = i };
                dup.AllCardNames.Add(dup.DisplayName);
                ApplyCustomName(dup);
                if (!ByCollection.ContainsKey("Unused"))
                    ByCollection["Unused"] = new List<CardEntry>();
                ByCollection["Unused"].Add(dup);
                usedIdx.Add(i);
                continue;
            }

            // ALT_IN_SAME_COLLECTION: first (Core) stays, second (DLC) added as [Alternative]
            if (alreadyAdded && ALT_IN_SAME_COLLECTION.Contains(key))
            {
                var alt = new CardEntry {
                    MaterialName  = artNames[i],
                    DisplayName   = e.DisplayName,
                    Collection    = e.Collection,
                    MaterialIndex = i,
                    IsAlternative = true,   // тільки ця копія — альтернативна
                };
                alt.AllCardNames.Add(alt.DisplayName);
                ApplyCustomName(alt);
                // Суфікс керується прапорцем IsAlternative у ApplyCustomName,
                // тож тут вручну нічого не додаємо (уникаємо подвоєння).
                if (!ByCollection.ContainsKey(alt.Collection))
                    ByCollection[alt.Collection] = new List<CardEntry>();
                ByCollection[alt.Collection].Add(alt);
                usedIdx.Add(i);
                continue;
            }

            if (alreadyAdded && LAST_WINS_CORE_TO_UNUSED.Contains(key))
            {
                var coreEntry = new CardEntry {
                    MaterialName = e.MaterialName, DisplayName = e.DisplayName,
                    Collection = "Unused", MaterialIndex = e.MaterialIndex };
                coreEntry.AllCardNames.Add(coreEntry.DisplayName);
                ApplyCustomName(coreEntry);
                if (!ByCollection.ContainsKey("Unused")) ByCollection["Unused"] = new List<CardEntry>();
                ByCollection["Unused"].Add(coreEntry);
                e.MaterialIndex = i; e.MaterialName = artNames[i]; ApplyCustomName(e);
                usedIdx.Add(i); continue;
            }

            if (!alreadyAdded)
                e.MaterialIndex = i;
            else if (!FIRST_WINS_MATS.Contains(key))
            {
                Debug.Log("[DLC_OVERRIDE] " + e.MaterialName + "  →  " + artNames[i]);
                e.MaterialIndex = i;
            }

            e.MaterialName = artNames[i];
            ApplyCustomName(e);
            usedIdx.Add(i);

            if (!alreadyAdded)
            {
                addedToCollection.Add(key);
                string targetCol = e.Collection;
                if (!ByCollection.ContainsKey(targetCol))
                    ByCollection[targetCol] = new List<CardEntry>();
                ByCollection[targetCol].Add(e);
            }
        }

        // Sort alphabetically within each collection (except Unused)
        foreach (string col in new List<string>(ByCollection.Keys))
        {
            if (col == "Unused") continue;
            ByCollection[col].Sort(CompareEntryName);
        }

        // Sort collection order using default priority list
        CollectionOrder.Sort(CompareCollectionOrder);

        // Ensure Unused exists and is last
        if (!ByCollection.ContainsKey("Unused")) ByCollection["Unused"] = new List<CardEntry>();
        if (!CollectionOrder.Contains("Unused"))  CollectionOrder.Add("Unused");

        // Add unmatched materials to Unused
        for (int i = 0; i < artNames.Count; i++)
        {
            if (usedIdx.Contains(i)) continue;
            var e = new CardEntry {
                MaterialName  = artNames[i],
                DisplayName   = FormatMaterialName(artNames[i]),
                Collection    = "Unused",
                MaterialIndex = i,
            };
            e.AllCardNames.Add(e.DisplayName);
            ApplyCustomName(e);
            ByCollection["Unused"].Add(e);
        }
    }

    void ParseCollection(string fileOrJson, string collName, Dictionary<string, CardEntry> matched, bool isJson=false)
    {
        string raw = isJson ? fileOrJson : File.ReadAllText(fileOrJson, System.Text.Encoding.UTF8);
        var col = JsonUtility.FromJson<JSONCollection>(raw);
        if (col == null || col.Cards == null) return;
        if (!CollectionOrder.Contains(collName)) CollectionOrder.Add(collName);

        foreach (var card in col.Cards)
        {
            if (string.IsNullOrEmpty(card.Image)) continue;
            var parts    = card.Image.Replace('\\','/').Split('/');
            string bname = parts[parts.Length-1].ToLower();
            string mkey  = bname + "_premium";
            string label = string.IsNullOrEmpty(card.Name) ? FormatMaterialName(bname) : card.Name;

            CardEntry existing;
            if (matched.TryGetValue(mkey, out existing))
            {
                // Той самий арт для іншої карти — додаємо назву
                if (!existing.AllCardNames.Contains(label))
                {
                    existing.AllCardNames.Add(label);
                    if (!existing.HasCustomName)
                        existing.DisplayName = string.Join(" | ", existing.AllCardNames.ToArray());
                }
            }
            else
            {
                var e = new CardEntry { MaterialName = mkey, DisplayName = label, Collection = collName };
                e.AllCardNames.Add(label);
                matched[mkey] = e;
            }
        }
    }

    // Пошук: по DisplayName, MaterialName та всіх AllCardNames
    public static bool MatchesSearch(CardEntry entry, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        string f = filter.ToLower();
        if (entry.DisplayName.ToLower().Contains(f))  return true;
        if (entry.MaterialName.ToLower().Contains(f)) return true;
        foreach (string n in entry.AllCardNames)
            if (n.ToLower().Contains(f)) return true;
        return false;
    }

    void ApplyCustomName(CardEntry e)
    {
        string val;
        if (customNames.TryGetValue(e.MaterialName.ToLower(), out val))
        { e.DisplayName = val; e.HasCustomName = true; }

        // Суфікс [Alternative] керується ВИКЛЮЧНО прапорцем IsAlternative,
        // не рядком. Спершу прибираємо будь-який наявний суфікс, потім
        // додаємо рівно один — і лише для alt-копії. Це унеможливлює як
        // подвоєння, так і потрапляння суфікса на неальтернативний запис.
        if (e.DisplayName != null && e.DisplayName.EndsWith(" [Alternative]"))
            e.DisplayName = e.DisplayName.Substring(0, e.DisplayName.Length - " [Alternative]".Length);
        if (e.IsAlternative)
            e.DisplayName += " [Alternative]";
    }

    public void SetCustomName(CardEntry e, string newName)
    {
        e.DisplayName = newName; e.HasCustomName = true;
        customNames[e.MaterialName.ToLower()] = newName;
        SaveCustomNames();
    }

    public void ClearCustomName(CardEntry e)
    {
        e.DisplayName   = e.AllCardNames.Count > 0
            ? string.Join(" | ", e.AllCardNames.ToArray())
            : FormatMaterialName(e.MaterialName);
        e.HasCustomName = false;
        customNames.Remove(e.MaterialName.ToLower());
        SaveCustomNames();
    }

    void LoadCustomNames()
    {
        customNames.Clear();
        if (!File.Exists(namesPath)) return;
        try {
            var w = JsonUtility.FromJson<NamesWrapper>(File.ReadAllText(namesPath));
            if (w?.entries != null)
                foreach (var en in w.entries)
                    customNames[en.key.ToLower()] = en.val;
        } catch (System.Exception ex) { Debug.LogWarning("names: " + ex.Message); }
    }

    void SaveCustomNames()
    {
        var w = new NamesWrapper { entries = new List<NamesWrapper.Entry>() };
        foreach (var kv in customNames)
            w.entries.Add(new NamesWrapper.Entry { key = kv.Key, val = kv.Value });
        File.WriteAllText(namesPath, JsonUtility.ToJson(w, true));
    }

    public static string FormatMaterialName(string raw)
    {
        string s = raw.Replace("_premium","").Replace("_"," ").Replace("-"," ").Trim();
        var sb = new System.Text.StringBuilder(); bool cap = true;
        foreach (char c in s) { sb.Append(cap ? char.ToUpper(c) : c); cap = c == ' '; }
        return sb.ToString();
    }

    void LoadViewerConfig(string sa)
    {
        string path = Path.Combine(sa, "viewer_config.json");
        if (!File.Exists(path)) return;
        try
        {
            string raw = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var cfg = JsonUtility.FromJson<ViewerConfig>(raw);
            if (cfg == null) return;
            if (cfg.collections_order != null)
                foreach (string c in cfg.collections_order)
                    if (!string.IsNullOrEmpty(c) && !c.StartsWith("_"))
                        ConfigCollectionOrder.Add(c);
            if (cfg.collection_labels != null)
                foreach (var kv in cfg.collection_labels)
                    if (!kv.key.StartsWith("_")) ConfigCollectionLabels[kv.key] = kv.val;
            if (cfg.card_art_names != null)
                foreach (var kv in cfg.card_art_names)
                    if (!kv.key.StartsWith("_")) customNames[kv.key.ToLower()] = kv.val;
            if (cfg.card_back_entries != null)
                foreach (var e in cfg.card_back_entries)
                    if (e != null && !string.IsNullOrEmpty(e.key) && !e.key.StartsWith("_"))
                        ConfigCardBackEntries.Add(new CardBackEntry { key=e.key.ToLower(), name=e.name, collection=e.collection });
            UnityEngine.Debug.Log("[Config] viewer_config.json завантажено");
        }
        catch (System.Exception ex) { UnityEngine.Debug.LogWarning("[Config] " + ex.Message); }
    }

    // JSON helpers for viewer_config.json
    [System.Serializable] class ViewerConfig
    {
        public string[] collections_order;
        public ConfigKV[] collection_labels;
        public ConfigKV[] card_art_names;
        public CardBackCfg[] card_back_entries;
    }
    [System.Serializable] class ConfigKV { public string key; public string val; }
    [System.Serializable] class CardBackCfg { public string key; public string name; public string collection; }

    [System.Serializable] class JSONCollection { public string Name; public JSONCard[] Cards; }
    [System.Serializable] class JSONCard { public string Name; public string Image; }
    [System.Serializable] class NamesWrapper
    {
        [System.Serializable] public class Entry { public string key; public string val; }
        public List<Entry> entries;
    }

    public static int CompareCollectionOrder(string a, string b)
    {
        int ia = IndexOfIgnoreCase(DEFAULT_COLLECTION_ORDER, a);
        int ib = IndexOfIgnoreCase(DEFAULT_COLLECTION_ORDER, b);
        if (ia < 0) ia = int.MaxValue;
        if (ib < 0) ib = int.MaxValue;
        if (ia == ib) return string.Compare(a, b, System.StringComparison.OrdinalIgnoreCase);
        return ia.CompareTo(ib);
    }

    static int IndexOfIgnoreCase(string[] arr, string val)
    {
        for (int i = 0; i < arr.Length; i++)
            if (string.Equals(arr[i], val, System.StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public static int CompareEntryName(CardEntry a, CardEntry b)
    {
        return string.Compare(a.DisplayName, b.DisplayName,
            System.StringComparison.OrdinalIgnoreCase);
    }
}