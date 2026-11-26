// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's Spotlight query (NSMetadataQuery in CommandBarFileSearch.swift): the Windows Search index, asked
// through its OLE DB provider with a SQL string built in Core (CommandBarFileSearchSupport). When the Windows Search service is off
// or the provider is missing, the answer says so and the palette shows that, rather than walking the disk.
using System.Data.OleDb;
using Serilog;

namespace Notchly.App.Platform.Apps;

public sealed record WindowsSearchResult(IReadOnlyList<string> Paths, string? Problem);

public static class WindowsSearchClient
{
    public const string ServiceOff = "Windows Search is turned off, so files cannot be found.";
    private const string ConnectionString = "Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows';OLE DB Services=-4";

    /// <summary>Runs a Windows Search SQL query returning one path column. Honors the token between rows.</summary>
    public static Task<WindowsSearchResult> QueryAsync(string sql, CancellationToken token) => Task.Run(() =>
    {
        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            var paths = new List<string>();
            while (!token.IsCancellationRequested && reader.Read())
                if (reader.GetValue(0) is string path && path.Length > 0) paths.Add(path);
            return new WindowsSearchResult(paths, null);
        }
        catch (Exception e) when (e is OleDbException or InvalidOperationException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            Log.Information("Windows Search is not available: {Message}", e.Message);
            return new WindowsSearchResult([], ServiceOff);
        }
    }, token);
}
