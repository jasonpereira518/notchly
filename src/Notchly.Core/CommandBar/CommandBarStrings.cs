// SPDX-License-Identifier: GPL-3.0-or-later
// Upstream's English strings for the command bar (Core/CommandBarStrings.swift, enUS), with Windows' names for its controls.
namespace Notchly.Core.CommandBar;

public static class CommandBarStrings
{
    public const string PageTitle = "Command Bar", SearchPlaceholder = "Type what you want to do", SuggestionsLabel = "Suggestions";
    public const string NoResultsTitle = "Nothing here by that name.", NoResultsAction = "See suggestions";
    public const string ArgumentHint = "Return applies · Esc goes back", ConfirmHint = "Return confirms · Esc cancels";
    public const string KindApp = "App", KindClipboard = "Paste from history", KindSnippet = "Insert snippet", KindWindow = "Window", KindMenu = "Menu command",
                        KindEmoji = "Emoji", KindFolder = "Folder", KindAnswer = "Answer", KindSelection = "Selection", KindLink = "Your shortcut", KindFile = "File";
    public const string QuitFormat = "Quit {0}", QuitConfirmFormat = "Quit {0}?", ForceQuitFormat = "Force Quit {0}…", ForceQuitConfirmFormat = "Force quit {0}? Unsaved changes will be lost.";
    public const string TurnOnFormat = "Turn on {0}", TurnOffFormat = "Turn off {0}", KeepAwakeForFormat = "Keep awake for {0}";
    public const string NeedsSetupFormat = "Turn on {0} in Settings", NotInstalledFormat = "Install {0} in Features";
    public const string CopyHint = "Return copies", CopyFailed = "Couldn’t copy", ReuseHint = "Continue";
    public const string ActionsTitle = "Actions", ActionPin = "Pin to the top", ActionUnpin = "Unpin", ActionName = "Give it your own name",
                        ActionRename = "Change the name it answers to", ActionHide = "Never show this", ActionForget = "Forget how often I use this",
                        ActionShortcut = "Give it a shortcut", ActionShortcutChange = "Change the shortcut", ActionShortcutRemove = "Take the shortcut off",
                        ActionReveal = "Show in Explorer", AliasPlaceholder = "The name you call it", AliasTakenFormat = "{0} already answers to that";
    public const string SelectedTitle = "Selected", PinnedTitle = "Pinned", EverythingTitle = "Everything it can do";
    public const string SelectionCopy = "Copy it", SelectionSearch = "Use it in the search", SelectionUpper = "UPPERCASE", SelectionLower = "lowercase",
                        SelectionTitleCase = "Title Case", SelectionShelf = "Keep it on the shelf", SelectionCount = "Count it", SelectionCountFormat = "Words: {0}, characters: {1}";
    public const string ShortcutCaptureHint = "Press the keys you want · Delete clears it · Esc goes back";
    public const string AnswerBatteryLabel = "Battery", AnswerBatteryCharging = "charging", AnswerBatteryPlugged = "on power",
                        AnswerMemoryLabel = "Memory", AnswerMemoryFormat = "{0} of {1} in use", AnswerStorageLabel = "Storage",
                        AnswerStorageFormat = "{0} available of {1}", AnswerDateLabel = "Today", AnswerTimeLabel = "Time now";
    public const string PowerSleep = "Sleep", PowerRestart = "Restart", PowerRestartConfirm = "Restart the PC?", PowerShutDown = "Shut down",
                        PowerShutDownConfirm = "Shut down the PC?", PowerLogOut = "Sign out", PowerLogOutConfirm = "Sign out?";
    public const string FilesNoFolders = "No folders yet, so the bar looks for no files.", StillLooking = "Still looking";
    public const string SourceActions = "Notchly actions", SourceApps = "Apps", SourceMenus = "Menu commands of the app in front", SourceWindows = "Open windows",
                        SourceQuitApps = "Quit an app", SourceSettingsPages = "Settings pages", SourceMacSettings = "Windows Settings pages", SourceSnippets = "Snippets",
                        SourceClipboard = "Clipboard history", SourceEmoji = "Emoji", SourceFolders = "Folders", SourceAnswers = "Answers about this PC",
                        SourceCalculator = "Sums and conversions", SourceSelection = "What is selected", SourceLinks = "Your shortcuts", SourceFiles = "Files",
                        SourceKillProcess = "Stop a process";

    /// <summary>"{0}"-style formats as text.</summary>
    public static string Format(string format, params object[] arguments) => string.Format(System.Globalization.CultureInfo.CurrentCulture, format, arguments);
}
