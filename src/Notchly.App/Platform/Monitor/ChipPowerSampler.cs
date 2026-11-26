// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: the processor's power, read from the "Energy Meter" counters (Intel RAPL), which is the one
// power figure Windows gives apps besides the battery's. Null where the hardware has no such counters.
using Notchly.Core.Power;

namespace Notchly.App.Platform.Monitor;

public sealed class ChipPowerSampler : IDisposable
{
    private PdhQuery? _query;
    private int _power = -1;
    private bool _primed;

    /// <summary>One read; the first only primes the counters and returns null.</summary>
    public ChipPower? Read()
    {
        if (_query is null)
        {
            _query = new PdhQuery();
            _power = _query.Add(@"\Energy Meter(*)\Power");
        }
        if (_power < 0 || !_query.Collect()) return null;
        if (!_primed) { _primed = true; return null; }
        int? package = null, cores = null, graphics = null;
        foreach (var (instance, value) in _query.Instances(_power))
        {
            // The counter reads in milliwatts.
            var milliwatts = (int)Math.Round(Math.Max(0, value));
            if (instance.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) package = milliwatts;
            else if (instance.EndsWith("_PP0", StringComparison.OrdinalIgnoreCase)) cores = milliwatts;
            else if (instance.EndsWith("_PP1", StringComparison.OrdinalIgnoreCase)) graphics = milliwatts;
        }
        return package is null ? null : new ChipPower(package.Value, cores, graphics);
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _primed = false;
    }
}
