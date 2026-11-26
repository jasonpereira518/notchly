// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/CommandBar/CommandBarEmoji.swift and the emoji lines of CommandBarSupport.swift.
using System.Globalization;
using System.Text;

namespace Notchly.Core.CommandBar;

/// <summary>
/// One emoji as the bar offers it: the character, and the words that find it. <see cref="Identity"/> is the
/// character as it was first written, which row ids and pins are stored under: it is the popular list's own
/// spelling (so a popular emoji's selector never changes its id) and never carries a skin tone.
/// </summary>
public sealed record EmojiItem(string Glyph, string Name, string Keywords, string Identity)
{
    public EmojiItem(string glyph, string name, string keywords) : this(glyph, name, keywords, glyph) { }
}

/// <summary>An emoji in one skin tone, as the actions of a row offer it.</summary>
public sealed record EmojiToneVariant(CommandBarEmoji.SkinTone Tone, string Glyph);

/// <summary>
/// The emoji the command bar can type at the cursor, and the words that find them. The set and the names
/// ship as the embedded <c>Emoji.tsv</c> (built by tools/EmojiGen from the Unicode data files, because .NET
/// has no Unicode name table); the popular order and the aliases live here.
/// </summary>
public static class CommandBarEmoji
{
    /// <summary>The five skin tones Unicode offers, and the yellow default. The stored names never change.</summary>
    public enum SkinTone { None, Light, MediumLight, Medium, MediumDark, Dark }

    /// <summary>Every tone, the default first.</summary>
    public static IReadOnlyList<SkinTone> AllTones { get; } =
        [SkinTone.None, SkinTone.Light, SkinTone.MediumLight, SkinTone.Medium, SkinTone.MediumDark, SkinTone.Dark];

    /// <summary>The stored preference for a tone: "", "light", "mediumLight", "medium", "mediumDark", "dark".</summary>
    public static string RawValue(this SkinTone tone) => tone switch
    {
        SkinTone.Light => "light",
        SkinTone.MediumLight => "mediumLight",
        SkinTone.Medium => "medium",
        SkinTone.MediumDark => "mediumDark",
        SkinTone.Dark => "dark",
        _ => "",
    };

    /// <summary>A stored preference back to its tone, or null for a name this version does not know.</summary>
    public static SkinTone? TryParseTone(string? raw) => raw switch
    {
        "" => SkinTone.None,
        "light" => SkinTone.Light,
        "mediumLight" => SkinTone.MediumLight,
        "medium" => SkinTone.Medium,
        "mediumDark" => SkinTone.MediumDark,
        "dark" => SkinTone.Dark,
        _ => null,
    };

    /// <summary>The modifier scalar that carries a tone (U+1F3FB..U+1F3FF), or null for the default.</summary>
    public static string? Modifier(this SkinTone tone) => tone switch
    {
        SkinTone.Light => "\U0001F3FB",
        SkinTone.MediumLight => "\U0001F3FC",
        SkinTone.Medium => "\U0001F3FD",
        SkinTone.MediumDark => "\U0001F3FE",
        SkinTone.Dark => "\U0001F3FF",
        _ => null,
    };

    /// <summary>The same raised hand in each tone. A picker of these shows what the choice changes.</summary>
    public static string Swatch(this SkinTone tone) => Applying(tone, "✋");

    /// <summary>Matches and searches that read this are capped at this many rows.</summary>
    public const int ResultLimit = 40;

    /// <summary>
    /// Popular emoji first, followed by every single-scalar emoji in Unicode sorted by name. Loaded once, on
    /// first use, from the embedded table.
    /// </summary>
    public static IReadOnlyList<EmojiItem> All => Table.Value.Items;

    /// <summary>The first <see cref="ResultLimit"/> rows, which is what a bare colon shows for browsing.</summary>
    public static IReadOnlyList<EmojiItem> Browse() => All.Take(ResultLimit).ToList();

    /// <summary>The row whose stored identity is this (the untoned character, as a row id carries it).</summary>
    public static EmojiItem? FindByIdentity(string identity) => Table.Value.ByIdentity.GetValueOrDefault(identity);

    /// <summary>
    /// A leading colon scopes the global search to emoji. The marker is not part of the text being matched, so
    /// <c>:fire</c> finds the same emoji as <c>fire</c> inside the Emoji category. Null when the query is an
    /// ordinary one; an empty string for a colon by itself.
    /// </summary>
    public static string? EmojiQuery(string query)
    {
        var trimmed = TrimSpaces(query);
        return trimmed.StartsWith(':') ? TrimSpaces(trimmed[1..]) : null;
    }

    /// <summary>
    /// Single-scalar bases can carry a tone, except the legacy family emoji: it has Emoji_Modifier_Base but no
    /// RGI skin-tone sequences. Multi-scalar sequences need placement rules of their own and are left unchanged.
    /// </summary>
    public static bool AcceptsSkinTone(string character)
    {
        var runes = Canonical(character).EnumerateRunes().ToList();
        return runes.Count == 1 && runes[0].Value != 0x1F46A && Table.Value.ModifierBases.Contains(runes[0].Value);
    }

    /// <summary>
    /// The emoji wearing a tone. The variation selector goes with it, because a modifier already means emoji
    /// presentation and the pickers produce the sequence without it.
    /// </summary>
    public static string Applying(SkinTone tone, string character)
    {
        var modifier = tone.Modifier();
        return modifier is null || !AcceptsSkinTone(character) ? character : Canonical(character) + modifier;
    }

    /// <summary>
    /// The other tones of an emoji, for the person whose default is not the one this message wants: every tone
    /// except <paramref name="current"/>, each with the character wearing it. Empty when the emoji takes no tone.
    /// </summary>
    public static IReadOnlyList<EmojiToneVariant> AlternateTones(string baseCharacter, SkinTone current)
    {
        if (!AcceptsSkinTone(baseCharacter)) return [];
        return AllTones.Where(t => t != current).Select(t => new EmojiToneVariant(t, Applying(t, baseCharacter))).ToList();
    }

    /// <summary>
    /// Variation selectors change presentation, not identity. Folding them keeps a popular text-style sequence
    /// from returning once more as the equivalent bare scalar in the long tail.
    /// </summary>
    internal static string Canonical(string character)
    {
        if (!character.Contains('️') && !character.Contains('︎')) return character;
        return character.Replace("️", "").Replace("︎", "");
    }

    // Foundation's .whitespaces: spaces and tabs, not line breaks.
    private static string TrimSpaces(string value)
    {
        static bool IsSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = value.Length;
        while (start < end && IsSpace(value[start])) start++;
        while (end > start && IsSpace(value[end - 1])) end--;
        return value[start..end];
    }

    /// <summary>
    /// Human search terms that Unicode's formal names do not carry. Keep this deliberately compact: names cover
    /// literal searches, aliases cover the common intent words people actually type into an emoji picker. Keyed
    /// by a row's identity.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["\U0001F602"] = "haha lol roflmao laugh laughing tears funny",
        ["\U0001F923"] = "haha lol rofl roflmao laugh laughing funny",
        ["\U0001F60A"] = "happy smile blush",
        ["\U0001F970"] = "love affection hearts",
        ["\U0001F618"] = "kiss love",
        ["\U0001F60E"] = "cool sunglasses",
        ["\U0001F914"] = "think thinking hmm",
        ["\U0001F644"] = "eyeroll whatever",
        ["\U0001F62D"] = "cry crying sad sob",
        ["\U0001F97A"] = "please pleading puppy eyes",
        ["\U0001F621"] = "angry mad rage",
        ["\U0001F92C"] = "swear cursing angry",
        ["\U0001F937"] = "idk shrug whatever",
        ["\U0001F480"] = "dead death dying skeleton halloween",
        ["☠️"] = "dead death danger poison pirate",
        ["\U0001F64F"] = "appreciate please thanks thank thx you pray prayer high five",
        ["\U0001F44D"] = "yes good approve like okay",
        ["\U0001F44E"] = "no bad disapprove dislike",
        ["\U0001F44C"] = "okay perfect good",
        ["\U0001F44F"] = "clap applause congrats congratulations",
        ["\U0001F64C"] = "hooray celebrate praise",
        ["\U0001FAF6"] = "love heart hands",
        ["\U0001F440"] = "look looking eyes see",
        ["❤️"] = "love heart red",
        ["\U0001F494"] = "heartbreak broken heart sad",
        ["\U0001F525"] = "fire hot lit trending",
        ["✨"] = "sparkle sparkles magic clean",
        ["\U0001F389"] = "party celebrate celebration congrats congratulations",
        ["✅"] = "check done yes complete success",
        ["❌"] = "cross no wrong error fail",
        ["⚠️"] = "warning caution alert",
        ["\U0001F4A1"] = "idea lightbulb tip",
        ["\U0001F680"] = "launch ship rocket fast",
    };

    /// <summary>The words that find an emoji beyond its name, keyed by identity. Exposed for the tests.</summary>
    internal static IReadOnlyDictionary<string, string> AliasTable => Aliases;

    private sealed record LoadedTable(
        IReadOnlyList<EmojiItem> Items,
        IReadOnlyDictionary<string, EmojiItem> ByIdentity,
        HashSet<int> ModifierBases);

    private static readonly Lazy<LoadedTable> Table = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static LoadedTable Load()
    {
        using var stream = typeof(CommandBarEmoji).Assembly.GetManifestResourceStream("Notchly.Core.CommandBar.Emoji.tsv")
            ?? throw new InvalidOperationException("The embedded emoji table is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var items = new List<EmojiItem>(1400);
        var byIdentity = new Dictionary<string, EmojiItem>(StringComparer.Ordinal);
        var bases = new HashSet<int>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split('\t');
            if (fields[0] == "@modifier-bases")
            {
                foreach (var range in fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ends = range.Split("..");
                    var lo = int.Parse(ends[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var hi = ends.Length > 1 ? int.Parse(ends[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : lo;
                    for (var cp = lo; cp <= hi; cp++) bases.Add(cp);
                }
                continue;
            }
            if (fields.Length < 2) continue;
            var identity = fields.Length > 2 ? fields[2] : fields[0];
            var item = new EmojiItem(fields[0], fields[1], Aliases.GetValueOrDefault(identity, ""), identity);
            items.Add(item);
            byIdentity.TryAdd(identity, item);
        }
        return new LoadedTable(items, byIdentity, bases);
    }
}
