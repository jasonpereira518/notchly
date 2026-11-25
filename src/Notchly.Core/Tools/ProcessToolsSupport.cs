// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/KillProcess/KillProcessSupport.swift and
// Services/PortManager/PortManagerSupport.swift: the rows' protection, search and order, the descendants a tree kill
// reaches, the start-time identity that stops a reused pid being killed, and the listening ports' dedupe, filter,
// links and refresh rules. Windows differences: the protected set is Windows' own core processes, identity is the
// process creation time, and ports come from the TCP table as structured rows (no lsof text to parse).
namespace Notchly.Core.Tools;

public enum KillSort { Cpu, Memory, Name, Pid }

public readonly record struct KillRow(int Pid, string Name, double Cpu, long MemoryBytes);

public static class KillProcessSupport
{
    /// <summary>Processes Windows cannot run without; they are shown but never killed from here.</summary>
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "memory compression", "secure system", "smss.exe", "csrss.exe", "wininit.exe",
        "services.exe", "lsass.exe", "winlogon.exe", "dwm.exe", "fontdrvhost.exe", "notchly", "notchly.exe",
    };

    /// <summary>Pid 4 and below, the app itself, Windows' core processes, and any row whose start time could not be read.</summary>
    public static bool IsProtected(int pid, string name, int ownPid, bool startTimeKnown) =>
        pid <= 4 || pid == ownPid || ProtectedNames.Contains(name) || !startTimeKnown;

    // MARK: Sorting and search

    public static KillSort ParseSort(string? raw) => raw?.ToLowerInvariant() switch
    {
        "memory" => KillSort.Memory,
        "name" => KillSort.Name,
        "pid" => KillSort.Pid,
        _ => KillSort.Cpu,
    };

    public static string SortName(KillSort sort) => sort.ToString().ToLowerInvariant();

    /// <summary>A column opens descending for numbers and ascending for names.</summary>
    public static bool DefaultAscending(KillSort sort) => sort == KillSort.Name;

    /// <summary>Strictly before: equal values never order themselves.</summary>
    public static bool NumberComesBefore(double a, double b, bool ascending) => ascending ? a < b : a > b;

    public static bool NameComesBefore(string a, string b, bool ascending)
    {
        var comparison = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return ascending ? comparison < 0 : comparison > 0;
    }

    /// <summary>The order of the list: by the chosen column, ties broken by pid ascending.</summary>
    public static IReadOnlyList<KillRow> Sorted(IEnumerable<KillRow> rows, KillSort sort, bool ascending)
    {
        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            bool before(KillRow x, KillRow y) => sort switch
            {
                KillSort.Memory => NumberComesBefore(x.MemoryBytes, y.MemoryBytes, ascending),
                KillSort.Name => NameComesBefore(x.Name, y.Name, ascending),
                KillSort.Pid => NumberComesBefore(x.Pid, y.Pid, ascending),
                _ => NumberComesBefore(x.Cpu, y.Cpu, ascending),
            };
            if (before(a, b)) return -1;
            if (before(b, a)) return 1;
            return a.Pid.CompareTo(b.Pid);
        });
        return list;
    }

    /// <summary>A name substring, ignoring case, or an exact pid.</summary>
    public static bool Matches(string query, string name, int pid)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        return name.Contains(q, StringComparison.OrdinalIgnoreCase) || q == pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // MARK: Trees and identity

    public const int DescendantCap = 4096;

    /// <summary>
    /// Every process below a root, breadth first, safe against cycles and self-parents, capped. The order is
    /// the reverse of the order to kill them in (deepest first).
    /// </summary>
    public static IReadOnlyList<int> Descendants(IEnumerable<(int Pid, int Parent)> pairs, int root)
    {
        var children = pairs.GroupBy(p => p.Parent).ToDictionary(g => g.Key, g => g.Select(p => p.Pid).ToList());
        var seen = new HashSet<int> { root };
        var order = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0 && order.Count < DescendantCap)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!seen.Add(kid)) continue;
                order.Add(kid);
                queue.Enqueue(kid);
                if (order.Count >= DescendantCap) break;
            }
        }
        return order;
    }

    /// <summary>Kill the deepest first, so a parent cannot respawn what is left.</summary>
    public static IReadOnlyList<int> TreeKillOrder(IEnumerable<(int Pid, int Parent)> pairs, int root) =>
        Descendants(pairs, root).Reverse().ToList();

    /// <summary>A kill goes ahead only when the process still has the start time it was listed with.</summary>
    public static bool SameProcess(long? listedStart, long? currentStart) => listedStart is { } a && currentStart is { } b && a == b;

    // MARK: Snapshots and messages

    /// <summary>A failed read, or one that parsed nothing, never wipes a list that was there.</summary>
    public static bool ShouldReplaceList(IReadOnlyCollection<KillRow>? snapshot) => snapshot is { Count: > 0 };

    public enum KillKind { Kill, Force, KillAll, Tree }

    public static string ConfirmTitle(KillKind kind, string name) => kind switch
    {
        KillKind.Force => $"Force Kill {name}?",
        KillKind.KillAll => $"Kill all “{name}” processes?",
        KillKind.Tree => $"Kill {name} and all its child processes?",
        _ => $"Kill {name}?",
    };
}

public readonly record struct PortEntry(int Port, string Protocol, string Address, int Pid, string ProcessName, long? StartedAt);

public static class PortManagerSupport
{
    /// <summary>One row per protocol, port, address and process; ports in order, then by process name.</summary>
    public static IReadOnlyList<PortEntry> DedupedAndSorted(IEnumerable<PortEntry> entries) =>
        entries.GroupBy(e => (e.Protocol, e.Port, e.Address, e.Pid)).Select(g => g.First())
            .OrderBy(e => e.Port).ThenBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Pid).ToList();

    /// <summary>The query matches a lowercase "port process pid address" line.</summary>
    public static bool Matches(PortEntry entry, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return true;
        return $"{entry.Port} {entry.ProcessName} {entry.Pid} {entry.Address}".ToLowerInvariant().Contains(q);
    }

    /// <summary>Listening on every interface: the wildcard addresses, bracketed or not.</summary>
    public static bool ListensOnAllInterfaces(string address)
    {
        var a = address.Trim();
        return a is "*" or "0.0.0.0" or "::" or "[::]";
    }

    /// <summary>Where "open in browser" goes: wildcards become the local machine, IPv6 literals are bracketed and escaped.</summary>
    public static string? BrowserUrl(string protocol, string address, int port)
    {
        if (!string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase) || port is < 1 or > 65535) return null;
        var host = address.Trim();
        if (host.Length == 0) return null;
        host = host switch { "*" => "localhost", "0.0.0.0" => "127.0.0.1", "::" or "[::]" => "[::1]", _ => host };
        if (host.Contains(':') && !host.StartsWith('[')) host = "[" + host + "]";
        host = host.Replace("%", "%25");
        return $"http://{host}:{port}";
    }

    /// <summary>The start time is kept only when it was the same before and after the table was read.</summary>
    public static long? KeepStart(long? before, long? after) => before is { } a && after is { } b && a == b ? a : null;

    /// <summary>The list's refresh bookkeeping: one refresh at a time, and a failure keeps what was shown.</summary>
    public sealed class RefreshState
    {
        public IReadOnlyList<PortEntry> Entries { get; private set; } = [];
        public bool HasLoadedOnce { get; private set; }
        public bool RefreshFailed { get; private set; }
        public bool IsRefreshing { get; private set; }

        /// <summary>False when a refresh is already running: a second call is ignored.</summary>
        public bool TryBegin()
        {
            if (IsRefreshing) return false;
            IsRefreshing = true;
            return true;
        }

        /// <summary>A null snapshot is a failure (a timeout); an empty one is a normal empty list.</summary>
        public void Complete(IReadOnlyList<PortEntry>? snapshot)
        {
            IsRefreshing = false;
            if (snapshot is null) { RefreshFailed = true; return; }
            Entries = snapshot;
            HasLoadedOnce = true;
            RefreshFailed = false;
        }
    }
}
