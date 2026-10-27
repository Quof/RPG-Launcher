namespace RpgLauncher;

/// <summary>What a tag chip does to the list: nothing, require it, or hide games with it.</summary>
internal enum TagState
{
    None,
    Include,
    Exclude
}

/// <summary>Multi-tag filter: games must have every included tag and none of the excluded ones.</summary>
internal sealed class TagFilter
{
    public HashSet<string> Include { get; } = new(StringComparer.CurrentCultureIgnoreCase);
    public HashSet<string> Exclude { get; } = new(StringComparer.CurrentCultureIgnoreCase);

    public bool IsEmpty => Include.Count == 0 && Exclude.Count == 0;
    public int Count => Include.Count + Exclude.Count;

    public TagState StateOf(string tag) =>
        Include.Contains(tag) ? TagState.Include : Exclude.Contains(tag) ? TagState.Exclude : TagState.None;

    /// <summary>Cycles the tag: off, include, exclude, then off again.</summary>
    public TagState Cycle(string tag)
    {
        switch (StateOf(tag))
        {
            case TagState.None:
                Include.Add(tag);
                return TagState.Include;
            case TagState.Include:
                Include.Remove(tag);
                Exclude.Add(tag);
                return TagState.Exclude;
            default:
                Exclude.Remove(tag);
                return TagState.None;
        }
    }

    public void Clear()
    {
        Include.Clear();
        Exclude.Clear();
    }

    /// <summary>True when a game or release with these tags should be shown.</summary>
    public bool Matches(IEnumerable<string>? tags)
    {
        if (IsEmpty) return true;
        var have = new HashSet<string>(tags ?? Enumerable.Empty<string>(), StringComparer.CurrentCultureIgnoreCase);
        return Include.All(have.Contains) && !Exclude.Any(have.Contains);
    }

    /// <summary>Drops tags not in <paramref name="available"/>; returns true when any were removed.</summary>
    public bool RemoveMissing(ICollection<string> available)
    {
        var known = new HashSet<string>(available, StringComparer.CurrentCultureIgnoreCase);
        int removed = Include.RemoveWhere(tag => !known.Contains(tag)) + Exclude.RemoveWhere(tag => !known.Contains(tag));
        return removed > 0;
    }

    /// <summary>Status bar text, e.g. tagged “RPG” + “Fantasy”, hiding “Horror”.</summary>
    public string Describe()
    {
        static string Quote(IEnumerable<string> tags) =>
            string.Join(" + ", tags.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).Select(t => $"“{t}”"));

        var parts = new List<string>();
        if (Include.Count > 0) parts.Add("tagged " + Quote(Include));
        if (Exclude.Count > 0) parts.Add("hiding " + Quote(Exclude));
        return string.Join(", ", parts);
    }
}
