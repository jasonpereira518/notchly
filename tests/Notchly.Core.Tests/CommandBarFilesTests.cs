// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the "Finding a file from the bar" block in upstream Tests/CommandBarFeatureTests.swift.
// The Spotlight expression assertions became Windows Search SQL assertions; the checks on
// registered defaults, settings backup and source rank bias belong to other ports and are not here.
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarFilesTests
{
    private const string Home = @"C:\Users\x";

    private static readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Users\x\Notes", @"C:\Users\x\Documents", @"C:\Users\x\Downloads", @"D:\Work", @"C:\Temp",
    };

    private static bool IsDirectory(string path) => Directories.Contains(path);

    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.GetValueOrDefault(name);
    }

    // MARK: - What is asked of Windows Search

    [Fact]
    public void EveryWordHasToBeInTheNameInAnyOrder() =>
        Assert.Equal("System.FileName LIKE '%annual%' AND System.FileName LIKE '%report%'",
            CommandBarFileSearchSupport.Expression("annual report"));

    [Fact]
    public void OneLetterIsNotASearchSoTheIndexIsNeverAsked()
    {
        Assert.Null(CommandBarFileSearchSupport.Expression("a"));
        Assert.Null(CommandBarFileSearchSupport.Expression("   "));
        Assert.Null(CommandBarFileSearchSupport.Expression(""));
        Assert.Null(CommandBarFileSearchSupport.Expression(" a "));
        Assert.NotNull(CommandBarFileSearchSupport.Expression("ab"));
        Assert.NotNull(CommandBarFileSearchSupport.Expression("a b"));
        // One emoji with a skin tone is one character, not four code units.
        Assert.Null(CommandBarFileSearchSupport.Expression("\U0001F44D\U0001F3FD"));
        Assert.Equal(2, CommandBarFileSearchSupport.ShortestQuery);
    }

    [Fact]
    public void WordsAreSplitOnAnyWhitespace() =>
        Assert.Equal("System.FileName LIKE '%a%' AND System.FileName LIKE '%b%' AND System.FileName LIKE '%c%'",
            CommandBarFileSearchSupport.Expression("  a\t b\u3000c \n"));

    [Fact]
    public void APatternCharacterSomebodyTypedIsTheCharacterNotAWiderSearch()
    {
        // Adapted: Spotlight's backslash escapes of * ? and " became Windows Search SQL escapes.
        Assert.Equal("100[%]", CommandBarFileSearchSupport.EscapedLike("100%"));
        Assert.Equal("a[_]b", CommandBarFileSearchSupport.EscapedLike("a_b"));
        Assert.Equal("[[]draft]", CommandBarFileSearchSupport.EscapedLike("[draft]"));
        Assert.Equal("don''t", CommandBarFileSearchSupport.EscapedLike("don't"));
        Assert.Equal("re*port?", CommandBarFileSearchSupport.EscapedLike("re*port?"));
        Assert.Equal("System.FileName LIKE '%50[%]%' AND System.FileName LIKE '%off%' AND System.FileName LIKE '%it''s%'",
            CommandBarFileSearchSupport.Expression("50% off it's"));
    }

    [Fact]
    public void ACrossedQuoteCanNeverEscapeTheLiteral()
    {
        var sql = CommandBarFileSearchSupport.Sql("x' OR 1=1 --", [@"C:\a'b"]);
        Assert.NotNull(sql);
        Assert.Contains("LIKE '%x''%'", sql);
        Assert.Contains("SCOPE='file:C:/a''b'", sql);
        Assert.Equal(0, sql!.Replace("''", "").Count(c => c == '\'') % 2);
    }

    [Fact]
    public void TheStatementAsksForAtMostTheCandidateLimitInsideTheScopes()
    {
        var sql = CommandBarFileSearchSupport.Sql("annual report", [@"C:\Users\x\Documents", @"D:\Work"]);
        Assert.Equal(
            "SELECT TOP 1000 System.ItemPathDisplay FROM SystemIndex WHERE "
            + "(SCOPE='file:C:/Users/x/Documents' OR SCOPE='file:D:/Work') AND "
            + "System.FileName LIKE '%annual%' AND System.FileName LIKE '%report%'", sql);
    }

    [Fact]
    public void NoScopeOrNoQueryMeansNoStatement()
    {
        Assert.Null(CommandBarFileSearchSupport.Sql("annual", []));
        Assert.Null(CommandBarFileSearchSupport.Sql("a", [@"C:\Users\x\Documents"]));
    }

    [Fact]
    public void WhatComesBackIsCheckedAgainstTheSameWordsAgain()
    {
        Assert.True(CommandBarFileSearchSupport.MatchesAllWords(@"C:\x\Report annual.pdf", "annual REPORT"));
        Assert.False(CommandBarFileSearchSupport.MatchesAllWords(@"C:\annual\report\x.pdf", "annual report"));
        Assert.True(CommandBarFileSearchSupport.MatchesAllWords(@"C:\x\50% off.txt", "50% off"));
        Assert.False(CommandBarFileSearchSupport.MatchesAllWords(@"C:\x\500 off.txt", "50% off"));
        Assert.False(CommandBarFileSearchSupport.MatchesAllWords(@"C:\x\a.txt", ""));
    }

    [Fact]
    public void ResultsAreSortedByNameThenPath()
    {
        Assert.Equal([@"D:\a\apple.txt", @"C:\z\Banana.txt", @"E:\z\banana.txt", @"C:\y\cherry.md"],
            CommandBarFileSearchSupport.Sorted([@"C:\y\cherry.md", @"E:\z\banana.txt", @"C:\z\Banana.txt", @"D:\a\apple.txt"]));
    }

    // MARK: - Where it looks

    [Fact]
    public void SavedScopesAreLiveDirectoriesDeduplicatedAfterExpansion()
    {
        var env = Env(("USERPROFILE", Home));
        Assert.Equal([@"C:\Users\x\Notes", @"C:\Temp"],
            CommandBarFileSearchSupport.ResolvedScopes(
                [@"%USERPROFILE%\Notes", @"%USERPROFILE%\Notes\", @"c:\users\x\notes", @"%USERPROFILE%\single.txt", "C:/Temp"],
                Home, [], IsDirectory, env));
    }

    [Fact]
    public void HomeExpandsOnlyToVisibleOrdinaryDirectories()
    {
        var env = Env(("USERPROFILE", Home));
        Assert.Equal([@"C:\Users\x\Documents"],
            CommandBarFileSearchSupport.ResolvedScopes(
                ["%USERPROFILE%"], Home,
                ["Documents", "AppData", ".ssh", "single.txt", "Application Data"], IsDirectory, env));
        Assert.Equal([@"C:\Users\x\Documents", @"C:\Users\x\Downloads"],
            CommandBarFileSearchSupport.ResolvedScopes(
                [Home + @"\"], Home, ["Documents", "Downloads"], IsDirectory));
    }

    [Fact]
    public void AListThePersonClearedSearchesNothingInsteadOfEverything()
    {
        Assert.Empty(CommandBarFileSearchSupport.ResolvedScopes([], Home, ["Documents"], IsDirectory));
        Assert.Empty(CommandBarFileSearchSupport.ResolvedScopes(["", "  "], Home, ["Documents"], IsDirectory));
    }

    [Fact]
    public void ThereIsNoTildeOnWindows() =>
        Assert.Empty(CommandBarFileSearchSupport.ResolvedScopes(["~"], Home, ["Documents"], IsDirectory));

    [Fact]
    public void AnUnknownVariableStaysAsWritten()
    {
        Assert.Equal(@"%NOPE%\x", CommandBarFileSearchSupport.ExpandEnvironment(@"%NOPE%\x", Env()));
        Assert.Equal(@"C:\Users\x\50% off", CommandBarFileSearchSupport.ExpandEnvironment(@"%userprofile%\50% off",
            Env(("USERPROFILE", @"C:\Users\x"))));
        Assert.Equal("100%", CommandBarFileSearchSupport.ExpandEnvironment("100%", Env()));
    }

    [Theory]
    [InlineData(@"C:\a\b\", @"C:\a\b")]
    [InlineData("C:/a//b/./c/..", @"C:\a\b")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", @"C:\")]
    [InlineData(@"C:\..\..", @"C:\")]
    [InlineData(@"\\server\share\x\", @"\\server\share\x")]
    [InlineData(@"\\server\share\..\..\..", @"\\server\share")]
    [InlineData("  ", "")]
    public void PathsAreStandardizedTextually(string path, string expected) =>
        Assert.Equal(expected, CommandBarFileSearchSupport.Standardized(path));

    // MARK: - What never comes back

    [Fact]
    public void AHiddenFileAndAnythingUnderAHiddenFolderIsNeverOffered()
    {
        Func<string, bool> none = _ => false;
        Assert.True(CommandBarFileSearchSupport.IsOfferable(@"C:\Users\x\Notes\plan.md", none));
        Assert.False(CommandBarFileSearchSupport.IsOfferable(@"C:\Users\x\.ssh\config", none));
        Assert.False(CommandBarFileSearchSupport.IsOfferable(@"C:\Users\x\Notes\.draft.md", none));
        Assert.False(CommandBarFileSearchSupport.IsOfferable("", none));
    }

    [Fact]
    public void WhatTheDiskCallsHiddenSealsTheFolderAndEverythingInIt()
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\ProgramData", @"C:\Users\x\Notes\Secret", @"C:\Users\x\Notes\hidden.txt",
        };
        Assert.False(CommandBarFileSearchSupport.IsOfferable(@"C:\ProgramData\app\a.txt", hidden.Contains));
        Assert.False(CommandBarFileSearchSupport.IsOfferable(@"C:\Users\x\Notes\Secret\data\item", hidden.Contains));
        Assert.False(CommandBarFileSearchSupport.IsOfferable(@"C:\Users\x\Notes\hidden.txt", hidden.Contains));
        Assert.True(CommandBarFileSearchSupport.IsOfferable(@"C:\Users\x\Notes\visible.txt", hidden.Contains));
    }

    [Fact]
    public void TheDriveItselfIsNeverAskedAboutAndForwardSlashesWork()
    {
        var asked = new List<string>();
        Assert.True(CommandBarFileSearchSupport.IsOfferable("C:/a/b.txt", path => { asked.Add(path); return false; }));
        Assert.Equal([@"C:\a", @"C:\a\b.txt"], asked);
    }

    [Fact]
    public void ANameNeverWorthShowingMatchesAWholeNameNeverHalfOfOne()
    {
        Assert.True(CommandBarFileSearchSupport.IsIgnored(@"C:\x\node_modules\a\index.js", ["node_modules"]));
        Assert.True(CommandBarFileSearchSupport.IsIgnored(@"C:\x\NODE_MODULES\a\index.js", ["node_modules"]));
        Assert.True(CommandBarFileSearchSupport.IsIgnored("C:/x/Node_Modules/a.js", [" Node_Modules "]));
        Assert.False(CommandBarFileSearchSupport.IsIgnored(@"C:\x\rebuild-notes.md", ["build"]));
        Assert.False(CommandBarFileSearchSupport.IsIgnored(@"C:\x\a.md", []));
        Assert.False(CommandBarFileSearchSupport.IsIgnored(@"C:\x\a.md", ["", "  "]));
    }

    [Fact]
    public void AnExtensionWrittenEitherWayTakesOutTheKindOfFileNotAFolderCalledLog()
    {
        Assert.True(CommandBarFileSearchSupport.IsIgnored(@"C:\x\run.log", ["*.log"]));
        Assert.True(CommandBarFileSearchSupport.IsIgnored(@"C:\x\run.log", [".log"]));
        Assert.True(CommandBarFileSearchSupport.IsIgnored(@"C:\x\RUN.LOG", [".log"]));
        Assert.False(CommandBarFileSearchSupport.IsIgnored(@"C:\x\log", [".log"]));
        Assert.False(CommandBarFileSearchSupport.IsIgnored(@"C:\x\catalog", ["*.log"]));
    }

    [Fact]
    public void OnePathIsOneRowAndWhatIsFilteredStaysFiltered()
    {
        Assert.Equal([@"C:\x\a.md", @"C:\x\c.md"],
            CommandBarFileSearchSupport.Offerable(
                [@"C:\x\a.md", @"C:\x\a.md", @"C:\x\A.MD", @"C:\x\.hidden", @"C:\x\node_modules\b.js", @"C:\x\c.md"],
                ["node_modules"], _ => false));
    }

    [Fact]
    public void TheHiddenCheckAsksOncePerFolder()
    {
        var asked = new List<string>();
        CommandBarFileSearchSupport.Offerable([@"C:\x\a.md", @"C:\x\b.md"], [], path => { asked.Add(path); return false; });
        Assert.Equal(1, asked.Count(path => path == @"C:\x"));
    }

    [Fact]
    public void NoMoreThanTheResultLimitSurvives()
    {
        var many = Enumerable.Range(0, 500).Select(i => $@"C:\x\file{i}.txt");
        Assert.Equal(CommandBarFileSearchSupport.ResultLimit,
            CommandBarFileSearchSupport.Offerable(many, [], _ => false).Count);
    }

    [Fact]
    public void ALateAsynchronousSearchNeverRefreshesTheVisibleBar()
    {
        Assert.True(CommandBarFileSearchSupport.ShouldPublishResult("new", "new"));
        Assert.False(CommandBarFileSearchSupport.ShouldPublishResult("old", "new"));
        Assert.False(CommandBarFileSearchSupport.ShouldPublishResult("old", null));
    }

    // MARK: - What is stored

    [Fact]
    public void TheSavedListsNeverRepeatThemselvesAndSurviveARoundTrip()
    {
        Assert.Equal(["a", "b"], CommandBarFileSearchSupport.DecodeList(" a \n\n b \na"));
        Assert.Equal("a\nb", CommandBarFileSearchSupport.EncodeList(["a", "", "a", "b"]));
        Assert.Equal(["a", "b"], CommandBarFileSearchSupport.DecodeList(CommandBarFileSearchSupport.EncodeList(["a", "b"])));
    }

    [Fact]
    public void AWindowsTextBoxListWithCarriageReturnsReadsTheSame()
    {
        Assert.Equal([@"C:\a", @"D:\b"], CommandBarFileSearchSupport.DecodeList("C:\\a\r\nD:\\b\r\n"));
        Assert.Equal(["*.LOG"], CommandBarFileSearchSupport.DecodeList("*.LOG\n*.log"));
        Assert.Empty(CommandBarFileSearchSupport.DecodeList(null));
        Assert.Empty(CommandBarFileSearchSupport.DecodeList(""));
    }

    [Fact]
    public void ThePersonsIgnoresFollowTheShippedOnesWithoutRepeats()
    {
        Assert.Equal(CommandBarFileSearchSupport.ShippedIgnores, CommandBarFileSearchSupport.AllIgnores(null));
        var all = CommandBarFileSearchSupport.AllIgnores("*.log\nNODE_MODULES\n  target ");
        Assert.Equal(CommandBarFileSearchSupport.ShippedIgnores.Concat(["*.log", "target"]), all);
    }

    [Fact]
    public void TheShippedIgnoresAreTheOnesUpstreamShips() =>
        Assert.Equal(["node_modules", ".git", "DerivedData", "Pods", ".build", "vendor"],
            CommandBarFileSearchSupport.ShippedIgnores);

    [Fact]
    public void APathIsWrittenTheShortWayOnlyWhereItReallyIsInsideHome()
    {
        Assert.Equal(@"%USERPROFILE%\Notes", CommandBarFileSearchSupport.Abbreviating(@"C:\Users\x\Notes", Home));
        Assert.Equal(@"%USERPROFILE%\Notes", CommandBarFileSearchSupport.Abbreviating(@"c:\users\X\Notes", Home));
        Assert.Equal("%USERPROFILE%", CommandBarFileSearchSupport.Abbreviating(Home, Home));
        Assert.Equal(@"C:\Temp\a", CommandBarFileSearchSupport.Abbreviating(@"C:\Temp\a", Home));
        Assert.Equal(@"C:\Users\xavier\a", CommandBarFileSearchSupport.Abbreviating(@"C:\Users\xavier\a", Home));
        Assert.Equal(@"C:\a", CommandBarFileSearchSupport.Abbreviating(@"C:\a", ""));
    }

    [Fact]
    public void AnAbbreviatedPathResolvesBackToTheSameFolder()
    {
        var env = Env(("USERPROFILE", Home));
        var short_ = CommandBarFileSearchSupport.Abbreviating(@"C:\Users\x\Notes", Home);
        Assert.Equal([@"C:\Users\x\Notes"], CommandBarFileSearchSupport.ResolvedScopes([short_], Home, [], IsDirectory, env));
    }

    [Fact]
    public void MoreNamesAreAskedForThanAreShownSinceMostAreFilteredAway()
    {
        Assert.True(CommandBarFileSearchSupport.CandidateLimit >= CommandBarFileSearchSupport.ResultLimit);
        Assert.Equal(1000, CommandBarFileSearchSupport.CandidateLimit);
        Assert.Equal(200, CommandBarFileSearchSupport.ResultLimit);
        Assert.Equal(4, CommandBarFileSearchSupport.RowsPerSearch);
    }
}
