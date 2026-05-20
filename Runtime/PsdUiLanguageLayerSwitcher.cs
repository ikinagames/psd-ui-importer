using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Toggles PSD-generated language layers named with !kr, !en, or !jp.
/// This component is intentionally project-agnostic so the PSD UI importer can
/// be copied to another Unity project without depending on this game's tables.
/// </summary>
[DisallowMultipleComponent]
public sealed class PsdUiLanguageLayerSwitcher : MonoBehaviour
{
    [Serializable]
    public sealed class Entry
    {
        public GameObject target;
        public SystemLanguage language = SystemLanguage.English;
    }

    [SerializeField] private bool applyOnEnable = true;
    [SerializeField] private SystemLanguage defaultLanguage = SystemLanguage.English;
    [SerializeField] private Entry[] entries = Array.Empty<Entry>();

    public SystemLanguage CurrentLanguage { get; private set; }
    public IReadOnlyList<Entry> Entries => entries;

    private void OnEnable()
    {
        if (applyOnEnable)
            Apply(Application.systemLanguage);
    }

    public void ApplyDefault()
    {
        Apply(defaultLanguage);
    }

    public void Apply(SystemLanguage language)
    {
        CurrentLanguage = Normalize(language);
        foreach (var entry in entries)
        {
            if (entry == null || entry.target == null) continue;
            entry.target.SetActive(Normalize(entry.language) == CurrentLanguage);
        }
    }

    public void SetEntries(Entry[] newEntries, SystemLanguage initialLanguage)
    {
        entries = newEntries ?? Array.Empty<Entry>();
        defaultLanguage = Normalize(initialLanguage);
        ApplyDefault();
    }

    public void RebuildEntriesFromChildren(SystemLanguage initialLanguage)
    {
        var rebuilt = new List<Entry>();
        foreach (var child in GetComponentsInChildren<Transform>(true).Skip(1))
        {
            if (!TryGetLanguage(child.name, out var language))
                continue;

            rebuilt.Add(new Entry
            {
                target = child.gameObject,
                language = language,
            });
        }

        SetEntries(rebuilt.ToArray(), initialLanguage);
    }

    public static bool TryGetLanguage(string name, out SystemLanguage language)
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

    private static SystemLanguage Normalize(SystemLanguage language)
    {
        return language switch
        {
            SystemLanguage.Korean => SystemLanguage.Korean,
            SystemLanguage.Japanese => SystemLanguage.Japanese,
            _ => SystemLanguage.English,
        };
    }

    private static bool HasToken(string name, string token)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
