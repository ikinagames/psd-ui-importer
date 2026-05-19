using System;
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

    private static SystemLanguage Normalize(SystemLanguage language)
    {
        return language switch
        {
            SystemLanguage.Korean => SystemLanguage.Korean,
            SystemLanguage.Japanese => SystemLanguage.Japanese,
            _ => SystemLanguage.English,
        };
    }
}
