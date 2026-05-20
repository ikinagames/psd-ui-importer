#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

/// <summary>
/// Generic PSD-to-UI prefab builder for JSON + PNG exports.
/// The tool has no dependency on puzzle scripts and can be copied to another
/// Unity project together with PsdUiLanguageLayerSwitcher.
/// </summary>
public sealed class GenericPsdUiImporter : EditorWindow
{
    private const string SettingsAssetPath = "Assets/Editor/PsdUiImporterSettings.asset";

    private static readonly string[] SkipLayerPrefixes = { "!ref" };

    [SerializeField] private GenericPsdUiImporterSettings settingsAsset;
    [SerializeField] private string psdSourceDir = "Assets/Art/PSD";
    [SerializeField] private string metadataDir = "Assets/Art/Extracted";
    [SerializeField] private string spriteRootDir = "Assets/Art/Extracted";
    [SerializeField] private string outputDir = "Assets/Resources/UI/Prefabs/Generated";
    [SerializeField] private float exportScale = 1f;
    [SerializeField] private int exportMaxDim;
    [SerializeField] private bool exportPotSnap;
    [SerializeField] private float exportPotThreshold = 1.05f;
    [SerializeField] private bool exportForceTopil;
    [SerializeField] private int exportSnapAlpha = -1;
    [SerializeField] private bool replaceExistingContent = true;
    [SerializeField] private bool prepareSprites = true;
    [SerializeField] private bool convertTmpLayers = true;
    [SerializeField] private bool addLanguageSwitcher = true;
    [SerializeField] private SystemLanguage previewLanguage = SystemLanguage.Korean;
    [SerializeField] private TMP_FontAsset defaultFont;
    [SerializeField] private bool useSpriteAtlas;
    [SerializeField] private string spriteAtlasPath = "Assets/Art/Extracted/PsdUiImporter.spriteatlas";
    [SerializeField] private bool createSpriteAtlasIfMissing = true;
    [SerializeField] private bool packSpriteAtlasAfterBuild;

    private readonly List<JsonEntry> jsonEntries = new();
    private readonly List<PsdUiLanguageLayerSwitcher.Entry> languageEntries = new();
    private Vector2 scroll;
    private string statusMessage = "";
    private int spriteCount;
    private int missingSpriteCount;
    private int activeTab;
    private readonly PsdOrphanedImageCleanerPanel orphanedImageCleaner = new();

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
        activeTab = GUILayout.Toolbar(activeTab, new[] { "Build UI", "Settings", "Cleanup Images" });
        EditorGUILayout.Space(4f);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (activeTab == 0)
            DrawBuildUiTab();
        else if (activeTab == 1)
            DrawSettingsTab();
        else
            orphanedImageCleaner.Draw();
        EditorGUILayout.EndScrollView();
    }

    private void DrawBuildUiTab()
    {
        EditorGUILayout.LabelField("Generic PSD UI Importer", EditorStyles.boldLabel);
        DrawSettingsAssetField();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Build Selected UI Prefabs", GUILayout.Height(32f)))
                BuildSelected();

            if (GUILayout.Button("Extract PSDs + Build", GUILayout.Height(32f)))
            {
                if (ExtractPsdFiles())
                    BuildSelected();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh JSON List", GUILayout.Height(26f)))
                RefreshJsonList();

            if (GUILayout.Button("Extract PSDs", GUILayout.Height(26f)))
                ExtractPsdFiles();

            using (new EditorGUI.DisabledScope(!useSpriteAtlas))
            {
                if (GUILayout.Button("Update Atlas", GUILayout.Height(26f)))
                    UpdateSpriteAtlas();
            }
        }

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Build From Extracted JSON", EditorStyles.boldLabel);
        DrawPathField("Metadata Folder", ref metadataDir, true);
        DrawPathField("Sprite Root Folder", ref spriteRootDir, true);
        DrawPathField("Output Prefab Folder", ref outputDir, true);

        EditorGUILayout.Space(6f);
        DrawJsonList();

        DrawStatusMessage();
    }

    private void DrawSettingsTab()
    {
        EditorGUILayout.LabelField("Project Settings", EditorStyles.boldLabel);
        DrawSettingsAssetField();

        EditorGUILayout.LabelField("PSD Extract", EditorStyles.boldLabel);
        DrawPathField("Source PSD Folder", ref psdSourceDir, true);
        DrawPathField("Extract Output Folder", ref metadataDir, true);
        exportScale = Mathf.Max(0.01f, EditorGUILayout.FloatField("Export Scale", exportScale));
        exportMaxDim = Mathf.Max(0, EditorGUILayout.IntField("Max Image Dimension", exportMaxDim));
        exportPotSnap = EditorGUILayout.ToggleLeft("Snap near power-of-two textures", exportPotSnap);
        using (new EditorGUI.DisabledScope(!exportPotSnap))
            exportPotThreshold = Mathf.Max(1.001f, EditorGUILayout.FloatField("POT Threshold", exportPotThreshold));
        exportForceTopil = EditorGUILayout.ToggleLeft("Force topil export only", exportForceTopil);
        exportSnapAlpha = EditorGUILayout.IntField("Snap Alpha Threshold (-1 off)", exportSnapAlpha);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Build UI", EditorStyles.boldLabel);
        DrawPathField("Sprite Root Folder", ref spriteRootDir, true);
        DrawPathField("Output Prefab Folder", ref outputDir, true);

        EditorGUILayout.Space(6f);
        replaceExistingContent = EditorGUILayout.ToggleLeft("Replace generated children when prefab exists", replaceExistingContent);
        prepareSprites = EditorGUILayout.ToggleLeft("Prepare PNG import settings as UI sprites", prepareSprites);
        convertTmpLayers = EditorGUILayout.ToggleLeft("Convert !tmp layers to TextMeshProUGUI", convertTmpLayers);
        addLanguageSwitcher = EditorGUILayout.ToggleLeft("Add !kr / !en / !jp language switcher", addLanguageSwitcher);
        previewLanguage = (SystemLanguage)EditorGUILayout.EnumPopup("Preview Language", previewLanguage);
        defaultFont = (TMP_FontAsset)EditorGUILayout.ObjectField("Default TMP Font", defaultFont, typeof(TMP_FontAsset), false);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Sprite Atlas", EditorStyles.boldLabel);
        useSpriteAtlas = EditorGUILayout.ToggleLeft("Use Sprite Atlas", useSpriteAtlas);
        using (new EditorGUI.DisabledScope(!useSpriteAtlas))
        {
            DrawPathField("Atlas Asset", ref spriteAtlasPath, false, "spriteatlas");
            createSpriteAtlasIfMissing = EditorGUILayout.ToggleLeft("Create atlas asset if missing", createSpriteAtlasIfMissing);
            packSpriteAtlasAfterBuild = EditorGUILayout.ToggleLeft("Pack atlas after build", packSpriteAtlasAfterBuild);
        }

        EditorGUILayout.Space(8f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Save Settings", GUILayout.Height(28f)))
                SaveSettingsAndReport();

            if (GUILayout.Button("Create Folders", GUILayout.Height(28f)))
                CreateConfiguredFolders();

            using (new EditorGUI.DisabledScope(!useSpriteAtlas))
            {
                if (GUILayout.Button("Update Atlas", GUILayout.Height(28f)))
                    UpdateSpriteAtlas();
            }
        }

        DrawStatusMessage();
    }

    private void DrawSettingsAssetField()
    {
        using (var change = new EditorGUI.ChangeCheckScope())
        {
            var pickedSettings = (GenericPsdUiImporterSettings)EditorGUILayout.ObjectField("Settings Asset", settingsAsset, typeof(GenericPsdUiImporterSettings), false);
            if (change.changed && pickedSettings != null)
            {
                settingsAsset = pickedSettings;
                LoadPrefs();
                RefreshJsonList();
            }
        }
    }

    private void DrawStatusMessage()
    {
        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(statusMessage, MessageType.None);
        }
    }

    private void DrawPathField(string label, ref string path, bool folder, string extension = "json")
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            path = EditorGUILayout.TextField(label, path);
            if (GUILayout.Button("...", GUILayout.Width(28f)))
            {
                string absolute = ToAbsolutePath(path);
                string picked = folder
                    ? EditorUtility.OpenFolderPanel(label, Directory.Exists(absolute) ? absolute : Application.dataPath, "")
                    : EditorUtility.OpenFilePanel(label, File.Exists(absolute) ? Path.GetDirectoryName(absolute) : Application.dataPath, extension);

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
        string atlasStatus = null;
        if (useSpriteAtlas)
        {
            UpdateSpriteAtlas();
            atlasStatus = statusMessage;
        }

        statusMessage = $"Built {built} UI prefab(s). Sprites {spriteCount}, missing {missingSpriteCount}. Output: {outputDir}";
        if (!string.IsNullOrEmpty(atlasStatus))
            statusMessage += $"\n{atlasStatus}";
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

    private void UpdateSpriteAtlas()
    {
        SavePrefs();

        if (!useSpriteAtlas)
        {
            statusMessage = "Sprite Atlas is disabled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(spriteAtlasPath))
        {
            statusMessage = "Sprite Atlas path is empty.";
            return;
        }

        string normalizedAtlasPath = spriteAtlasPath.Replace('\\', '/');
        if (!normalizedAtlasPath.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase))
            normalizedAtlasPath += ".spriteatlas";
        spriteAtlasPath = normalizedAtlasPath;

        var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(spriteAtlasPath);
        if (atlas == null)
        {
            if (!createSpriteAtlasIfMissing)
            {
                statusMessage = $"Sprite Atlas not found: {spriteAtlasPath}";
                return;
            }

            string atlasDirectory = Path.GetDirectoryName(spriteAtlasPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(atlasDirectory))
                Directory.CreateDirectory(ToAbsolutePath(atlasDirectory));

            atlas = new SpriteAtlas();
            AssetDatabase.CreateAsset(atlas, spriteAtlasPath);
        }

        string spriteFolder = spriteRootDir.Replace('\\', '/').TrimEnd('/');
        if (!AssetDatabase.IsValidFolder(spriteFolder))
        {
            statusMessage = $"Sprite root folder not found: {spriteRootDir}";
            return;
        }

        var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(spriteFolder);
        if (folderAsset == null)
        {
            statusMessage = $"Sprite root folder could not be loaded: {spriteRootDir}";
            return;
        }

        var packables = SpriteAtlasExtensions.GetPackables(atlas);
        if (!packables.Contains(folderAsset))
            SpriteAtlasExtensions.Add(atlas, new[] { folderAsset });

        EditorUtility.SetDirty(atlas);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (packSpriteAtlasAfterBuild)
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget, false);

        statusMessage = $"Updated Sprite Atlas: {spriteAtlasPath}";
    }

    private void SaveSettingsAndReport()
    {
        SavePrefs();
        string path = AssetDatabase.GetAssetPath(settingsAsset);
        statusMessage = $"Saved settings: {(string.IsNullOrEmpty(path) ? SettingsAssetPath : path)}";
    }

    private void CreateConfiguredFolders()
    {
        SavePrefs();

        CreateFolderIfPathIsRelative(psdSourceDir);
        CreateFolderIfPathIsRelative(metadataDir);
        CreateFolderIfPathIsRelative(spriteRootDir);
        CreateFolderIfPathIsRelative(outputDir);

        AssetDatabase.Refresh();
        statusMessage = "Created configured folders.";
    }

    private static void CreateFolderIfPathIsRelative(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || Path.IsPathRooted(assetPath))
            return;

        Directory.CreateDirectory(ToAbsolutePath(assetPath));
    }

    private bool ExtractPsdFiles()
    {
        SavePrefs();

        string sourceDir = ToAbsolutePath(psdSourceDir);
        if (!Directory.Exists(sourceDir))
        {
            EditorUtility.DisplayDialog("PSD Extract", $"Source PSD folder not found:\n{psdSourceDir}", "OK");
            statusMessage = $"Source PSD folder not found: {psdSourceDir}";
            return false;
        }

        string scriptPath = GetExtractScriptPath();
        if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
        {
            EditorUtility.DisplayDialog("PSD Extract", "extract_psd.py was not found inside the package.", "OK");
            statusMessage = "extract_psd.py was not found inside the package.";
            return false;
        }

        if (!FindPython(out string pythonCommand, out bool usePyLauncher))
        {
            EditorUtility.DisplayDialog(
                "PSD Extract",
                "Python 3.x was not found. Install Python, then run:\n\npip install psd-tools pillow",
                "OK");
            statusMessage = "Python 3.x not found. Install Python and psd-tools/pillow.";
            return false;
        }

        string outputDirAbsolute = ToAbsolutePath(metadataDir);
        Directory.CreateDirectory(outputDirAbsolute);

        string scaleText = exportScale.ToString("0.####", CultureInfo.InvariantCulture);
        string thresholdText = exportPotThreshold.ToString("0.####", CultureInfo.InvariantCulture);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonCommand,
                WorkingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (usePyLauncher)
                startInfo.ArgumentList.Add("-3");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--psd-dir");
            startInfo.ArgumentList.Add(sourceDir);
            startInfo.ArgumentList.Add("--out-dir");
            startInfo.ArgumentList.Add(outputDirAbsolute);
            startInfo.ArgumentList.Add("--scale");
            startInfo.ArgumentList.Add(scaleText);
            startInfo.ArgumentList.Add("--max-dim");
            startInfo.ArgumentList.Add(exportMaxDim.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--pot-threshold");
            startInfo.ArgumentList.Add(thresholdText);
            if (exportPotSnap)
                startInfo.ArgumentList.Add("--pot-snap");
            if (exportForceTopil)
                startInfo.ArgumentList.Add("--force-topil");
            if (exportSnapAlpha >= 0)
            {
                startInfo.ArgumentList.Add("--snap-alpha");
                startInfo.ArgumentList.Add(Mathf.Clamp(exportSnapAlpha, 0, 255).ToString(CultureInfo.InvariantCulture));
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                statusMessage = "Failed to start Python process.";
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
                Debug.Log($"[GenericPsdUiImporter] PSD extract output:\n{stdout}");
            if (!string.IsNullOrWhiteSpace(stderr))
                Debug.LogWarning($"[GenericPsdUiImporter] PSD extract warnings:\n{stderr}");

            if (process.ExitCode != 0)
            {
                statusMessage = $"PSD extract failed. Exit code {process.ExitCode}. See Console.";
                EditorUtility.DisplayDialog("PSD Extract", statusMessage, "OK");
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            statusMessage = $"PSD extract failed: {ex.Message}";
            EditorUtility.DisplayDialog("PSD Extract", statusMessage, "OK");
            return false;
        }

        AssetDatabase.Refresh();
        spriteRootDir = metadataDir;
        RefreshJsonList();
        statusMessage = $"PSD extract complete. Output: {metadataDir}";
        return true;
    }

    private static string GetExtractScriptPath()
    {
        var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GenericPsdUiImporter).Assembly);
        if (packageInfo == null || string.IsNullOrEmpty(packageInfo.resolvedPath))
            return null;

        return Path.Combine(packageInfo.resolvedPath, "Editor", "Tools", "extract_psd.py");
    }

    private static bool FindPython(out string command, out bool usePyLauncher)
    {
        if (TryPythonCommand("python", "--version"))
        {
            command = "python";
            usePyLauncher = false;
            return true;
        }

        if (TryPythonCommand("python3", "--version"))
        {
            command = "python3";
            usePyLauncher = false;
            return true;
        }

        if (TryPythonCommand("py", "-3 --version"))
        {
            command = "py";
            usePyLauncher = true;
            return true;
        }

        command = null;
        usePyLauncher = false;
        return false;
    }

    private static bool TryPythonCommand(string command, string arguments)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return false;
            process.WaitForExit(3000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }



    private void LoadPrefs()
    {
        settingsAsset = settingsAsset != null ? settingsAsset : LoadOrCreateSettingsAsset();
        if (settingsAsset == null)
            return;

        psdSourceDir = settingsAsset.psdSourceDir;
        metadataDir = settingsAsset.metadataDir;
        spriteRootDir = settingsAsset.spriteRootDir;
        outputDir = settingsAsset.outputDir;
        exportScale = settingsAsset.exportScale;
        exportMaxDim = settingsAsset.exportMaxDim;
        exportPotSnap = settingsAsset.exportPotSnap;
        exportPotThreshold = settingsAsset.exportPotThreshold;
        exportForceTopil = settingsAsset.exportForceTopil;
        exportSnapAlpha = settingsAsset.exportSnapAlpha;
        replaceExistingContent = settingsAsset.replaceExistingContent;
        prepareSprites = settingsAsset.prepareSprites;
        convertTmpLayers = settingsAsset.convertTmpLayers;
        addLanguageSwitcher = settingsAsset.addLanguageSwitcher;
        previewLanguage = settingsAsset.previewLanguage;
        defaultFont = settingsAsset.defaultFont;
        useSpriteAtlas = settingsAsset.useSpriteAtlas;
        spriteAtlasPath = string.IsNullOrWhiteSpace(settingsAsset.spriteAtlasPath)
            ? "Assets/Art/Extracted/PsdUiImporter.spriteatlas"
            : settingsAsset.spriteAtlasPath;
        createSpriteAtlasIfMissing = settingsAsset.createSpriteAtlasIfMissing;
        packSpriteAtlasAfterBuild = settingsAsset.packSpriteAtlasAfterBuild;
    }

    private void SavePrefs()
    {
        settingsAsset = settingsAsset != null ? settingsAsset : LoadOrCreateSettingsAsset();
        if (settingsAsset == null)
            return;

        settingsAsset.psdSourceDir = psdSourceDir;
        settingsAsset.metadataDir = metadataDir;
        settingsAsset.spriteRootDir = spriteRootDir;
        settingsAsset.outputDir = outputDir;
        settingsAsset.exportScale = exportScale;
        settingsAsset.exportMaxDim = exportMaxDim;
        settingsAsset.exportPotSnap = exportPotSnap;
        settingsAsset.exportPotThreshold = exportPotThreshold;
        settingsAsset.exportForceTopil = exportForceTopil;
        settingsAsset.exportSnapAlpha = exportSnapAlpha;
        settingsAsset.replaceExistingContent = replaceExistingContent;
        settingsAsset.prepareSprites = prepareSprites;
        settingsAsset.convertTmpLayers = convertTmpLayers;
        settingsAsset.addLanguageSwitcher = addLanguageSwitcher;
        settingsAsset.previewLanguage = previewLanguage;
        settingsAsset.defaultFont = defaultFont;
        settingsAsset.useSpriteAtlas = useSpriteAtlas;
        settingsAsset.spriteAtlasPath = spriteAtlasPath;
        settingsAsset.createSpriteAtlasIfMissing = createSpriteAtlasIfMissing;
        settingsAsset.packSpriteAtlasAfterBuild = packSpriteAtlasAfterBuild;

        EditorUtility.SetDirty(settingsAsset);
        AssetDatabase.SaveAssets();
    }

    private static GenericPsdUiImporterSettings LoadOrCreateSettingsAsset()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GenericPsdUiImporterSettings>(SettingsAssetPath);
        if (asset != null)
            return asset;

        string directory = Path.GetDirectoryName(SettingsAssetPath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(ToAbsolutePath(directory));

        asset = CreateInstance<GenericPsdUiImporterSettings>();
        AssetDatabase.CreateAsset(asset, SettingsAssetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[GenericPsdUiImporter] Created project settings asset: {SettingsAssetPath}");
        return asset;
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

public sealed class GenericPsdUiImporterSettings : ScriptableObject
{
    public string psdSourceDir = "Assets/Art/PSD";
    public string metadataDir = "Assets/Art/Extracted";
    public string spriteRootDir = "Assets/Art/Extracted";
    public string outputDir = "Assets/Resources/UI/Prefabs/Generated";
    public float exportScale = 1f;
    public int exportMaxDim;
    public bool exportPotSnap;
    public float exportPotThreshold = 1.05f;
    public bool exportForceTopil;
    public int exportSnapAlpha = -1;
    public bool replaceExistingContent = true;
    public bool prepareSprites = true;
    public bool convertTmpLayers = true;
    public bool addLanguageSwitcher = true;
    public SystemLanguage previewLanguage = SystemLanguage.Korean;
    public TMP_FontAsset defaultFont;
    public bool useSpriteAtlas;
    public string spriteAtlasPath = "Assets/Art/Extracted/PsdUiImporter.spriteatlas";
    public bool createSpriteAtlasIfMissing = true;
    public bool packSpriteAtlasAfterBuild;
}
#endif
