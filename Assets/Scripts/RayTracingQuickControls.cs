using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class RayTracingQuickControls : MonoBehaviour
{
    [Serializable]
    public sealed class Entry
    {
        public string Label;
        public Component Target;
        public string[] PropertyPaths;
    }

    [SerializeField]
    private List<Entry> entries = new List<Entry>();

    public IReadOnlyList<Entry> Entries => entries;

    public void SetEntries(IEnumerable<Entry> values)
    {
        entries.Clear();
        entries.AddRange(values);
    }

    public static Entry CreateEntry(string label, Component target, params string[] propertyPaths)
    {
        return new Entry
        {
            Label = label,
            Target = target,
            PropertyPaths = propertyPaths
        };
    }
}
