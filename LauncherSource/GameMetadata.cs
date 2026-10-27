using System.Globalization;
using System.Text.Json.Serialization;

namespace RpgLauncher;

/// <summary>Which store text to show on cards and in the info dialog.</summary>
internal enum MetadataLanguage
{
    English,
    Japanese
}

internal static class MetadataLanguageLabels
{
    public static string Label(MetadataLanguage language) =>
        language == MetadataLanguage.Japanese ? "日本語" : "English";
}

/// <summary>Localized store text for one language variant.</summary>
internal sealed class GameMetadataText
{
    public string Title { get; set; } = "";
    public string Maker { get; set; } = "";
    public string WorkType { get; set; } = "";
    public string WorkTypeName { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>Store metadata fetched once for a game and then cached locally.</summary>
internal sealed class GameMetadata
{
    public string Source { get; set; } = "DLsite";
    public string Site { get; set; } = "maniax";
    public string ProductId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Maker { get; set; } = "";
    public string WorkType { get; set; } = "";
    public string WorkTypeName { get; set; } = "";
    public DateTime? Released { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public double Match { get; set; }
    public DateTime FetchedUtc { get; set; }

    /// <summary>English store text; null until fetched.</summary>
    public GameMetadataText? English { get; set; }

    /// <summary>True once the work's English edition has been checked for an English title.</summary>
    public bool EnglishTitleChecked { get; set; }

    /// <summary>True when the English text lacks its own title (DLsite's English page repeats the Japanese one).</summary>
    public bool NeedsEnglishTitle() =>
        English is null || string.IsNullOrWhiteSpace(English.Title) || English.Title.Trim() == Title.Trim();

    public string TitleFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.Title)
            ? English!.Title.Trim()
            : Title;

    public string MakerFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.Maker)
            ? English!.Maker.Trim()
            : Maker;

    public string WorkTypeFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.WorkType)
            ? English!.WorkType.Trim()
            : WorkType;

    public string WorkTypeNameFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.WorkTypeName)
            ? English!.WorkTypeName.Trim()
            : WorkTypeName;

    public string? DescriptionFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.Description)
            ? English!.Description
            : Description;

    public IReadOnlyList<string> TagsFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && English is { Tags.Count: > 0 } english
            ? english.Tags
            : Tags;

    public string TagLineFor(MetadataLanguage language) =>
        string.Join(" \u00b7 ", TagsFor(language).Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Line under the card title, e.g. "Circle \u00b7 2023 \u00b7 RPG".</summary>
    public string ShortLineFor(MetadataLanguage language)
    {
        var parts = new List<string>();
        string maker = MakerFor(language);
        if (!string.IsNullOrWhiteSpace(maker)) parts.Add(maker.Trim());

        if (Released is { } released)
            parts.Add(released.Year.ToString(CultureInfo.InvariantCulture));

        string type = WorkTypeFor(language);
        if (string.IsNullOrWhiteSpace(type)) type = WorkTypeNameFor(language);
        if (!string.IsNullOrWhiteSpace(type)) parts.Add(type.Trim());

        return string.Join(" \u00b7 ", parts);
    }

    public string UrlFor(MetadataLanguage language)
    {
        if (language != MetadataLanguage.English || English is null) return Url;
        if (string.IsNullOrWhiteSpace(Url) || Url.Contains("locale=", StringComparison.OrdinalIgnoreCase))
            return Url;
        return Url.TrimEnd('/') + "/?locale=en_US";
    }
}

internal sealed class MetadataLookupResult
{
    public GameMetadata? Metadata { get; init; }
    public GameMetadataText? Translation { get; init; }
    public bool NotFound { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the Japanese lookup worked but the English request failed.</summary>
    public bool EnglishFailed { get; init; }

    public static MetadataLookupResult Found(GameMetadata metadata, bool englishFailed = false) =>
        new() { Metadata = metadata, EnglishFailed = englishFailed };

    public static MetadataLookupResult FoundTranslation(GameMetadataText translation) =>
        new() { Translation = translation };

    public static MetadataLookupResult Miss() => new() { NotFound = true };

    public static MetadataLookupResult Fail(string error) => new() { Error = error };
}
