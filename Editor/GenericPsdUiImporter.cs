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
    [SerializeField] private bool auditShowWarnings = true;
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
    private int itemPrefabCount;
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
        public List<string> warnings = new();
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
        activeTab = GUILayout.Toolbar(activeTab, new[] { "UI 생성", "설정", "텍스처 검사", "이미지 정리" });
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
        EditorGUILayout.LabelField("PSD UI Importer", EditorStyles.boldLabel);
        DrawSettingsAssetField();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("선택 UI 프리팹 생성", GUILayout.Height(32f)))
                BuildSelected();

            if (GUILayout.Button("PSD 추출 + 생성", GUILayout.Height(32f)))
            {
                if (ExtractPsdFiles())
                    BuildSelected();
            }

            if (GUILayout.Button("변경 PSD 추출 + 생성", GUILayout.Height(32f)))
                ExtractChangedPsdFilesAndBuild();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("JSON 목록 갱신", GUILayout.Height(26f)))
                RefreshJsonList();

            if (GUILayout.Button("PSD 추출", GUILayout.Height(26f)))
                ExtractPsdFiles();

            using (new EditorGUI.DisabledScope(!useSpriteAtlas))
            {
                if (GUILayout.Button("아틀라스 갱신", GUILayout.Height(26f)))
                    UpdateSpriteAtlas();
            }
        }

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("추출된 JSON에서 생성", EditorStyles.boldLabel);
        DrawPathField("메타데이터 폴더", ref metadataDir, true);
        DrawPathField("스프라이트 루트 폴더", ref spriteRootDir, true);
        DrawPathField("프리팹 출력 폴더", ref outputDir, true);

        EditorGUILayout.Space(6f);
        DrawJsonList();

        DrawStatusMessage();
    }

    private void DrawSettingsTab()
    {
        EditorGUILayout.LabelField("프로젝트 설정", EditorStyles.boldLabel);
        DrawSettingsAssetField();

        EditorGUILayout.LabelField("PSD 추출", EditorStyles.boldLabel);
        DrawPathField("PSD 원본 폴더", ref psdSourceDir, true);
        DrawPathField("추출 출력 폴더", ref metadataDir, true);
        exportScale = Mathf.Max(0.01f, EditorGUILayout.FloatField("추출 스케일", exportScale));
        exportMaxDim = Mathf.Max(0, EditorGUILayout.IntField("최대 이미지 크기", exportMaxDim));
        exportPotSnap = EditorGUILayout.ToggleLeft("2의 거듭제곱에 가까운 텍스처 보정", exportPotSnap);
        using (new EditorGUI.DisabledScope(!exportPotSnap))
            exportPotThreshold = Mathf.Max(1.001f, EditorGUILayout.FloatField("POT 보정 임계값", exportPotThreshold));
        exportForceTopil = EditorGUILayout.ToggleLeft("topil 방식만 사용", exportForceTopil);
        exportSnapAlpha = EditorGUILayout.IntField("알파 스냅 임계값 (-1 꺼짐)", exportSnapAlpha);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("UI 생성", EditorStyles.boldLabel);
        DrawPathField("스프라이트 루트 폴더", ref spriteRootDir, true);
        DrawPathField("프리팹 출력 폴더", ref outputDir, true);
        DrawPathField("아이템 프리팹 폴더", ref itemPrefabOutputDir, true);

        EditorGUILayout.Space(6f);
        replaceExistingContent = EditorGUILayout.ToggleLeft("기존 프리팹의 생성 자식 교체", replaceExistingContent);
        prepareSprites = EditorGUILayout.ToggleLeft("PNG를 UI Sprite 설정으로 준비", prepareSprites);
        convertTmpLayers = EditorGUILayout.ToggleLeft("!tmp 레이어를 TextMeshProUGUI로 변환", convertTmpLayers);
        addLanguageSwitcher = EditorGUILayout.ToggleLeft("언어 태그 전환 컴포넌트 추가", addLanguageSwitcher);
        addTextLocalizer = EditorGUILayout.ToggleLeft("!tmp 레이어용 프로젝트 텍스트 로컬라이저 추가", addTextLocalizer);
        using (new EditorGUI.DisabledScope(!addTextLocalizer))
            textLocalizerScript = (MonoScript)EditorGUILayout.ObjectField("텍스트 로컬라이저 스크립트", textLocalizerScript, typeof(MonoScript), false);
        previewLanguage = (SystemLanguage)EditorGUILayout.EnumPopup("미리보기 언어", previewLanguage);
        defaultFont = (TMP_FontAsset)EditorGUILayout.ObjectField("기본 TMP 폰트", defaultFont, typeof(TMP_FontAsset), false);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("스프라이트 아틀라스", EditorStyles.boldLabel);
        useSpriteAtlas = EditorGUILayout.ToggleLeft("스프라이트 아틀라스 사용", useSpriteAtlas);
        using (new EditorGUI.DisabledScope(!useSpriteAtlas))
        {
            DrawPathField("아틀라스 출력 폴더", ref spriteAtlasOutputDir, true);
            createSpriteAtlasIfMissing = EditorGUILayout.ToggleLeft("아틀라스가 없으면 생성", createSpriteAtlasIfMissing);
            packSpriteAtlasAfterBuild = EditorGUILayout.ToggleLeft("생성 후 아틀라스 Pack", packSpriteAtlasAfterBuild);
        }

        EditorGUILayout.Space(8f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("설정 저장", GUILayout.Height(28f)))
                SaveSettingsAndReport();

            if (GUILayout.Button("폴더 생성", GUILayout.Height(28f)))
                CreateConfiguredFolders();

            if (GUILayout.Button("PSD 변경 기록 초기화", GUILayout.Height(28f)))
                ResetPsdFileStates();

            using (new EditorGUI.DisabledScope(!useSpriteAtlas))
            {
                if (GUILayout.Button("아틀라스 갱신", GUILayout.Height(28f)))
                    UpdateSpriteAtlas();
            }
        }

        DrawTagReference();
        DrawStatusMessage();
    }

    private void DrawTagReference()
    {
        EditorGUILayout.Space(10f);
        showTagReference = EditorGUILayout.Foldout(showTagReference, "태그 참고", true);
        if (!showTagReference)
            return;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            DrawTagHelp("!tmp", "Image 대신 TextMeshProUGUI 생성, PSD 텍스트가 있으면 초기값 적용");
            DrawTagHelp("!btn", "Button 추가, Transition None 설정");
            DrawTagHelp("!item", "하위 루트를 별도 아이템 프리팹으로 저장");
            DrawTagHelp("!mask", "Mask 추가");
            DrawTagHelp("!cg", "CanvasGroup 추가");
            DrawTagHelp("!kr/!ko !en !jp/!ja", "한국어/영어/일본어 레이어 엔트리 등록");
            DrawTagHelp("!zh !zhs !zht", "중국어/간체/번체 레이어 엔트리 등록");
            DrawTagHelp("!fr !de !es !it !pt !ru", "추가 언어 레이어 엔트리 등록");
            DrawTagHelp("!ref", "참고용 레이어/트리는 추출 및 생성에서 제외");
            DrawTagHelp("!x1.5", "해당 레이어 이미지 추출 스케일 override");
            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField("예시", EditorStyles.boldLabel);
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
            var pickedSettings = (GenericPsdUiImporterSettings)EditorGUILayout.ObjectField("설정 에셋", settingsAsset, typeof(GenericPsdUiImporterSettings), false);
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
        EditorGUILayout.LabelField("텍스처 검사", EditorStyles.boldLabel);
        auditPrefab = (GameObject)EditorGUILayout.ObjectField("프리팹", auditPrefab, typeof(GameObject), false);
        auditIncludeFolderTextures = EditorGUILayout.ToggleLeft("같은 PSD 폴더의 텍스처도 포함", auditIncludeFolderTextures);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("선택 프리팹 검사", GUILayout.Height(26f)))
                UseSelectedPrefabAndRefreshAudit();

            if (GUILayout.Button("새로고침", GUILayout.Height(26f)))
                RefreshTextureAudit();

            using (new EditorGUI.DisabledScope(textureAuditRows.Count == 0))
            {
                if (GUILayout.Button("권장 설정 적용", GUILayout.Height(26f)))
                    ApplyRecommendedTextureSettings();
            }

            using (new EditorGUI.DisabledScope(!useSpriteAtlas || auditPrefab == null))
            {
                if (GUILayout.Button("Atlas 누락 추가", GUILayout.Height(26f)))
                    AddAuditPrefabToAtlas();
            }
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("컬럼", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            auditShowUsage = EditorGUILayout.ToggleLeft("사용처", auditShowUsage, GUILayout.Width(80f));
            auditShowWarnings = EditorGUILayout.ToggleLeft("경고", auditShowWarnings, GUILayout.Width(80f));
            auditShowImportSettings = EditorGUILayout.ToggleLeft("Import", auditShowImportSettings, GUILayout.Width(80f));
            auditShowPlatformSettings = EditorGUILayout.ToggleLeft("플랫폼", auditShowPlatformSettings, GUILayout.Width(90f));
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
            EditorGUILayout.HelpBox("검사 데이터가 없습니다. 생성된 프리팹을 선택하고 새로고침하세요.", MessageType.Info);
            return;
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("텍스처", GUILayout.Width(180f));
            GUILayout.Label("크기", GUILayout.Width(80f));
            GUILayout.Label("경로", GUILayout.Width(280f));
            if (auditShowUsage) GUILayout.Label("사용처", GUILayout.Width(170f));
            if (auditShowWarnings) GUILayout.Label("경고", GUILayout.Width(250f));
            if (auditShowImportSettings) GUILayout.Label("Import 설정", GUILayout.Width(320f));
            if (auditShowPlatformSettings) GUILayout.Label("플랫폼", GUILayout.Width(300f));
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
                    GUILayout.Label(row.usedByPrefab ? row.usage : "폴더만", GUILayout.Width(170f));
                if (auditShowWarnings)
                    GUILayout.Label(row.warnings.Count > 0 ? string.Join(", ", row.warnings) : "-", GUILayout.Width(250f));
                if (auditShowImportSettings)
                    GUILayout.Label(GetImportSettingsSummary(row.importer), GUILayout.Width(320f));
                if (auditShowPlatformSettings)
                    GUILayout.Label(GetPlatformSettingsSummary(row.importer), GUILayout.Width(300f));
                if (auditShowAtlas)
                    GUILayout.Label(row.inConfiguredAtlas ? "등록됨" : "-", GUILayout.Width(90f));

                if (GUILayout.Button("선택", GUILayout.Width(60f)))
                    SelectAuditAsset(row);
                if (GUILayout.Button("폴더", GUILayout.Width(60f)))
                    SelectAuditFolder(row);
            }
        }
    }

    private void UseSelectedPrefabAndRefreshAudit()
    {
        var selected = Selection.activeObject as GameObject;
        if (selected == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(selected)) || PrefabUtility.GetPrefabAssetType(selected) == PrefabAssetType.NotAPrefab)
        {
            statusMessage = "Project 창에서 프리팹 에셋을 선택하세요.";
            return;
        }

        auditPrefab = selected;
        RefreshTextureAudit();
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
            statusMessage = "검사할 프리팹을 선택하세요.";
            return;
        }

        string prefabPath = AssetDatabase.GetAssetPath(auditPrefab);
        if (string.IsNullOrEmpty(prefabPath))
        {
            statusMessage = "텍스처 검사는 프리팹 에셋이 필요합니다.";
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
        foreach (var row in textureAuditRows)
            row.warnings = GetTextureWarnings(row);
        statusMessage = $"텍스처 검사: {textureAuditRows.Count}개";
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

    private List<string> GetTextureWarnings(TextureAuditRow row)
    {
        var warnings = new List<string>();
        if (row.importer == null)
        {
            warnings.Add("Importer 없음");
            return warnings;
        }

        if (row.importer.textureType != TextureImporterType.Sprite)
            warnings.Add("Sprite 아님");
        if (row.importer.spriteImportMode != SpriteImportMode.Single)
            warnings.Add("Single 아님");
        if (row.importer.mipmapEnabled)
            warnings.Add("Mipmap 켜짐");
        if (!row.importer.alphaIsTransparency)
            warnings.Add("Alpha 투명 꺼짐");
        if (useSpriteAtlas && row.usedByPrefab && !row.inConfiguredAtlas)
            warnings.Add("Atlas 미등록");

        return warnings;
    }

    private void ApplyRecommendedTextureSettings()
    {
        int changed = 0;
        foreach (var row in textureAuditRows)
        {
            if (row.importer == null)
                continue;

            bool dirty = false;
            if (row.importer.textureType != TextureImporterType.Sprite)
            {
                row.importer.textureType = TextureImporterType.Sprite;
                dirty = true;
            }

            if (row.importer.spriteImportMode != SpriteImportMode.Single)
            {
                row.importer.spriteImportMode = SpriteImportMode.Single;
                dirty = true;
            }

            if (row.importer.mipmapEnabled)
            {
                row.importer.mipmapEnabled = false;
                dirty = true;
            }

            if (!row.importer.alphaIsTransparency)
            {
                row.importer.alphaIsTransparency = true;
                dirty = true;
            }

            if (!dirty)
                continue;

            row.importer.SaveAndReimport();
            changed++;
        }

        AssetDatabase.Refresh();
        RefreshTextureAudit();
        statusMessage = $"권장 텍스처 설정 적용: {changed}개";
    }

    private void AddAuditPrefabToAtlas()
    {
        if (auditPrefab == null)
        {
            statusMessage = "검사할 프리팹을 선택하세요.";
            return;
        }

        string jsonPath = $"{metadataDir.TrimEnd('/', '\\')}/{auditPrefab.name}.json".Replace('\\', '/');
        if (!File.Exists(ToAbsolutePath(jsonPath)))
        {
            statusMessage = $"대응 JSON을 찾을 수 없습니다: {jsonPath}";
            return;
        }

        string updated = UpdateSpriteAtlasForJson(jsonPath);
        RefreshTextureAudit();
        statusMessage = string.IsNullOrEmpty(updated)
            ? "아틀라스에 추가할 항목이 없습니다."
            : $"아틀라스 갱신: {updated}";
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
        if (!settings.overridden)
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
        BuildJsonEntries(jsonEntries.Where(e => e.selected));
    }

    private void BuildJsonPaths(IEnumerable<string> jsonAssetPaths)
    {
        var pathSet = new HashSet<string>(jsonAssetPaths.Select(path => path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        BuildJsonEntries(jsonEntries.Where(entry => pathSet.Contains(entry.path.Replace('\\', '/'))));
    }

    private void BuildJsonEntries(IEnumerable<JsonEntry> entries)
    {
        SavePrefs();
        Directory.CreateDirectory(ToAbsolutePath(outputDir));

        int built = 0;
        spriteCount = 0;
        missingSpriteCount = 0;
        itemPrefabCount = 0;
        var buildEntries = entries.ToList();

        foreach (var entry in buildEntries)
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
            foreach (var entry in buildEntries)
            {
                string updated = UpdateSpriteAtlasForJson(entry.path);
                if (!string.IsNullOrEmpty(updated))
                    updatedAtlases.Add(updated);
            }
        }

        statusMessage = $"UI 프리팹 {built}개 생성. Sprite {spriteCount}개, 누락 {missingSpriteCount}개, Item {itemPrefabCount}개. 출력: {outputDir}";
        if (updatedAtlases.Count > 0)
            statusMessage += $"\n아틀라스 갱신: {string.Join(", ", updatedAtlases)}";
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
                    var tmp = AddTmpText(go, layer["text"]?.ToString());
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
                itemPrefabCount++;
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

    private TextMeshProUGUI AddTmpText(GameObject go, string initialText)
    {
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = initialText ?? "";
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

        foreach (string token in KnownTags)
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
        foreach (var pair in LanguageTags)
        {
            if (!HasToken(name, pair.token))
                continue;

            language = pair.language;
            return true;
        }

        language = SystemLanguage.Unknown;
        return false;
    }

    private static readonly (string token, SystemLanguage language)[] LanguageTags =
    {
        ("!kr", SystemLanguage.Korean),
        ("!ko", SystemLanguage.Korean),
        ("!jp", SystemLanguage.Japanese),
        ("!ja", SystemLanguage.Japanese),
        ("!en", SystemLanguage.English),
        ("!zh-cn", SystemLanguage.ChineseSimplified),
        ("!zh-tw", SystemLanguage.ChineseTraditional),
        ("!zhs", SystemLanguage.ChineseSimplified),
        ("!zht", SystemLanguage.ChineseTraditional),
        ("!zh", SystemLanguage.Chinese),
        ("!fr", SystemLanguage.French),
        ("!de", SystemLanguage.German),
        ("!es", SystemLanguage.Spanish),
        ("!it", SystemLanguage.Italian),
        ("!pt", SystemLanguage.Portuguese),
        ("!ru", SystemLanguage.Russian),
    };

    private static readonly string[] KnownTags =
    {
        "!tmp", "!btn", "!item", "!mask", "!cg",
        "!kr", "!ko", "!en", "!jp", "!ja",
        "!zh-cn", "!zh-tw", "!zhs", "!zht", "!zh",
        "!fr", "!de", "!es", "!it", "!pt", "!ru",
    };

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
            ? $"아틀라스 갱신: {string.Join(", ", updatedAtlases)}"
            : "아틀라스를 갱신할 선택 JSON이 없습니다.";
    }

    private string UpdateSpriteAtlasForJson(string jsonAssetPath)
    {
        SavePrefs();

        if (!useSpriteAtlas)
        {
            statusMessage = "스프라이트 아틀라스가 꺼져 있습니다.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(spriteAtlasOutputDir))
        {
            statusMessage = "스프라이트 아틀라스 출력 폴더가 비어 있습니다.";
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
                statusMessage = $"스프라이트 아틀라스를 찾을 수 없습니다: {atlasPath}";
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
            statusMessage = $"아틀라스에 추가할 스프라이트를 찾을 수 없습니다: {atlasName}";
            return null;
        }

        if (newPackables.Count > 0)
            SpriteAtlasExtensions.Add(atlas, newPackables.ToArray());

        EditorUtility.SetDirty(atlas);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (packSpriteAtlasAfterBuild)
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget, false);

        statusMessage = $"스프라이트 아틀라스 갱신: {atlasPath}";
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
        statusMessage = $"설정 저장: {(string.IsNullOrEmpty(path) ? SettingsAssetPath : path)}";
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
        statusMessage = "설정된 폴더를 생성했습니다.";
    }

    private void ResetPsdFileStates()
    {
        SavePrefs();
        settingsAsset.psdFileStates = new List<PsdUiImporterPsdFileState>();
        EditorUtility.SetDirty(settingsAsset);
        AssetDatabase.SaveAssets();
        statusMessage = "PSD 변경 기록을 초기화했습니다. 다음 변경 PSD 추출 시 모든 PSD가 대상이 됩니다.";
    }

    private static void CreateFolderIfPathIsRelative(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || Path.IsPathRooted(assetPath))
            return;

        Directory.CreateDirectory(ToAbsolutePath(assetPath));
    }

    private bool ExtractPsdFiles()
    {
        return ExtractPsdFiles(null, true);
    }

    private void ExtractChangedPsdFilesAndBuild()
    {
        var changedPsdFiles = GetChangedPsdFiles();
        if (changedPsdFiles.Count == 0)
        {
            statusMessage = "변경된 PSD/PSB가 없습니다.";
            return;
        }

        if (!ExtractPsdFiles(changedPsdFiles, true))
            return;

        RefreshJsonList();
        var changedJsonPaths = changedPsdFiles
            .Select(path => $"{metadataDir.TrimEnd('/', '\\')}/{Path.GetFileNameWithoutExtension(path)}.json".Replace('\\', '/'))
            .ToList();
        BuildJsonPaths(changedJsonPaths);
    }

    private bool ExtractPsdFiles(IReadOnlyList<string> psdFiles, bool updatePsdState)
    {
        SavePrefs();

        string sourceDir = ToAbsolutePath(psdSourceDir);
        if ((psdFiles == null || psdFiles.Count == 0) && !Directory.Exists(sourceDir))
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
            if (psdFiles == null || psdFiles.Count == 0)
            {
                startInfo.ArgumentList.Add("--psd-dir");
                startInfo.ArgumentList.Add(sourceDir);
            }
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
            if (psdFiles != null)
            {
                foreach (string psdFile in psdFiles)
                    startInfo.ArgumentList.Add(psdFile);
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
        if (updatePsdState)
            UpdatePsdFileStates(psdFiles);
        statusMessage = psdFiles != null && psdFiles.Count > 0
            ? $"PSD 추출 완료: {psdFiles.Count}개. 출력: {metadataDir}"
            : $"PSD 추출 완료. 출력: {metadataDir}";
        return true;
    }

    private List<string> GetChangedPsdFiles()
    {
        string sourceDir = ToAbsolutePath(psdSourceDir);
        if (!Directory.Exists(sourceDir))
        {
            statusMessage = $"PSD 원본 폴더를 찾을 수 없습니다: {psdSourceDir}";
            return new List<string>();
        }

        settingsAsset.psdFileStates ??= new List<PsdUiImporterPsdFileState>();
        var previous = settingsAsset.psdFileStates
            .Where(state => !string.IsNullOrEmpty(state.path))
            .ToDictionary(state => state.path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>();
        foreach (string path in Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsPsdFile)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string assetPath = ToAssetPath(path).Replace('\\', '/');
            var info = new FileInfo(path);
            if (!previous.TryGetValue(assetPath, out var oldState) ||
                oldState.lastWriteTicksUtc != info.LastWriteTimeUtc.Ticks ||
                oldState.length != info.Length)
            {
                changed.Add(path);
            }
        }

        return changed;
    }

    private void UpdatePsdFileStates(IReadOnlyList<string> updatedFiles)
    {
        settingsAsset.psdFileStates ??= new List<PsdUiImporterPsdFileState>();
        var states = settingsAsset.psdFileStates
            .Where(state => !string.IsNullOrEmpty(state.path))
            .ToDictionary(state => state.path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files = updatedFiles != null && updatedFiles.Count > 0
            ? updatedFiles
            : Directory.Exists(ToAbsolutePath(psdSourceDir))
                ? Directory.GetFiles(ToAbsolutePath(psdSourceDir), "*.*", SearchOption.TopDirectoryOnly).Where(IsPsdFile)
                : Enumerable.Empty<string>();

        foreach (string path in files)
        {
            var info = new FileInfo(path);
            string assetPath = ToAssetPath(path).Replace('\\', '/');
            states[assetPath] = new PsdUiImporterPsdFileState
            {
                path = assetPath,
                lastWriteTicksUtc = info.LastWriteTimeUtc.Ticks,
                length = info.Length,
            };
        }

        settingsAsset.psdFileStates = states.Values.OrderBy(state => state.path, StringComparer.OrdinalIgnoreCase).ToList();
        EditorUtility.SetDirty(settingsAsset);
        AssetDatabase.SaveAssets();
    }

    private static bool IsPsdFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".psd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".psb", StringComparison.OrdinalIgnoreCase);
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
        auditShowUsage = settingsAsset.auditShowUsage;
        auditShowWarnings = settingsAsset.auditShowWarnings;
        auditShowImportSettings = settingsAsset.auditShowImportSettings;
        auditShowPlatformSettings = settingsAsset.auditShowPlatformSettings;
        auditShowAtlas = settingsAsset.auditShowAtlas;
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
        settingsAsset.auditShowUsage = auditShowUsage;
        settingsAsset.auditShowWarnings = auditShowWarnings;
        settingsAsset.auditShowImportSettings = auditShowImportSettings;
        settingsAsset.auditShowPlatformSettings = auditShowPlatformSettings;
        settingsAsset.auditShowAtlas = auditShowAtlas;

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
    public bool auditShowUsage = true;
    public bool auditShowWarnings = true;
    public bool auditShowImportSettings = true;
    public bool auditShowPlatformSettings;
    public bool auditShowAtlas = true;
    public List<PsdUiImporterPsdFileState> psdFileStates = new();
}

[Serializable]
public sealed class PsdUiImporterPsdFileState
{
    public string path;
    public long lastWriteTicksUtc;
    public long length;
}
#endif
