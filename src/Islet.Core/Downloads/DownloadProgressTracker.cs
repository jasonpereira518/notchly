// SPDX-License-Identifier: GPL-3.0-or-later
// Windows has no published download progress and NTFS updates a file's listed size and time lazily
// while it is open, so the island follows each partial's growth itself: when it last grew (active or
// paused, upstream's 120 s rule) and how fast (the rate shown instead of a percentage).
namespace Islet.Core.Downloads;

public sealed class DownloadProgressTracker
{
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromSeconds(0.9);

    private sealed class State
    {
        public long Bytes;
        public DateTime LastGrowth;
        public readonly List<(DateTime At, long Bytes)> Samples = [];
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a size reading. A partial first seen counts from its own last write, so one left behind
    /// days ago starts paused while a transfer already under way starts active.
    /// </summary>
    public void Observe(string path, long bytes, DateTime modified, DateTime now)
    {
        if (!_states.TryGetValue(path, out var state))
        {
            state = new State { Bytes = bytes, LastGrowth = modified < now ? modified : now };
            _states[path] = state;
        }
        else if (bytes != state.Bytes)
        {
            state.Bytes = bytes;
            state.LastGrowth = now;
        }
        state.Samples.Add((now, bytes));
        // Keep the window plus the one sample before it, so the rate always spans the full window.
        var cutoff = now - RateWindow;
        var firstInside = state.Samples.FindIndex(s => s.At >= cutoff);
        if (firstInside > 1) state.Samples.RemoveRange(0, firstInside - 1);
    }

    /// <summary>The latest size read for a partial.</summary>
    public long? Bytes(string path) => _states.TryGetValue(path, out var state) ? state.Bytes : null;

    public bool IsActive(string path, DateTime now) =>
        _states.TryGetValue(path, out var state) && now - state.LastGrowth < DownloadSupport.InactiveAfter;

    /// <summary>Bytes a second over about the last three seconds; null until two readings a second apart.</summary>
    public double? Rate(string path, DateTime now)
    {
        if (!_states.TryGetValue(path, out var state) || state.Samples.Count < 2) return null;
        var cutoff = now - RateWindow;
        var start = state.Samples.FindLastIndex(s => s.At <= cutoff);
        var first = state.Samples[Math.Max(0, start)];
        var last = state.Samples[^1];
        var span = last.At - first.At;
        if (span < MinimumSpan) return null;
        return Math.Max(0, (last.Bytes - first.Bytes) / span.TotalSeconds);
    }

    /// <summary>Forgets partials that are gone.</summary>
    public void Retain(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _states.Keys.Where(p => !keep.Contains(p)).ToList()) _states.Remove(path);
    }

    public void Clear() => _states.Clear();
}
