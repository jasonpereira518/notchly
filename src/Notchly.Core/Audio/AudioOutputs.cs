// SPDX-License-Identifier: GPL-3.0-or-later
// The outputs the island's speaker menu offers, as upstream's NotchAudioControls.outputItems:
// every output that can be the default, the current one ticked.
namespace Notchly.Core.Audio;

public readonly record struct AudioOutput(string Id, string Name);

public readonly record struct AudioOutputChoice(string Id, string Name, bool Current);

public static class AudioOutputMenu
{
    /// <summary>The outputs by name, the current one marked; the same name twice keeps both, in id order.</summary>
    public static IReadOnlyList<AudioOutputChoice> Choices(IEnumerable<AudioOutput> outputs, string? currentId) =>
        outputs
            .GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => new AudioOutputChoice(o.Id, o.Name, string.Equals(o.Id, currentId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
}
