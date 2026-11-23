// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentOpenCodeReader.swift: reads OpenCode's messages
// from its SQLite database, incrementally by rowid with replies still being written looked up again, through
// Microsoft.Data.Sqlite opened read-only. The SQL and the row-to-line mapping are Notchly.Core's OpenCodeRows;
// only the values the parser reads leave the database. Nothing from it is archived: each launch reads it again.
using System.IO;
using Microsoft.Data.Sqlite;
using Notchly.App.Platform.Shell;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

internal static class OpenCodeDatabase
{
    /// <summary>OpenCode's database: <c>%XDG_DATA_HOME%\opencode\opencode.db</c>, by default under <c>.local\share</c>.</summary>
    public static string DefaultPath => Path.Combine(AgentPaths.OpenCodeDataDirectory, OpenCodeRows.Database);

    /// <summary>When the database or its write-ahead log last changed; null when there is no database.</summary>
    public static DateTimeOffset? Modified(string path)
    {
        if (AgentLogFiles.Modified(path) is not { } database) return null;
        var wal = AgentLogFiles.Modified(path + "-wal") ?? AgentTime.DistantPast;
        return AgentTime.Max(database, wal);
    }

    private static SqliteConnection? Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using var busy = connection.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout = 2000";
            busy.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Reads the messages saved since the last read, or since <paramref name="horizon"/> on a first read, and the
    /// replies still being written that changed since, handing each one's line to <paramref name="line"/>. SQLite
    /// keeps rows in the order they were saved: new rows are past the last rowid read, and the only rows that change
    /// afterwards are replies still being written.
    /// </summary>
    public static void ReadAppended(AgentLogCursor cursor, DateTimeOffset horizon, Func<bool> shouldContinue, Action<ReadOnlyMemory<byte>> line)
    {
        if (!shouldContinue()) return;
        if (FileIdentity.Read(cursor.Path) is not { IsDirectory: false } facts) return;
        if (facts.Identity != cursor.Identity)
        {
            var replaced = cursor.Identity.Length > 0;
            cursor.Identity = facts.Identity;
            Restart(cursor, replaced, line);
        }
        cursor.Modified = Modified(cursor.Path) ?? cursor.Modified;
        try
        {
            using var db = Open(cursor.Path);
            if (db is null) return;
            Read(db, cursor, horizon, shouldContinue, line);
        }
        catch (SqliteException) { }
        catch (InvalidOperationException) { }
    }

    private static void Read(SqliteConnection db, AgentLogCursor cursor, DateTimeOffset horizon, Func<bool> shouldContinue,
                             Action<ReadOnlyMemory<byte>> line)
    {
        var version2 = Exists(db, OpenCodeRows.Version2Probe);
        var messages = OpenCodeRows.MessageTable(version2);
        // Reverting a session deletes its newest messages, and SQLite gives their rowids to the next ones saved.
        if (!Resumes(db, cursor, messages)) Restart(cursor, true, line);
        var floor = OpenCodeRows.Floor(horizon);
        if (cursor.Offset == 0)
        {
            // OpenCode 2 copies an upgraded database's history session by session, so its rows are not saved in time
            // order; the index it keeps on their times finds the first recent one instead.
            var indexed = version2 ? FirstIndexedRow(db, floor) : null;
            cursor.Offset = indexed ?? FirstRow(db, messages, floor);
        }
        var hasParent = Exists(db, OpenCodeRows.ParentProbe(version2));
        var hasParts = Exists(db, OpenCodeRows.PartsProbe);
        var select = OpenCodeRows.Select(version2, hasParts, hasParent, version2 ? floor : 0);
        var now = DateTimeOffset.UtcNow;

        // Replies still being written, before anything saved after them.
        var open = cursor.OpenCode.Open.Keys.ToList();
        if (open.Count > 0)
        {
            using var command = db.CreateCommand();
            command.CommandText = select + " WHERE m.id IN (" + string.Join(", ", open.Select((_, i) => "$o" + i)) + ") ORDER BY m.rowid";
            for (var i = 0; i < open.Count; i++) command.Parameters.AddWithValue("$o" + i, open[i]);
            var found = new HashSet<string>();
            using (var reader = command.ExecuteReader())
            {
                while (shouldContinue() && reader.Read())
                {
                    var row = Row(reader);
                    if (row.Id is null || !cursor.OpenCode.Open.TryGetValue(row.Id, out var known)) continue;
                    found.Add(row.Id);
                    // Unchanged since the last read: nothing is copied or parsed.
                    if ((row.TimeUpdated ?? 0) == known.Updated && (row.Length ?? 0) == known.Length)
                    {
                        if (now.Since(known.Changed) > OpenCodeRows.Abandoned) cursor.OpenCode.Open.Remove(row.Id);
                        continue;
                    }
                    HandOver(row, cursor, now, version2, line);
                }
            }
            // A reply deleted with its session or by a revert is written no more.
            if (!shouldContinue()) return;
            foreach (var id in open.Where(id => !found.Contains(id))) cursor.OpenCode.Open.Remove(id);
        }

        // Everything saved since the last read.
        using var rows = db.CreateCommand();
        rows.CommandText = select + " WHERE m.rowid > $after ORDER BY m.rowid";
        rows.Parameters.AddWithValue("$after", cursor.Offset);
        using var all = rows.ExecuteReader();
        while (shouldContinue() && all.Read())
        {
            var row = Row(all);
            cursor.Offset = Math.Max(0, row.RowId);
            if (row.Id is { } id)
            {
                cursor.OpenCode.Tail.Add((row.RowId, id));
                if (cursor.OpenCode.Tail.Count > OpenCodeRows.TailLength) cursor.OpenCode.Tail.RemoveAt(0);
            }
            HandOver(row, cursor, now, version2, line);
        }
    }

    private static OpenCodeRow Row(SqliteDataReader reader)
    {
        string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetValue(column) switch
        {
            string text => text,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
        };
        long? Number(int column) => reader.IsDBNull(column) ? null : reader.GetValue(column) is long n ? n : null;
        return new OpenCodeRow(Number(0) ?? 0, Text(1), Text(2), Number(3), Number(4), Number(5), Text(6), Text(7), Text(8),
                               (Number(9) ?? 0) != 0, (Number(10) ?? 0) != 0, (Number(11) ?? 0) != 0, Text(12));
    }

    /// <summary>Hands over a row, and keeps a reply still being written to look at again.</summary>
    private static void HandOver(OpenCodeRow row, AgentLogCursor cursor, DateTimeOffset now, bool version2, Action<ReadOnlyMemory<byte>> line)
    {
        if (row.Id is null || row.SessionId is null || row.TimeCreated is null) return;
        if (row.Writing) cursor.OpenCode.Open[row.Id] = (row.TimeUpdated ?? 0, row.Length ?? 0, now);
        else cursor.OpenCode.Open.Remove(row.Id);
        if (OpenCodeRows.Line(row, version2) is { } bytes) line(bytes);
    }

    /// <summary>Starts over from the horizon, telling the parser to forget what the earlier reading left open.</summary>
    private static void Restart(AgentLogCursor cursor, bool announce, Action<ReadOnlyMemory<byte>> line)
    {
        cursor.Offset = 0;
        cursor.OpenCode = new OpenCodeProgress();
        cursor.State = new AgentLogState();
        if (announce) line(OpenCodeRows.ResetLine);
    }

    /// <summary>False when every row read last is gone or holds another message. When only the newest were deleted,
    /// reading goes on after the newest one still there.</summary>
    private static bool Resumes(SqliteConnection db, AgentLogCursor cursor, string table)
    {
        var tail = cursor.OpenCode.Tail;
        if (tail.Count == 0) return true;
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT id FROM {table} WHERE rowid = $row";
        var parameter = command.Parameters.Add("$row", SqliteType.Integer);
        for (var position = tail.Count - 1; position >= 0; position--)
        {
            parameter.Value = tail[position].RowId;
            if (command.ExecuteScalar() is not string id || id != tail[position].Id) continue;
            cursor.Offset = tail[position].RowId;
            tail.RemoveRange(position + 1, tail.Count - 1 - position);
            return true;
        }
        return false;
    }

    /// <summary>The rowid just before the first message created at <paramref name="floor"/> or later, by binary search:
    /// rows are stored in the order they were saved, so their times only grow along them.</summary>
    private static long FirstRow(SqliteConnection db, string table, long floor)
    {
        if (floor <= 0) return 0;
        using var last = db.CreateCommand();
        last.CommandText = $"SELECT max(rowid) FROM {table}";
        var high = last.ExecuteScalar() is long max ? max : 0;
        using var probe = db.CreateCommand();
        probe.CommandText = $"SELECT rowid, time_created FROM {table} WHERE rowid >= $row ORDER BY rowid LIMIT 1";
        var parameter = probe.Parameters.Add("$row", SqliteType.Integer);
        long low = 0;
        // Everything up to low is older than the floor; the answer is at most high.
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            parameter.Value = middle;
            using var reader = probe.ExecuteReader();
            if (!reader.Read())
            {
                high = middle - 1;
                continue;
            }
            var rowid = reader.GetInt64(0);
            if (reader.IsDBNull(1) || reader.GetValue(1) is not long created || created < floor) low = rowid;
            else high = middle - 1;
        }
        return low;
    }

    /// <summary>The rowid just before the first OpenCode 2 message created at <paramref name="floor"/> or later, found on
    /// the index OpenCode keeps on their times, or the newest rowid when none is that recent; null without that index.</summary>
    private static long? FirstIndexedRow(SqliteConnection db, long floor)
    {
        if (floor <= 0) return 0;
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT (SELECT min(rowid) FROM session_message INDEXED BY session_message_time_created_idx
                        WHERE time_created >= $floor),
                    (SELECT max(rowid) FROM session_message)
                """;
            command.Parameters.AddWithValue("$floor", floor);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (reader.IsDBNull(0)) return reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            return Math.Max(0, reader.GetInt64(0) - 1);
        }
        catch (SqliteException) { return null; }
    }

    private static bool Exists(SqliteConnection db, string query)
    {
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            return reader.Read();
        }
        catch (SqliteException) { return false; }
    }
}
