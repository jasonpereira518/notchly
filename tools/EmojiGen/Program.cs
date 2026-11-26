// SPDX-License-Identifier: GPL-3.0-or-later
// Builds the command bar's emoji table (src/Notchly.Core/CommandBar/Emoji.tsv) with the same rules as
// upstream's CommandBarEmoji.swift: the popular emoji first, in their hand-picked order, then every
// other single-scalar emoji from U+0 to U+1FAFF sorted by name. .NET has no Unicode name table, so the
// names come from UnicodeData.txt (what ICU's Any-Name gives upstream), lower-cased.
//
//   dotnet run --project tools/EmojiGen -- <UnicodeData.txt> <emoji-data.txt> [output.tsv]
//
// Both inputs are Unicode Consortium files, https://www.unicode.org/Public/UCD/latest/ucd/UnicodeData.txt
// and https://www.unicode.org/Public/UCD/latest/ucd/emoji/emoji-data.txt. They are not kept in the repo.
using System.Globalization;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: EmojiGen <UnicodeData.txt> <emoji-data.txt> [output.tsv]");
    return 1;
}

var output = args.Length > 2 ? args[2] : Path.Combine("src", "Notchly.Core", "CommandBar", "Emoji.tsv");

// --- UnicodeData.txt: code point -> name -------------------------------------------------------------
var names = new Dictionary<int, string>();
foreach (var line in File.ReadLines(args[0]))
{
    var f = line.Split(';');
    if (f.Length < 2) continue;
    var cp = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    // Ranges (CJK, Hangul...) carry "<..., First>" markers and hold no emoji; skip them and controls.
    if (f[1].StartsWith('<')) continue;
    names[cp] = f[1];
}

// --- emoji-data.txt: the four properties the rules use ------------------------------------------------
var emoji = new HashSet<int>();
var presentation = new HashSet<int>();
var modifier = new HashSet<int>();
var modifierBase = new HashSet<int>();
foreach (var raw in File.ReadLines(args[1]))
{
    var text = raw;
    var hash = text.IndexOf('#');
    if (hash >= 0) text = text[..hash];
    var parts = text.Split(';');
    if (parts.Length < 2) continue;
    var set = parts[1].Trim() switch
    {
        "Emoji" => emoji,
        "Emoji_Presentation" => presentation,
        "Emoji_Modifier" => modifier,
        "Emoji_Modifier_Base" => modifierBase,
        _ => null,
    };
    if (set is null) continue;
    var range = parts[0].Trim().Split("..");
    var lo = int.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    var hi = range.Length > 1 ? int.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : lo;
    for (var cp = lo; cp <= hi; cp++) set.Add(cp);
}

// --- the rules ----------------------------------------------------------------------------------------
string Canonical(string s) => string.Concat(s.EnumerateRunes().Where(r => r.Value is not (0xFE0F or 0xFE0E)).Select(r => r.ToString()));

string? NameOf(string glyph)
{
    var parts = new List<string>();
    foreach (var rune in glyph.EnumerateRunes())
    {
        if (rune.Value is 0xFE0F or 0xFE0E) continue;
        if (!names.TryGetValue(rune.Value, out var n) || n.Length == 0 || n.StartsWith("ZERO WIDTH", StringComparison.Ordinal)) continue;
        parts.Add(n);
    }
    return parts.Count == 0 ? null : string.Join(' ', parts).ToLowerInvariant();
}

string Presentation(string source)
{
    var runes = source.EnumerateRunes().ToList();
    return runes.Count == 1 && emoji.Contains(runes[0].Value) && !presentation.Contains(runes[0].Value)
        ? source + "️"
        : source;
}

var seen = new HashSet<string>(StringComparer.Ordinal);
var rows = new List<(string Glyph, string Name, string Identity)>();

(string, string, string)? Make(string glyph, string identity)
{
    if (!seen.Add(Canonical(glyph))) return null;
    var name = NameOf(glyph);
    return name is null ? null : (glyph, name, identity);
}

// The popular emoji, each line the code points of the character as upstream writes it.
var popularFile = Path.Combine(AppContext.BaseDirectory, "popular.txt");
foreach (var line in File.ReadLines(popularFile))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var source = string.Concat(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(h => char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
    if (Make(Presentation(source), source) is { } row) rows.Add(row);
}
var popularCount = rows.Count;

var longTail = new List<(string Glyph, string Name, string Identity)>();
for (var cp = 0; cp <= 0x1FAFF; cp++)
{
    if (cp is >= 0xD800 and <= 0xDFFF) continue;
    var isKeycapBase = cp == 0x23 || cp == 0x2A || cp is >= 0x30 and <= 0x39;
    if (!emoji.Contains(cp) || modifier.Contains(cp)
        || cp is >= 0x1F1E6 and <= 0x1F1FF || cp is >= 0x1F9B0 and <= 0x1F9B3 || isKeycapBase) continue;
    var glyph = char.ConvertFromUtf32(cp) + (presentation.Contains(cp) ? "" : "️");
    if (Make(glyph, glyph) is { } row) longTail.Add(row);
}
longTail.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
rows.AddRange(longTail);

// --- write --------------------------------------------------------------------------------------------
// glyph TAB name [TAB identity, only for the popular rows whose stored identity differs from the glyph].
var sb = new StringBuilder();
sb.Append("# Generated by tools/EmojiGen from the Unicode Character Database (UnicodeData.txt, emoji-data.txt).\n");
sb.Append("# glyph<TAB>name[<TAB>identity]. The popular rows come first, then every other emoji sorted by name.\n");
sb.Append("# The @modifier-bases line lists the scalars that can take a skin tone (hex, inclusive ranges).\n");
// The Emoji_Modifier_Base property (which emoji can wear a skin tone) as inclusive hex ranges: .NET has no
// property table for it either.
var ranges = new List<string>();
var sorted = modifierBase.Order().ToList();
for (var i = 0; i < sorted.Count;)
{
    var j = i;
    while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
    ranges.Add(i == j ? $"{sorted[i]:X}" : $"{sorted[i]:X}..{sorted[j]:X}");
    i = j + 1;
}
sb.Append("@modifier-bases\t").Append(string.Join(' ', ranges)).Append('\n');
foreach (var (glyph, name, identity) in rows)
{
    sb.Append(glyph).Append('\t').Append(name);
    if (identity != glyph) sb.Append('\t').Append(identity);
    sb.Append('\n');
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"{rows.Count} emoji ({popularCount} popular + {longTail.Count} long tail) -> {output}");
return 0;
