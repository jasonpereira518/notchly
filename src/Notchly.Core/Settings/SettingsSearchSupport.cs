// SPDX-License-Identifier: GPL-3.0-or-later
// Pure matching for the Settings search box, as upstream's SettingsSearchSupport.swift: case-, accent- and
// width-insensitive containment, with a few everyday synonyms. The window feeds it each row's text.
using System.Globalization;
using System.Text;

namespace Notchly.Core.Settings;

public static class SettingsSearchSupport
{
    /// <summary>Everyday words people type for an option whose label uses another term ("storage" finds "Low disk space"). Folded.</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["storage"] = "disk", ["drive"] = "disk", ["ssd"] = "disk",
        ["ram"] = "memory",
        ["warn"] = "alert", ["warning"] = "alert", ["notify"] = "alert", ["notification"] = "alert",
        ["jiggle"] = "pointer slightly", ["interval"] = "update every",
        ["mic"] = "microphone", ["sound"] = "audio", ["volume"] = "audio",
    };

    /// <summary>Lower-cased, accents and width folded, trimmed. Culture-free, so Turkish dotted I folds like any other.</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Trim();
    }

    /// <summary>The query's words, folded. A blank query has none.</summary>
    public static IReadOnlyList<string> Terms(string? query) =>
        Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every term is found in the text (or the term's synonym is). No terms match everything.</summary>
    public static bool Matches(IReadOnlyList<string> terms, string? text) => MatchesFolded(terms, Fold(text));

    /// <summary>As <see cref="Matches"/>, for text already run through <see cref="Fold"/>.</summary>
    public static bool MatchesFolded(IReadOnlyList<string> terms, string foldedText)
    {
        foreach (var term in terms)
        {
            if (foldedText.Contains(term, StringComparison.Ordinal)) continue;
            if (Synonyms.TryGetValue(term, out var alias) && foldedText.Contains(alias, StringComparison.Ordinal)) continue;
            return false;
        }
        return true;
    }
}
