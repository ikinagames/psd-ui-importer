using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Base class for project-specific localization of PSD layers tagged with !tmp.
/// Generated prefabs receive the entry list; each project supplies the lookup.
/// </summary>
public abstract class PsdUiTextLocalizerBase : MonoBehaviour
{
    [Serializable]
    public sealed class Entry
    {
        public TextMeshProUGUI target;
        public string key;
        public string layerName;
        public SystemLanguage language = SystemLanguage.English;
    }

    [SerializeField] private bool applyOnEnable = true;
    [SerializeField] private SystemLanguage defaultLanguage = SystemLanguage.English;
    [SerializeField] private Entry[] entries = Array.Empty<Entry>();

    public IReadOnlyList<Entry> Entries => entries;

    protected virtual void OnEnable()
    {
        if (applyOnEnable)
            Apply(Application.systemLanguage);
    }

    public void SetEntries(Entry[] newEntries, SystemLanguage initialLanguage)
    {
        entries = newEntries ?? Array.Empty<Entry>();
        defaultLanguage = Normalize(initialLanguage);
        ApplyDefault();
    }

    public void ApplyDefault()
    {
        Apply(defaultLanguage);
    }

    public void Apply(SystemLanguage language)
    {
        var normalized = Normalize(language);
        foreach (var entry in entries)
        {
            if (entry == null || entry.target == null)
                continue;

            if (TryGetText(entry, normalized, out string text))
                entry.target.text = text;
        }
    }

    protected abstract bool TryGetText(Entry entry, SystemLanguage language, out string text);

    protected static SystemLanguage Normalize(SystemLanguage language)
    {
        return language switch
        {
            SystemLanguage.Korean => SystemLanguage.Korean,
            SystemLanguage.Japanese => SystemLanguage.Japanese,
            SystemLanguage.Chinese => SystemLanguage.Chinese,
            SystemLanguage.ChineseSimplified => SystemLanguage.ChineseSimplified,
            SystemLanguage.ChineseTraditional => SystemLanguage.ChineseTraditional,
            SystemLanguage.French => SystemLanguage.French,
            SystemLanguage.German => SystemLanguage.German,
            SystemLanguage.Spanish => SystemLanguage.Spanish,
            SystemLanguage.Italian => SystemLanguage.Italian,
            SystemLanguage.Portuguese => SystemLanguage.Portuguese,
            SystemLanguage.Russian => SystemLanguage.Russian,
            _ => SystemLanguage.English,
        };
    }
}
