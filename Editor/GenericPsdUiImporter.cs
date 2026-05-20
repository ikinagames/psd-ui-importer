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
    [SerializeField] private string itemPrefabOutputDir = "Assets/Resources/UI/Prefabs/Generated/Items";
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
    [SerializeField] private bool addTextLocalizer;
    [SerializeField] private MonoScript textLocalizerScript;
    [SerializeField] private SystemLanguage previewLanguage = SystemLanguage.Korean;
    [SerializeField] private TMP_FontAsset defaultFont;
    [SerializeField] private bool useSpriteAtlas;
    [SerializeField] private string spriteAtlasOutputDir = "Assets/Art/Extracted/Atlases";
    [SerializeField] private bool createSpriteAtlasIfMissing = true;
    [SerializeField] private bool packSpriteAtlasAfterBuild;
    [SerializeField] private GameObject auditPrefab;
    [SerializeField] private bool auditIncludeFolderTextures = true;
    [SerializeField] private bool auditShowUsage = true;
    [SerializeField] private bool auditShowImportSettings = true;
    [SerializeField] private bool auditShowPlatformSettings;
    [SerializeField] private bool auditShowAtlas = true;
    [SerializeField] private bool showTagReference;

    private readonly List<JsonEntry> jsonEntries = new();
    private readonly List<PsdUiLanguageLayerSwitcher.Entry> languageEntries = new();
    private readonly List<PsdUiTextLocalizerBase.Entry> textEntries = new();
    private readonly List<ItemPrefabEntry> itemPrefabEntries = new();
    private readonly List<TextureAuditRow> textureAuditRows = new();
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

    private sealed class TextureAuditRow
    {
        public string path;
        public string name;
        public string usage;
        public bool usedByPrefab;
        public TextureImporter importer;
        public Texture2D texture;
        public Sprite sprite;
        public bool inConfiguredAtlas;
    }

    private sealed class ItemPrefabEntry
    {
        public GameObject root;
        public string layerName;
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
        activeTab = GUILayout.Toolbar(activeTab, new[] { "Build UI", "Settings", "Texture Audit", "Cleanup Images" });
        EditorGUILayout.Space(4f);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (activeTab == 0)
            DrawBuildUiTab();
        else if (activeTab == 1)
            DrawSettingsTab();
        else if (activeTab == 2)
            DrawTextureAuditTab();
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
        DrawPathField("Item Prefab Folder", ref itemPrefabOutputDir, true);

        EditorGUILayout.Space(6f);
        replaceExistingContent = EditorGUILayout.ToggleLeft("Replace generated children when prefab exists", replaceExistingContent);
        prepareSprites = EditorGUILayout.ToggleLeft("Prepare PNG import settings as UI sprites", prepareSprites);
        convertTmpLayers = EditorGUILayout.ToggleLeft("Convert !tmp layers to TextMeshProUGUI", convertTmpLayers);
        addLanguageSwitcher = EditorGUILayout.ToggleLeft("Add !kr / !en / !jp language switcher", addLanguageSwitcher);
        addTextLocalizer = EditorGUILayout.ToggleLeft("Add project text localizer for !tmp layers", addTextLocalizer);
        using (new EditorGUI.DisabledScope(!addTextLocalizer))
            textLocalizerScript = (MonoScript)EditorGUILayout.ObjectField("Text Localizer Script", textLocalizerScript, typeof(MonoScript), false);
        previewLanguage = (SystemLanguage)EditorGUILayout.EnumPopup("Preview Language", previewLanguage);
        defaultFont = (TMP_FontAsset)EditorGUILayout.ObjectField("Default TMP Font", defaultFont, typeof(TMP_FontAsset), false);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Sprite Atlas", EditorStyles.boldLabel);
        useSpriteAtlas = EditorGUILayout.ToggleLeft("Use Sprite Atlas", useSpriteAtlas);
        using (new EditorGUI.DisabledScope(!useSpriteAtlas))
        {
            DrawPathField("Atlas Output Folder", ref spriteAtlasOutputDir, true);
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

        DrawTagReference();
        DrawStatusMessage();
    }

    private void DrawTagReference()
    {
        EditorGUILayout.Space(10f);
        showTagReference = EditorGUILayout.Foldout(showTagReference, "Tag Reference", true);
        if (!showTagReference)
            return;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            DrawTagHelp("!tmp", "Create a TextMeshProUGUI object instead of an Image.");
            DrawTagHelp("!btn", "Add Button and set Transition to None.");
            DrawTagHelp("!item", "Save this subtree as a separate item prefab.");
            DrawTagHelp("!mask", "Add Mask.");
            DrawTagHelp("!cg", "Add CanvasGroup.");
            DrawTagHelp("!kr / !en / !jp", "Register language-specific layer entries.");
            DrawTagHelp("!ref", "Skip this reference layer/tree during extraction and build.");
            DrawTagHelp("!x1.5", "Override extraction scale for this layer image.");
            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField("Example", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("aa.psd + !item bb -> aa_bb_Item.prefab");
        }
    }

    private static void DrawTagHelp(string tag, string description)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(tag, EditorStyles.boldLabel, GUILayout.Width(110f));
            EditorGUILayout.LabelField(description);
        }
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

    private void DrawTextureAuditTab()
    {
        EditorGUILayout.LabelField("Texture Audit", EditorStyles.boldLabel);
        auditPrefab = (GameObject)EditorGUILayout.ObjectField("Prefab", auditPrefab, typeof(GameObject), false);
        auditIncludeFolderTextures = EditorGUILayout.ToggleLeft("Include textures in matching PSD folder", auditIncludeFolderTextures);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Use Selected Prefab", GUILayout.Height(26f)))
                auditPrefab = Selection.activeObject as GameObject;

            if (GUILayout.Button("Refresh", GUILayout.Height(26f)))
                RefreshTextureAudit();
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Columns", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            auditShowUsage = EditorGUILayout.ToggleLeft("Usage", auditShowUsage, GUILayout.Width(80f));
            auditShowImportSettings = EditorGUILayout.ToggleLeft("Import", auditShowImportSettings, GUILayout.Width(80f));
            auditShowPlatformSettings = EditorGUILayout.ToggleLeft("Platform", auditShowPlatformSettings, GUILayout.Width(90f));
            auditShowAtlas = EditorGUILayout.ToggleLeft("Atlas", auditShowAtlas, GUILayout.Width(80f));
        }

        EditorGUILayout.Space(6f);
        DrawTextureAuditRows();
        DrawStatusMessage();
    }

    private void DrawTextureAuditRows()
    {
        if (textureAuditRows.Count == 0)
        {
            EditorGUILayout.HelpBox("No texture audit data. Select a generated prefab and click Refresh.", MessageType.Info);
            return;
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("Texture", GUILayout.Width(180f));
            GUILayout.Label("Size", GUILayout.Width(80f));
            GUILayout.Label("Path", GUILayout.Width(280f));
            if (auditShowUsage) GUILayout.Label("Usage", GUILayout.Width(170f));
            if (auditShowImportSettings) GUILayout.Label("Import Settings", GUILayout.Width(320f));
            if (auditShowPlatformSettings) GUILayout.Label("Platforms", GUILayout.Width(300f));
            if (auditShowAtlas) GUILayout.Label("Atlas", GUILayout.Width(90f));
            GUILayout.Label("", GUILayout.Width(150f));
        }

        foreach (var row in textureAuditRows)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(row.name, GUILayout.Width(180f));
                GUILayout.Label(row.texture != null ? $"{row.texture.width}x{row.texture.height}" : "-", GUILayout.Width(80f));
                GUILayout.Label(row.path, GUILayout.Width(280f));
                if (auditShowUsage)
                    GUILayout.Label(row.usedByPrefab ? row.usage : "Folder only", GUILayout.Width(170f));
                if (auditShowImportSettings)
                    GUILayout.Label(GetImportSettingsSummary(row.importer), GUILayout.Width(320f));
                if (auditShowPlatformSettings)
                    GUILayout.Label(GetPlatformSettingsSummary(row.importer), GUILayout.Width(300f));
                if (auditShowAtlas)
                    GUILayout.Label(row.inConfiguredAtlas ? "Configured" : "-", GUILayout.Width(90f));

                if (GUILayout.Button("Select", GUILayout.Width(60f)))
                    SelectAuditAsset(row);
                if (GUILayout.Button("Folder", GUILayout.Width(60f)))
                    SelectAuditFolder(row);
            }
        }
    }

    private static void SelectAuditAsset(TextureAuditRow row)
    {
        UnityEngine.Object target = row.sprite != null ? row.sprite : row.texture;
        if (target == null)
            target = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(row.path);

        if (target == null)
            return;

        Selection.activeObject = target;
        EditorGUIUtility.PingObject(target);
    }

    private static void SelectAuditFolder(TextureAuditRow row)
    {
        string folderPath = Path.GetDirectoryName(row.path)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(folderPath))
            return;

        var folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
        if (folder == null)
            return;

        Selection.activeObject = folder;
        EditorGUIUtility.PingObject(folder);
    }

    private void RefreshTextureAudit()
    {
        textureAuditRows.Clear();
        if (auditPrefab == null)
        {
            statusMessage = "Select a prefab to audit.";
            return;
        }

        string prefabPath = AssetDatabase.GetAssetPath(auditPrefab);
        if (string.IsNullOrEmpty(prefabPath))
        {
            statusMessage = "Texture Audit requires a prefab asset.";
            return;
        }

        var byPath = new Dictionary<string, TextureAuditRow>(StringComparer.OrdinalIgnoreCase);
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabPath);
            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null)
                    continue;

                string path = AssetDatabase.GetAssetPath(image.sprite);
                if (string.IsNullOrEmpty(path))
                    continue;

                AddTextureAuditRow(byPath, path, image.sprite, true, GetHierarchyPath(image.transform, root.transform));
            }
        }
        finally
        {
            if (root != null)
                PrefabUtility.UnloadPrefabContents(root);
        }

        if (auditIncludeFolderTextures)
            AddMatchingFolderTextures(byPath, Path.GetFileNameWithoutExtension(prefabPath));

        textureAuditRows.AddRange(byPath.Values.OrderBy(row => row.path));
        statusMessage = $"Texture Audit: {textureAuditRows.Count} texture(s).";
    }

    private void AddMatchingFolderTextures(Dictionary<string, TextureAuditRow> byPath, string prefabName)
    {
        string folderPath = $"{spriteRootDir.Replace('\\', '/').TrimEnd('/')}/{prefabName}";
        if (!AssetDatabase.IsValidFolder(folderPath))
            return;

        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (byPath.ContainsKey(path))
                continue;

            AddTextureAuditRow(byPath, path, null, false, "");
        }
    }

    private void AddTextureAuditRow(Dictionary<string, TextureAuditRow> byPath, string path, Sprite sprite, bool usedByPrefab, string usage)
    {
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (!byPath.TryGetValue(path, out var row))
        {
            row = new TextureAuditRow
            {
                path = path,
                name = Path.GetFileName(path),
                importer = importer,
                texture = texture,
                sprite = sprite,
                inConfiguredAtlas = IsInConfiguredAtlas(path),
            };
            byPath.Add(path, row);
        }

        row.usedByPrefab |= usedByPrefab;
        if (sprite != null)
            row.sprite = sprite;
        if (!string.IsNullOrEmpty(usage))
            row.usage = string.IsNullOrEmpty(row.usage) ? usage : $"{row.usage}, {usage}";
    }

    private bool IsInConfiguredAtlas(string texturePath)
    {
        if (!useSpriteAtlas || auditPrefab == null)
            return false;

        string prefabName = auditPrefab.name;
        string atlasPath = $"{spriteAtlasOutputDir.Replace('\\', '/').TrimEnd('/')}/atlas_{SanitizeAssetName(prefabName)}.spriteatlas";
        var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
        if (atlas == null)
            return false;

        string expectedFolder = $"{spriteRootDir.Replace('\\', '/').TrimEnd('/')}/{prefabName}";
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        foreach (var packable in SpriteAtlasExtensions.GetPackables(atlas))
        {
            string packablePath = AssetDatabase.GetAssetPath(packable);
            if (string.Equals(packablePath, expectedFolder, StringComparison.OrdinalIgnoreCase))
                return texturePath.StartsWith($"{expectedFolder}/", StringComparison.OrdinalIgnoreCase);
            if (texture != null && packable == texture)
                return true;
        }

        return false;
    }

    private static string GetHierarchyPath(Transform target, Transform root)
    {
        var names = new Stack<string>();
        Transform current = target;
        while (current != null && current != root)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    private static string GetImportSettingsSummary(TextureImporter importer)
    {
        if (importer == null)
            return "-";

        return $"max {importer.maxTextureSize}, {importer.textureType}, {importer.spriteImportMode}, alpha {importer.alphaIsTransparency}, mip {importer.mipmapEnabled}, wrap {importer.wrapMode}, filter {importer.filterMode}, {importer.textureCompression}";
    }

    private static string GetPlatformSettingsSummary(TextureImporter importer)
    {
        if (importer == null)
            return "-";

        return $"{GetPlatformSummary(importer, "Standalone")} | {GetPlatformSummary(importer, "Android")} | {GetPlatformSummary(importer, "iPhone")}";
    }

    private static string GetPlatformSummary(TextureImporter importer, string platform)
    {
        var settings = importer.GetPlatformTextureSettings(platform);
        if (settings == null || !settings.overridden)
            return $"{platform}: default";

        return $"{platform}: {settings.maxTextureSize}/{settings.format}";
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
        var updatedAtlases = new List<string>();
        if (useSpriteAtlas)
        {
            foreach (var entry in jsonEntries.Where(e => e.selected))
            {
                string updated = UpdateSpriteAtlasForJson(entry.path);
                if (!string.IsNullOrEmpty(updated))
                    updatedAtlases.Add(updated);
            }
        }

        statusMessage = $"Built {built} UI prefab(s). Sprites {spriteCount}, missing {missingSpriteCount}. Output: {outputDir}";
        if (updatedAtlases.Count > 0)
            statusMessage += $"\nUpdated atlas: {string.Join(", ", updatedAtlases)}";
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
        textEntries.Clear();
        itemPrefabEntries.Clear();
        BuildLayerTree(rootRect, layers, canvasW, canvasH);
        SaveItemPrefabs(prefabName);
        ConfigureLanguageSwitcher(root);
        ConfigureTextLocalizer(root);

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
            bool isItem = HasToken(layerName, "!item");

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
                    var tmp = AddTmpText(go);
                    textEntries.Add(new PsdUiTextLocalizerBase.Entry
                    {
                        target = tmp,
                        key = CreateTextKey(layerName),
                        layerName = layerName,
                        language = TryGetLanguage(layerName, out var textLanguage) ? textLanguage : previewLanguage,
                    });
                }
                else
                {
                    AddImage(go, layer);
                }
            }

            ApplyLayerTagComponents(go, layerName);
            if (isItem)
            {
                itemPrefabEntries.Add(new ItemPrefabEntry
                {
                    root = go,
                    layerName = layerName,
                });
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

    private void ApplyLayerTagComponents(GameObject go, string layerName)
    {
        if (HasToken(layerName, "!btn"))
        {
            var button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            if (button.targetGraphic == null)
                button.targetGraphic = go.GetComponent<Graphic>();
            EditorUtility.SetDirty(button);
        }

        if (HasToken(layerName, "!mask"))
        {
            var mask = go.GetComponent<Mask>() ?? go.AddComponent<Mask>();
            mask.showMaskGraphic = true;
            EditorUtility.SetDirty(mask);
        }

        if (HasToken(layerName, "!cg"))
        {
            var canvasGroup = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
            EditorUtility.SetDirty(canvasGroup);
        }
    }

    private void SaveItemPrefabs(string sourcePrefabName)
    {
        if (itemPrefabEntries.Count == 0)
            return;

        string itemOutput = string.IsNullOrWhiteSpace(itemPrefabOutputDir) ? outputDir : itemPrefabOutputDir;
        Directory.CreateDirectory(ToAbsolutePath(itemOutput));

        foreach (var entry in itemPrefabEntries)
        {
            if (entry.root == null)
                continue;

            string itemName = CreateItemPrefabName(sourcePrefabName, entry.layerName);
            string itemPath = $"{itemOutput.TrimEnd('/', '\\')}/{itemName}.prefab".Replace('\\', '/');
            var itemClone = Instantiate(entry.root);
            itemClone.name = itemName;
            try
            {
                ConfigureItemPrefabComponents(itemClone);
                PrefabUtility.SaveAsPrefabAsset(itemClone, itemPath);
            }
            finally
            {
                DestroyImmediate(itemClone);
            }
        }
    }

    private void ConfigureItemPrefabComponents(GameObject itemRoot)
    {
        if (addLanguageSwitcher)
        {
            var switcher = itemRoot.GetComponent<PsdUiLanguageLayerSwitcher>() ?? itemRoot.AddComponent<PsdUiLanguageLayerSwitcher>();
            switcher.RebuildEntriesFromChildren(previewLanguage);
            if (switcher.Entries.Count == 0)
            {
                DestroyImmediate(switcher);
            }
            else
            {
                EditorUtility.SetDirty(switcher);
            }
        }

        if (!addTextLocalizer)
            return;

        Type localizerType = GetTextLocalizerType();
        if (localizerType == null)
            return;

        var entries = new List<PsdUiTextLocalizerBase.Entry>();
        foreach (var tmp in itemRoot.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (!HasToken(tmp.name, "!tmp"))
                continue;

            entries.Add(new PsdUiTextLocalizerBase.Entry
            {
                target = tmp,
                key = CreateTextKey(tmp.name),
                layerName = tmp.name,
                language = TryGetLanguage(tmp.name, out var language) ? language : previewLanguage,
            });
        }

        if (entries.Count == 0)
            return;

        var localizer = (PsdUiTextLocalizerBase)(itemRoot.GetComponent(localizerType) ?? itemRoot.AddComponent(localizerType));
        localizer.SetEntries(entries.ToArray(), previewLanguage);
        EditorUtility.SetDirty(localizer);
    }

    private static string CreateItemPrefabName(string sourcePrefabName, string layerName)
    {
        string cleanLayerName = StripKnownTags(layerName);
        cleanLayerName = SanitizeAssetName(cleanLayerName);
        if (string.IsNullOrWhiteSpace(cleanLayerName))
            cleanLayerName = "Item";

        return $"{SanitizeAssetName(sourcePrefabName)}_{cleanLayerName}_Item";
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

    private void ConfigureTextLocalizer(GameObject root)
    {
        var existing = root.GetComponents<PsdUiTextLocalizerBase>();
        if (!addTextLocalizer || textEntries.Count == 0)
        {
            foreach (var component in existing)
                DestroyImmediate(component);
            return;
        }

        Type localizerType = GetTextLocalizerType();
        if (localizerType == null)
        {
            statusMessage = "Text localizer script must inherit PsdUiTextLocalizerBase.";
            Debug.LogError($"[GenericPsdUiImporter] {statusMessage}");
            foreach (var component in existing)
                DestroyImmediate(component);
            return;
        }

        PsdUiTextLocalizerBase localizer = existing.FirstOrDefault(component => component.GetType() == localizerType);
        foreach (var component in existing)
        {
            if (component != localizer)
                DestroyImmediate(component);
        }

        localizer ??= (PsdUiTextLocalizerBase)root.AddComponent(localizerType);
        localizer.SetEntries(textEntries.ToArray(), previewLanguage);
        EditorUtility.SetDirty(localizer);
    }

    private Type GetTextLocalizerType()
    {
        if (textLocalizerScript == null)
            return null;

        Type type = textLocalizerScript.GetClass();
        if (type == null || type.IsAbstract || !typeof(PsdUiTextLocalizerBase).IsAssignableFrom(type))
            return null;

        return type;
    }

    private TextMeshProUGUI AddTmpText(GameObject go)
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
        return tmp;
    }

    private static string CreateTextKey(string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName))
            return "";

        string key = layerName;
        key = StripKnownTags(key);

        return key.Trim();
    }

    private static string StripKnownTags(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        foreach (string token in new[] { "!tmp", "!kr", "!en", "!jp", "!btn", "!item", "!mask", "!cg" })
            value = RemoveToken(value, token);

        return value.Trim();
    }

    private static string RemoveToken(string value, string token)
    {
        while (true)
        {
            int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return value;

            value = value.Remove(index, token.Length);
        }
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

        var updatedAtlases = new List<string>();
        foreach (var entry in jsonEntries.Where(e => e.selected))
        {
            string updated = UpdateSpriteAtlasForJson(entry.path);
            if (!string.IsNullOrEmpty(updated))
                updatedAtlases.Add(updated);
        }

        statusMessage = updatedAtlases.Count > 0
            ? $"Updated atlas: {string.Join(", ", updatedAtlases)}"
            : "No selected JSON files to update atlas.";
    }

    private string UpdateSpriteAtlasForJson(string jsonAssetPath)
    {
        SavePrefs();

        if (!useSpriteAtlas)
        {
            statusMessage = "Sprite Atlas is disabled.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(spriteAtlasOutputDir))
        {
            statusMessage = "Sprite Atlas output folder is empty.";
            return null;
        }

        JObject meta = JObject.Parse(File.ReadAllText(ToAbsolutePath(jsonAssetPath)));
        string atlasName = GetAtlasName(meta, jsonAssetPath);
        string normalizedAtlasDir = spriteAtlasOutputDir.Replace('\\', '/').TrimEnd('/');
        string atlasPath = $"{normalizedAtlasDir}/{atlasName}.spriteatlas";

        var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
        if (atlas == null)
        {
            if (!createSpriteAtlasIfMissing)
            {
                statusMessage = $"Sprite Atlas not found: {atlasPath}";
                return null;
            }

            Directory.CreateDirectory(ToAbsolutePath(normalizedAtlasDir));

            atlas = new SpriteAtlas();
            AssetDatabase.CreateAsset(atlas, atlasPath);
        }

        var packables = SpriteAtlasExtensions.GetPackables(atlas).ToList();
        var newPackables = CollectAtlasPackables(meta, jsonAssetPath, packables, out bool foundPackables);
        if (!foundPackables)
        {
            statusMessage = $"No sprite packables found for atlas: {atlasName}";
            return null;
        }

        if (newPackables.Count > 0)
            SpriteAtlasExtensions.Add(atlas, newPackables.ToArray());

        EditorUtility.SetDirty(atlas);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (packSpriteAtlasAfterBuild)
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget, false);

        statusMessage = $"Updated Sprite Atlas: {atlasPath}";
        return atlasPath;
    }

    private List<UnityEngine.Object> CollectAtlasPackables(JObject meta, string jsonAssetPath, List<UnityEngine.Object> existingPackables, out bool foundPackables)
    {
        foundPackables = false;
        var result = new List<UnityEngine.Object>();
        string psdStem = GetPsdStem(meta, jsonAssetPath);
        string psdSpriteFolder = $"{spriteRootDir.Replace('\\', '/').TrimEnd('/')}/{psdStem}";
        var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(psdSpriteFolder);
        if (folderAsset != null && AssetDatabase.IsValidFolder(psdSpriteFolder))
        {
            foundPackables = true;
            AddPackableIfMissing(folderAsset, existingPackables, result);
            return result;
        }

        foreach (string pngPath in EnumeratePngAssetPaths(meta["layers"] as JArray))
        {
            string spritePath = $"{spriteRootDir.TrimEnd('/', '\\')}/{pngPath}".Replace('\\', '/');
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
            if (texture != null)
            {
                foundPackables = true;
                AddPackableIfMissing(texture, existingPackables, result);
            }
        }

        return result;
    }

    private static void AddPackableIfMissing(UnityEngine.Object packable, List<UnityEngine.Object> existingPackables, List<UnityEngine.Object> newPackables)
    {
        if (existingPackables.Contains(packable) || newPackables.Contains(packable))
            return;

        newPackables.Add(packable);
    }

    private static IEnumerable<string> EnumeratePngAssetPaths(JArray layers)
    {
        if (layers == null)
            yield break;

        foreach (JObject layer in layers.OfType<JObject>())
        {
            string png = layer["png"]?.ToString();
            if (!string.IsNullOrEmpty(png))
                yield return png.Replace('\\', '/');

            foreach (string childPng in EnumeratePngAssetPaths(layer["children"] as JArray))
                yield return childPng;
        }
    }

    private static string GetAtlasName(JObject meta, string jsonAssetPath)
    {
        return $"atlas_{SanitizeAssetName(GetPsdStem(meta, jsonAssetPath))}";
    }

    private static string GetPsdStem(JObject meta, string jsonAssetPath)
    {
        string source = meta["source"]?.ToString();
        return !string.IsNullOrEmpty(source)
            ? Path.GetFileNameWithoutExtension(source)
            : Path.GetFileNameWithoutExtension(jsonAssetPath);
    }

    private static string SanitizeAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
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
        CreateFolderIfPathIsRelative(itemPrefabOutputDir);
        CreateFolderIfPathIsRelative(spriteAtlasOutputDir);

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
        itemPrefabOutputDir = string.IsNullOrWhiteSpace(settingsAsset.itemPrefabOutputDir)
            ? "Assets/Resources/UI/Prefabs/Generated/Items"
            : settingsAsset.itemPrefabOutputDir;
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
        addTextLocalizer = settingsAsset.addTextLocalizer;
        textLocalizerScript = settingsAsset.textLocalizerScript;
        previewLanguage = settingsAsset.previewLanguage;
        defaultFont = settingsAsset.defaultFont;
        useSpriteAtlas = settingsAsset.useSpriteAtlas;
        spriteAtlasOutputDir = string.IsNullOrWhiteSpace(settingsAsset.spriteAtlasOutputDir)
            ? "Assets/Art/Extracted/Atlases"
            : settingsAsset.spriteAtlasOutputDir;
        createSpriteAtlasIfMissing = settingsAsset.createSpriteAtlasIfMissing;
        packSpriteAtlasAfterBuild = settingsAsset.packSpriteAtlasAfterBuild;
        showTagReference = settingsAsset.showTagReference;
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
        settingsAsset.itemPrefabOutputDir = itemPrefabOutputDir;
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
        settingsAsset.addTextLocalizer = addTextLocalizer;
        settingsAsset.textLocalizerScript = textLocalizerScript;
        settingsAsset.previewLanguage = previewLanguage;
        settingsAsset.defaultFont = defaultFont;
        settingsAsset.useSpriteAtlas = useSpriteAtlas;
        settingsAsset.spriteAtlasOutputDir = spriteAtlasOutputDir;
        settingsAsset.createSpriteAtlasIfMissing = createSpriteAtlasIfMissing;
        settingsAsset.packSpriteAtlasAfterBuild = packSpriteAtlasAfterBuild;
        settingsAsset.showTagReference = showTagReference;

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
    public string itemPrefabOutputDir = "Assets/Resources/UI/Prefabs/Generated/Items";
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
    public bool addTextLocalizer;
    public MonoScript textLocalizerScript;
    public SystemLanguage previewLanguage = SystemLanguage.Korean;
    public TMP_FontAsset defaultFont;
    public bool useSpriteAtlas;
    public string spriteAtlasOutputDir = "Assets/Art/Extracted/Atlases";
    public bool createSpriteAtlasIfMissing = true;
    public bool packSpriteAtlasAfterBuild;
    public bool showTagReference;
}
#endif
