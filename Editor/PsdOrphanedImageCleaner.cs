#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

internal sealed class PsdOrphanedImageCleanerPanel
{
    private string scanFolder = "Assets";
    private bool wholeProjectReferences = false;
    private bool includeScenes = true;

    private sealed class OrphanEntry
    {
        public string path;
        public bool selected = true;
        public Texture2D preview;
        public long fileSize;
    }

    private readonly List<OrphanEntry> orphans = new();
    private bool scanned;
    private Vector2 scroll;

    public void Draw()
    {
        EditorGUILayout.LabelField("Orphaned PSD Image Cleaner", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Scans Texture2D assets and lists images that are not referenced by prefabs or scenes. " +
            "PSD/PSB source files are ignored because Unity owns their sub-assets.",
            MessageType.Info);

        EditorGUILayout.Space(6f);
        DrawScanFolderField();

        wholeProjectReferences = EditorGUILayout.Toggle(
            new GUIContent("Reference scope: whole project",
                "On: scan every prefab/scene for references. Off: only scan references inside the scan folder."),
            wholeProjectReferences);

        includeScenes = EditorGUILayout.Toggle(
            new GUIContent("Include scene references",
                "Treat textures referenced by scenes as used."),
            includeScenes);

        EditorGUILayout.Space(4f);
        if (GUILayout.Button("Scan", GUILayout.Height(32f)))
            Scan();

        if (!scanned)
            return;

        EditorGUILayout.Space(8f);
        if (orphans.Count == 0)
        {
            EditorGUILayout.HelpBox("No orphaned images found.", MessageType.None);
            return;
        }

        DrawResultsHeader();
        DrawResultsList();
        DrawDeleteButton();
    }

    private void DrawScanFolderField()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            scanFolder = EditorGUILayout.TextField("Image scan folder", scanFolder);
            if (GUILayout.Button("...", GUILayout.Width(28f)))
            {
                string picked = EditorUtility.OpenFolderPanel("Select scan folder", Application.dataPath, "");
                if (!string.IsNullOrEmpty(picked))
                    scanFolder = ToAssetPath(picked);
            }
        }
    }

    private void DrawResultsHeader()
    {
        long totalSize = orphans.Sum(entry => entry.fileSize);
        EditorGUILayout.LabelField($"Orphaned images: {orphans.Count} ({FormatBytes(totalSize)})", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All")) SetAllSelected(true);
            if (GUILayout.Button("Select None")) SetAllSelected(false);
        }
    }

    private void DrawResultsList()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var entry in orphans)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                entry.selected = EditorGUILayout.Toggle(entry.selected, GUILayout.Width(18f));

                if (entry.preview != null)
                    GUILayout.Label(entry.preview, GUILayout.Width(36f), GUILayout.Height(36f));
                else
                    GUILayout.Space(40f);

                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    EditorGUILayout.LabelField(entry.path, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(FormatBytes(entry.fileSize), EditorStyles.centeredGreyMiniLabel);
                }

                if (GUILayout.Button("Ping", GUILayout.Width(46f), GUILayout.Height(36f)))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.path);
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawDeleteButton()
    {
        EditorGUILayout.Space(4f);
        int selectedCount = orphans.Count(entry => entry.selected);
        long selectedSize = orphans.Where(entry => entry.selected).Sum(entry => entry.fileSize);

        using (new EditorGUI.DisabledScope(selectedCount == 0))
        {
            if (GUILayout.Button($"Delete Selected {selectedCount} ({FormatBytes(selectedSize)})", GUILayout.Height(30f)))
                DeleteSelected();
        }
    }

    private void Scan()
    {
        orphans.Clear();
        scanned = true;

        try
        {
            var candidates = CollectCandidateTextures();
            if (candidates.Count == 0)
            {
                GUIUtility.ExitGUI();
                return;
            }

            var referenced = BuildReferencedSet();
            int index = 0;
            foreach (string path in candidates.OrderBy(path => path))
            {
                EditorUtility.DisplayProgressBar("Finding orphaned images", path, (float)index++ / candidates.Count);
                if (referenced.Contains(path))
                    continue;

                orphans.Add(new OrphanEntry
                {
                    path = path,
                    fileSize = GetFileSize(path),
                    preview = AssetPreview.GetAssetPreview(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path)),
                });
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private HashSet<string> CollectCandidateTextures()
    {
        var candidates = new HashSet<string>();
        if (!AssetDatabase.IsValidFolder(scanFolder))
            return candidates;

        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { scanFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!IsPsdFile(path))
                candidates.Add(path);
        }

        return candidates;
    }

    private HashSet<string> BuildReferencedSet()
    {
        var referenced = new HashSet<string>();
        string[] searchScope = wholeProjectReferences ? null : new[] { scanFolder };

        var guids = new List<string>(
            searchScope != null
                ? AssetDatabase.FindAssets("t:Prefab", searchScope)
                : AssetDatabase.FindAssets("t:Prefab"));

        if (includeScenes)
        {
            guids.AddRange(
                searchScope != null
                    ? AssetDatabase.FindAssets("t:Scene", searchScope)
                    : AssetDatabase.FindAssets("t:Scene"));
        }

        for (int i = 0; i < guids.Count; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            EditorUtility.DisplayProgressBar("Collecting references", path, (float)i / guids.Count);
            foreach (string dependency in AssetDatabase.GetDependencies(path, recursive: true))
                referenced.Add(dependency);
        }

        return referenced;
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var entry in orphans)
            entry.selected = selected;
    }

    private void DeleteSelected()
    {
        var toDelete = orphans.Where(entry => entry.selected).Select(entry => entry.path).ToList();
        if (toDelete.Count == 0) return;

        if (!EditorUtility.DisplayDialog(
                "Delete orphaned images",
                $"Delete {toDelete.Count} selected image asset(s)? This cannot be undone.",
                "Delete",
                "Cancel"))
            return;

        foreach (string path in toDelete)
        {
            AssetDatabase.DeleteAsset(path);
            Debug.Log($"[PsdOrphanedImageCleaner] Deleted: {path}");
        }

        AssetDatabase.Refresh();
        orphans.RemoveAll(entry => entry.selected);
        Debug.Log($"[PsdOrphanedImageCleaner] Deleted {toDelete.Count} orphaned image asset(s).");
    }

    private static bool IsPsdFile(string path)
    {
        return path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase);
    }

    private static long GetFileSize(string assetPath)
    {
        try
        {
            string absolute = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            return new FileInfo(absolute).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string ToAssetPath(string absolutePath)
    {
        absolutePath = absolutePath.Replace('\\', '/');
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
        if (absolutePath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            return absolutePath.Substring(projectRoot.Length + 1);
        return absolutePath;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024 => $"{bytes / 1_024.0:F0} KB",
            _ => $"{bytes} B",
        };
    }
}
#endif
