#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Generic PSD-to-UI prefab builder for JSON + PNG exports.
/// The tool has no dependency on puzzle scripts and can be copied to another
/// Unity project together with PsdUiLanguageLayerSwitcher.
/// </summary>
public sealed class GenericPsdUiImporter : EditorWindow
{
    private const string PrefMetadataDir = "GenericPsdUiImporter.MetadataDir";
    private const string PrefSpriteRootDir = "GenericPsdUiImporter.SpriteRootDir";
    private const string PrefOutputDir = "GenericPsdUiImporter.OutputDir";

    private static readonly string[] SkipLayerPrefixes = { "!ref" };

    [SerializeField] private string metadataDir = "Assets/Art/Extracted";
    [SerializeField] private string spriteRootDir = "Assets/Art/Extracted";
    [SerializeField] private string outputDir = "Assets/Resources/UI/Prefabs/Generated";
    [SerializeField] private bool replaceExistingContent = true;
    [SerializeField] private bool prepareSprites = true;
    [SerializeField] private bool convertTmpLayers = true;
    [SerializeField] private bool addLanguageSwitcher = true;
    [SerializeField] private SystemLanguage previewLanguage = SystemLanguage.Korean;
    [SerializeField] private TMP_FontAsset defaultFont;

    private readonly List<JsonEntry> jsonEntries = new();
    private readonly List<PsdUiLanguageLayerSwitcher.Entry> languageEntries = new();
    private Vector2 scroll;
    private string statusMessage = "";
    private int spriteCount;
    private int missingSpriteCount;

    private struct JsonEntry
    {
        public string path;
        public string label;
        public bool selected;
    }

    [MenuItem("Tools/PSD UI Importer/Generic PSD UI Importer", false, 120)]
    public static void ShowWindow()
    {
        var window = GetWindow<GenericPsdUiImporter>("PSD UI Importer");
        window.minSize = new Vector2(460f, 580f);
        window.LoadPrefs();
        window.RefreshJsonList();
    }

    private void OnEnable()
    {
        LoadPrefs();
        RefreshJsonList();
    }

    private void OnDisable()
    {
        SavePrefs();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Generic PSD UI Importer", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Builds UI prefabs from extracted PSD JSON + PNG files. Existing puzzle importers are not touched.",
            MessageType.Info);

        DrawPathField("Metadata JSON Folder", ref metadataDir, true);
        DrawPathField("Sprite Root Folder", ref spriteRootDir, true);
        DrawPathField("Output Prefab Folder", ref outputDir, true);

        EditorGUILayout.Space(6f);
        replaceExistingContent = EditorGUILayout.ToggleLeft("Replace generated children when prefab exists", replaceExistingContent);
        prepareSprites = EditorGUILayout.ToggleLeft("Prepare PNG import settings as UI sprites", prepareSprites);
        convertTmpLayers = EditorGUILayout.ToggleLeft("Convert !tmp layers to TextMeshProUGUI", convertTmpLayers);
        addLanguageSwitcher = EditorGUILayout.ToggleLeft("Add !kr / !en / !jp language switcher", addLanguageSwitcher);
        previewLanguage = (SystemLanguage)EditorGUILayout.EnumPopup("Preview Language", previewLanguage);
        defaultFont = (TMP_FontAsset)EditorGUILayout.ObjectField("Default TMP Font", defaultFont, typeof(TMP_FontAsset), false);

        EditorGUILayout.Space(8f);
        DrawJsonList();

        EditorGUILayout.Space(8f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh JSON List", GUILayout.Height(28f)))
                RefreshJsonList();

            using (new EditorGUI.DisabledScope(jsonEntries.All(e => !e.selected)))
            {
                if (GUILayout.Button("Build Selected UI Prefabs", GUILayout.Height(28f)))
                    BuildSelected();
            }
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(statusMessage, MessageType.None);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawPathField(string label, ref string path, bool folder)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            path = EditorGUILayout.TextField(label, path);
            if (GUILayout.Button("...", GUILayout.Width(28f)))
            {
                string absolute = ToAbsolutePath(path);
                string picked = folder
                    ? EditorUtility.OpenFolderPanel(label, Directory.Exists(absolute) ? absolute : Application.dataPath, "")
                    : EditorUtility.OpenFilePanel(label, Directory.Exists(absolute) ? absolute : Application.dataPath, "json");

                if (!string.IsNullOrEmpty(picked))
                    path = ToAssetPath(picked);
            }
        }
    }

    private void DrawJsonList()
    {
        EditorGUILayout.LabelField("JSON Files", EditorStyles.boldLabel);
        if (jsonEntries.Count == 0)
        {
            EditorGUILayout.HelpBox("No JSON files found. Use the metadata folder generated by your PSD extraction step.", MessageType.Warning);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("All", GUILayout.Width(44f)))
                SetAllSelected(true);
            if (GUILayout.Button("None", GUILayout.Width(52f)))
                SetAllSelected(false);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{jsonEntries.Count} file(s)", GUILayout.Width(80f));
        }

        for (int i = 0; i < jsonEntries.Count; i++)
        {
            var entry = jsonEntries[i];
            bool selected = EditorGUILayout.ToggleLeft(entry.label, entry.selected);
            if (selected != entry.selected)
            {
                entry.selected = selected;
                jsonEntries[i] = entry;
            }
        }
    }

    private void SetAllSelected(bool selected)
    {
        for (int i = 0; i < jsonEntries.Count; i++)
        {
            var entry = jsonEntries[i];
            entry.selected = selected;
            jsonEntries[i] = entry;
        }
    }

    private void RefreshJsonList()
    {
        jsonEntries.Clear();

        string absoluteDir = ToAbsolutePath(metadataDir);
        if (!Directory.Exists(absoluteDir))
        {
            statusMessage = $"Metadata folder not found: {metadataDir}";
            return;
        }

        foreach (string path in Directory.GetFiles(absoluteDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.Equals("_all_meta.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string assetPath = ToAssetPath(path);
            jsonEntries.Add(new JsonEntry
            {
                path = assetPath,
                label = fileName,
                selected = true,
            });
        }

        statusMessage = jsonEntries.Count > 0
            ? $"Found {jsonEntries.Count} JSON file(s)."
            : $"No JSON files in {metadataDir}.";
    }

    private void BuildSelected()
    {
        SavePrefs();
        Directory.CreateDirectory(ToAbsolutePath(outputDir));

        int built = 0;
        spriteCount = 0;
        missingSpriteCount = 0;

        foreach (var entry in jsonEntries.Where(e => e.selected))
        {
            try
            {
                BuildOne(entry.path);
                built++;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError($"[GenericPsdUiImporter] Failed: {entry.path}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        statusMessage = $"Built {built} UI prefab(s). Sprites {spriteCount}, missing {missingSpriteCount}. Output: {outputDir}";
        Debug.Log($"[GenericPsdUiImporter] {statusMessage}");
    }

    private void BuildOne(string jsonAssetPath)
    {
        string json = File.ReadAllText(ToAbsolutePath(jsonAssetPath));
        JObject meta = JObject.Parse(json);

        string source = meta["source"]?.ToString();
        string prefabName = !string.IsNullOrEmpty(source)
            ? Path.GetFileNameWithoutExtension(source)
            : Path.GetFileNameWithoutExtension(jsonAssetPath);

        int canvasW = meta["canvas"]?["width"]?.Value<int>() ?? 1920;
        int canvasH = meta["canvas"]?["height"]?.Value<int>() ?? 1080;
        var layers = meta["layers"] as JArray ?? new JArray();
        string prefabPath = $"{outputDir.TrimEnd('/', '\\')}/{prefabName}.prefab".Replace('\\', '/');

        GameObject root;
        bool updateExisting = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;

        if (updateExisting)
        {
            root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (replaceExistingContent)
                ClearChildren(root.transform);
        }
        else
        {
            root = new GameObject(prefabName, typeof(RectTransform));
        }

        var rootRect = root.GetComponent<RectTransform>() ?? root.AddComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = new Vector2(canvasW, canvasH);

        languageEntries.Clear();
        BuildLayerTree(rootRect, layers, canvasW, canvasH);
        ConfigureLanguageSwitcher(root);

        if (updateExisting)
        {
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
        }
        else
        {
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            DestroyImmediate(root);
        }
    }

    private void BuildLayerTree(RectTransform parent, JArray layers, int canvasW, int canvasH)
    {
        foreach (JObject layer in layers)
        {
            string layerName = layer["name"]?.ToString() ?? "Layer";
            if (ShouldSkipLayer(layerName)) continue;

            bool visible = layer["visible"]?.Value<bool>() ?? true;
            string kind = layer["kind"]?.ToString() ?? "";
            bool isGroup = kind.Equals("group", StringComparison.OrdinalIgnoreCase);
            bool isTmp = convertTmpLayers && HasToken(layerName, "!tmp");

            var go = new GameObject(layerName, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            go.SetActive(visible);

            if (isGroup)
            {
                StretchToParent(rect);
                var children = layer["children"] as JArray;
                if (children != null)
                    BuildLayerTree(rect, children, canvasW, canvasH);
            }
            else
            {
                ApplyBbox(rect, layer["bbox"], canvasW, canvasH);

                if (isTmp)
                {
                    AddTmpText(go);
                }
                else
                {
                    AddImage(go, layer);
                }
            }

            if (TryGetLanguage(layerName, out var language))
            {
                languageEntries.Add(new PsdUiLanguageLayerSwitcher.Entry
                {
                    target = go,
                    language = language,
                });
            }
        }
    }

    private void ConfigureLanguageSwitcher(GameObject root)
    {
        var existing = root.GetComponent<PsdUiLanguageLayerSwitcher>();
        if (!addLanguageSwitcher || languageEntries.Count == 0)
        {
            if (existing != null)
                DestroyImmediate(existing);
            return;
        }

        var switcher = existing != null ? existing : root.AddComponent<PsdUiLanguageLayerSwitcher>();
        switcher.SetEntries(languageEntries.ToArray(), previewLanguage);
        EditorUtility.SetDirty(switcher);
    }

    private void AddTmpText(GameObject go)
    {
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = "";
        tmp.raycastTarget = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 8f;
        tmp.fontSizeMax = 80f;
        tmp.color = Color.black;
        if (defaultFont != null)
            tmp.font = defaultFont;
    }

    private void AddImage(GameObject go, JObject layer)
    {
        string pngRel = layer["png"]?.ToString();
        if (string.IsNullOrEmpty(pngRel)) return;

        pngRel = pngRel.Replace('\\', '/');
        string spritePath = $"{spriteRootDir.TrimEnd('/', '\\')}/{pngRel}".Replace('\\', '/');

        if (prepareSprites)
            PrepareSprite(spritePath);

        Sprite sprite = LoadSprite(spritePath);
        if (sprite == null)
        {
            missingSpriteCount++;
            Debug.LogWarning($"[GenericPsdUiImporter] Sprite not found: {spritePath}");
            return;
        }

        var image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.type = Image.Type.Simple;
        image.preserveAspect = false;
        image.raycastTarget = false;
        spriteCount++;
    }

    private static void StretchToParent(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.pivot = new Vector2(0.5f, 0.5f);
    }

    private static void ApplyBbox(RectTransform rect, JToken bbox, int canvasW, int canvasH)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);

        if (bbox == null)
        {
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            return;
        }

        float left = bbox["left"]?.Value<float>() ?? 0f;
        float top = bbox["top"]?.Value<float>() ?? 0f;
        float right = bbox["right"]?.Value<float>() ?? left;
        float bottom = bbox["bottom"]?.Value<float>() ?? top;
        float width = right - left;
        float height = bottom - top;

        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(
            left + width * 0.5f - canvasW * 0.5f,
            canvasH * 0.5f - (top + height * 0.5f));
    }

    private static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
            DestroyImmediate(root.GetChild(i).gameObject);
    }

    private static bool ShouldSkipLayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string trimmed = name.TrimStart();
        return SkipLayerPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetLanguage(string name, out SystemLanguage language)
    {
        if (HasToken(name, "!kr"))
        {
            language = SystemLanguage.Korean;
            return true;
        }

        if (HasToken(name, "!jp"))
        {
            language = SystemLanguage.Japanese;
            return true;
        }

        if (HasToken(name, "!en"))
        {
            language = SystemLanguage.English;
            return true;
        }

        language = SystemLanguage.Unknown;
        return false;
    }

    private static bool HasToken(string name, string token)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Sprite LoadSprite(string assetPath)
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        if (sprite != null) return sprite;

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
        {
            if (asset is Sprite found)
                return found;
        }

        return null;
    }

    private static void PrepareSprite(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return;

        bool dirty = false;
        if (importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            dirty = true;
        }

        if (importer.spriteImportMode != SpriteImportMode.Single)
        {
            importer.spriteImportMode = SpriteImportMode.Single;
            dirty = true;
        }

        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            dirty = true;
        }

        if (importer.alphaIsTransparency == false)
        {
            importer.alphaIsTransparency = true;
            dirty = true;
        }

        if (dirty)
            importer.SaveAndReimport();
    }

    private void LoadPrefs()
    {
        metadataDir = EditorPrefs.GetString(PrefMetadataDir, metadataDir);
        spriteRootDir = EditorPrefs.GetString(PrefSpriteRootDir, spriteRootDir);
        outputDir = EditorPrefs.GetString(PrefOutputDir, outputDir);
    }

    private void SavePrefs()
    {
        EditorPrefs.SetString(PrefMetadataDir, metadataDir);
        EditorPrefs.SetString(PrefSpriteRootDir, spriteRootDir);
        EditorPrefs.SetString(PrefOutputDir, outputDir);
    }

    private static string ToAbsolutePath(string assetPath)
    {
        if (Path.IsPathRooted(assetPath))
            return assetPath;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    private static string ToAssetPath(string absolutePath)
    {
        absolutePath = absolutePath.Replace('\\', '/');
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
        if (absolutePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            return absolutePath.Substring(projectRoot.Length + 1);
        return absolutePath;
    }
}
#endif
