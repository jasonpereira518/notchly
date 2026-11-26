// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the "Remappable screenshot tool shortcuts" block of upstream Tests/ScreenshotFeatureTests.swift and of
// Tests/ScreenshotToolShortcutTests.swift. Key codes are Windows virtual keys (upstream's are macOS key codes) and the
// macOS Command key is Control. The keyboard-layout oracle of the upstream file (real layout data through UCKeyTranslate)
// is replaced by small fake IKeyboardLayout implementations: a US one (the default) and an AZERTY-style one.
using Notchly.Core.Capture;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;

namespace Notchly.Core.Tests;

public class ScreenshotCanvasShortcutTests
{
    private const int KeyT = 0x54, KeyK = 0x4B, KeyN = 0x4E, Key1 = 0x31, Key5 = 0x35, Key0 = 0x30;
    private const ShortcutModifiers None = ShortcutModifiers.None;
    private const ShortcutModifiers Pen = ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Win;

    private static readonly ToolShortcut Text = new(KeyT);
    private static readonly ToolShortcut PenKey = new(KeyK, Pen);
    private static readonly ToolShortcut One = new(Key1);

    /// <summary>A French-style layout: the digit row types symbols bare and digits with Shift (or Caps Lock on the numerical variant).</summary>
    private sealed class AzertyLayout(bool numerical = false) : IKeyboardLayout
    {
        private const string Bare = "&é\"'(-è_ç";

        public string? TypedLabel(int virtualKey, bool shift, bool capsLock)
        {
            if (virtualKey is >= 0x31 and <= 0x39)
            {
                bool digit = shift || (numerical && capsLock);
                return digit ? ((char)virtualKey).ToString() : Bare[virtualKey - 0x31].ToString();
            }
            return UsKeyboardLayout.Instance.TypedLabel(virtualKey, shift, capsLock);
        }
    }

    private static readonly string Raw = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut>
    {
        [ScreenshotTool.Text] = Text,
        [ScreenshotTool.Freehand] = PenKey,
    });

    private static ScreenshotTool? Resolve(ToolShortcut key, int? number = null, string? bindings = null, bool enabled = true,
                                          IKeyboardLayout? layout = null, bool caps = false) =>
        ToolShortcuts.ShortcutTool(key.VirtualKey, key.Modifiers, number, null, bindings ?? Raw, enabled, caps, layout);

    // ---- Storage ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ToolBindingsRoundTrip() =>
        Assert.Equal(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = Text, [ScreenshotTool.Freehand] = PenKey },
                     ToolShortcuts.Bindings(Raw));

    [Fact]
    public void ToolBindingStorageHasAStableToolOrder() => Assert.Equal("text=:84,freehand=control+alt+win:75", Raw);

    [Fact]
    public void BindingsStorageOrdersByTheRailNotByInsertion()
    {
        var raw = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut>
        {
            [ScreenshotTool.Freehand] = PenKey,
            [ScreenshotTool.Arrow] = new ToolShortcut(0x41),
        });
        Assert.Equal("arrow=:65,freehand=control+alt+win:75", raw);
    }

    [Fact]
    public void ABareEditorKeyIsAToolShortcutButNeverAGlobalOne()
    {
        Assert.Equal(Text, ToolShortcut.Parse(Text.StorageValue));
        Assert.Null(Shortcut.Parse(Text.StorageValue));
    }

    [Fact]
    public void ModifierSpellingsFromMacBackupsStillParse()
    {
        Assert.Equal(new ToolShortcut(KeyK, Pen), ToolShortcut.Parse("control+option+command:75"));
        Assert.Equal(new ToolShortcut(KeyK, Pen), ToolShortcut.Parse("control+alt+win:75"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData(":")]
    [InlineData(":abc")]
    [InlineData("super:15")]
    [InlineData(":999999")]
    [InlineData(":0")]
    [InlineData(":16")]
    [InlineData("control:17")]
    public void InvalidShortcutStorageIsDiscarded(string? raw) => Assert.Null(ToolShortcut.Parse(raw));

    [Fact]
    public void InvalidImportedToolBindingsAreDiscarded()
    {
        Assert.Empty(ToolShortcuts.Bindings("unknown=:84,text=oops,rect=super:15,arrow=:999999"));
        Assert.Empty(ToolShortcuts.Bindings(""));
        Assert.Empty(ToolShortcuts.Bindings(null));
        Assert.Empty(ToolShortcuts.Bindings("image=:84"));
        Assert.Empty(ToolShortcuts.Bindings("text"));
    }

    [Fact]
    public void TheLastValidBindingForADuplicateToolWins() =>
        Assert.Equal(KeyN, ToolShortcuts.Bindings("text=:84,text=:78")[ScreenshotTool.Text].VirtualKey);

    [Fact]
    public void DuplicateImportedShortcutsHaveOneDeterministicOwner() =>
        Assert.Equal(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Arrow] = Text },
                     ToolShortcuts.Bindings("text=:84,arrow=:84"));

    [Fact]
    public void DisplayTextNamesTheKeyAndItsModifiers()
    {
        Assert.Equal("T", Text.DisplayText());
        Assert.Equal("Ctrl+Alt+Win+K", PenKey.DisplayText());
        Assert.Equal("Shift+F5", new ToolShortcut(0x74, ShortcutModifiers.Shift).DisplayText());
    }

    // ---- Reserved keys ---------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> ReservedControlCombos()
    {
        int[] keys = [0x43, 0x53, 0x5A, 0x50, 0x30, 0x31, 0xBB, 0xBD, 0x08, 0x2E, 0x57, 0x51, 0x56, 0x59];
        ShortcutModifiers[] flags =
        [
            ShortcutModifiers.Control, ShortcutModifiers.Control | ShortcutModifiers.Shift,
            ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Win,
        ];
        foreach (var key in keys) foreach (var flag in flags) yield return [key, flag];
    }

    [Theory]
    [MemberData(nameof(ReservedControlCombos))]
    public void EditorAndMenuCommandsStayReservedOnRead(int key, ShortcutModifiers modifiers)
    {
        var reserved = new ToolShortcut(key, modifiers);
        Assert.True(ToolShortcuts.IsReservedEditorKey(reserved));
        Assert.Empty(ToolShortcuts.Bindings("text=" + reserved.StorageValue));
    }

    [Theory]
    [InlineData(0x1B)]
    [InlineData(0x0D)]
    [InlineData(0x08)]
    [InlineData(0x2E)]
    public void BareAndShiftedEditorActionsCannotBeRebound(int key)
    {
        Assert.True(ToolShortcuts.IsReservedEditorKey(new ToolShortcut(key)));
        Assert.True(ToolShortcuts.IsReservedEditorKey(new ToolShortcut(key, ShortcutModifiers.Shift)));
    }

    [Fact]
    public void CustomToolKeysAreAllowed()
    {
        Assert.False(ToolShortcuts.IsReservedEditorKey(Text));
        Assert.False(ToolShortcuts.IsReservedEditorKey(PenKey));
        Assert.False(ToolShortcuts.IsReservedEditorKey(new ToolShortcut(0x1B, ShortcutModifiers.Alt)));
        Assert.False(ToolShortcuts.IsReservedEditorKey(new ToolShortcut(0x41, ShortcutModifiers.Control)));
    }

    [Fact]
    public void ARejectedReservedBindingFallsBackToThePositionDigit() =>
        Assert.Equal(ScreenshotTool.Text, Resolve(new ToolShortcut(Key5), number: 5, bindings: "text=control:8"));

    // ---- Routing ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BareAndModifiedKeysSelectTheirTools()
    {
        Assert.Equal(ScreenshotTool.Text, Resolve(Text));
        Assert.Equal(ScreenshotTool.Freehand, Resolve(PenKey));
    }

    [Fact]
    public void DisablingShortcutsGatesCustomBindingsAndPositionDigits()
    {
        Assert.Null(Resolve(Text, enabled: false));
        Assert.Null(Resolve(One, number: 1, enabled: false));
    }

    [Fact]
    public void CustomBindingsRequireAnExactModifierMatch() =>
        Assert.Null(Resolve(new ToolShortcut(KeyT, ShortcutModifiers.Shift)));

    [Fact]
    public void AReboundToolNoLongerAnswersToItsOldDigit() =>
        Assert.Null(Resolve(new ToolShortcut(Key5), number: 5));

    [Fact]
    public void AnUnboundKeyWithNoDigitSelectsNothing() =>
        Assert.Null(Resolve(new ToolShortcut(0x4A)));

    [Fact]
    public void ADigitWithAPrimaryModifierIsNotAPositionShortcut() =>
        Assert.Null(ToolShortcuts.ShortcutTool(Key1, ShortcutModifiers.Control, 1, null, "", true));

    [Fact]
    public void ShiftPlusAPrintedDigitPreservesTheDigitPath() =>
        Assert.Equal(ScreenshotTool.Select, Resolve(new ToolShortcut(Key1, ShortcutModifiers.Shift), number: 1, bindings: ""));

    [Fact]
    public void EmptyBindingsPreservePositionBehaviourForEveryTool()
    {
        foreach (var (tool, index) in ScreenshotTools.EditorTools.Select((t, i) => (t, i)))
        {
            Assert.Equal(ScreenshotTools.ShortcutTool(index + 1, null, true),
                         ToolShortcuts.ShortcutTool(-1, None, index + 1, null, "", true));
            Assert.Equal(index < ScreenshotTools.ShortcutLimit,
                         ToolShortcuts.ShortcutLabel(tool, null, "", true) is not null);
        }
    }

    [Fact]
    public void ABoundToolsBadgeShowsItsCapsAndDisablingHidesEveryBadge()
    {
        Assert.Equal(Text.DisplayText(), ToolShortcuts.ShortcutLabel(ScreenshotTool.Text, null, Raw, true));
        Assert.Null(ToolShortcuts.ShortcutLabel(ScreenshotTool.Text, null, Raw, false));
        Assert.Equal("1", ToolShortcuts.ShortcutLabel(ScreenshotTool.Select, null, Raw, true));
    }

    // ---- Binding rejection -----------------------------------------------------------------------------------------

    private static BindingRejection? Rejection(ToolShortcut key, ScreenshotTool excluding = ScreenshotTool.Arrow,
                                               string? role = null, string? layout = null, bool system = false) =>
        ToolShortcuts.Rejection(key, excluding, Raw, _ => role, _ => layout, _ => system);

    [Fact]
    public void AKeyBoundToAnotherToolIsRejectedAndAToolsOwnKeyIsNot()
    {
        Assert.Equal(BindingRejection.ForTool(ScreenshotTool.Text), Rejection(Text));
        Assert.Null(Rejection(Text, excluding: ScreenshotTool.Text));
        Assert.Null(Rejection(PenKey, excluding: ScreenshotTool.Freehand));
    }

    [Fact]
    public void KeysOwnedByAFeatureAWindowLayoutOrTheSystemAreRejectedBeforeSaving()
    {
        Assert.Equal(BindingRejection.ForRole("keepAwake"), Rejection(PenKey, ScreenshotTool.Freehand, role: "keepAwake"));
        Assert.Equal(BindingRejection.ForWindowLayout("Left"), Rejection(PenKey, ScreenshotTool.Freehand, layout: "Left"));
        Assert.Equal(BindingRejection.System, Rejection(PenKey, ScreenshotTool.Freehand, system: true));
    }

    [Fact]
    public void EditorKeysAndToolsAnswerFirstAndTheSystemTableIsReadLast()
    {
        Assert.Equal(BindingRejection.Reserved, Rejection(new ToolShortcut(0x1B), role: "keepAwake"));
        Assert.Equal(BindingRejection.ForTool(ScreenshotTool.Text), Rejection(Text, role: "keepAwake", system: true));
        Assert.Equal(BindingRejection.ForRole("keepAwake"),
                     Rejection(PenKey, ScreenshotTool.Freehand, role: "keepAwake", layout: "Left"));
        Assert.Equal(BindingRejection.ForWindowLayout("Left"),
                     Rejection(PenKey, ScreenshotTool.Freehand, layout: "Left", system: true));
    }

    [Fact]
    public void TheSystemTableIsNotReadWhenAnEarlierCheckAlreadyRejected()
    {
        bool asked = false;
        ToolShortcuts.Rejection(Text, ScreenshotTool.Arrow, Raw, _ => null, _ => null, _ => { asked = true; return false; });
        Assert.False(asked);
    }

    // ---- Assigning ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RecordingADigitMovesTheToolAndClearsItsCustomBinding()
    {
        var moved = ToolShortcuts.AssigningBinding(One, 1, ScreenshotTool.Text, null, Raw);
        Assert.Equal(ScreenshotTool.Text, ScreenshotTools.Ordered(moved.OrderRaw).First());
        Assert.Equal(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Freehand] = PenKey },
                     ToolShortcuts.Bindings(moved.BindingsRaw));
    }

    [Fact]
    public void ClearingMovesTheToolBelowTheNumberedSlotsAndDropsItsBadge()
    {
        var cleared = ToolShortcuts.AssigningBinding(null, null, ScreenshotTool.Text, null, Raw);
        Assert.Equal(ScreenshotTools.ShortcutLimit, ScreenshotTools.Ordered(cleared.OrderRaw).IndexOf(ScreenshotTool.Text));
        Assert.Null(ToolShortcuts.ShortcutLabel(ScreenshotTool.Text, cleared.OrderRaw, cleared.BindingsRaw, true));
    }

    [Fact]
    public void RecordingACustomKeyPreservesTheRailOrder()
    {
        var rebound = ToolShortcuts.AssigningBinding(Text, null, ScreenshotTool.Text, "crop,text", "");
        Assert.Equal([ScreenshotTool.Crop, ScreenshotTool.Text], ScreenshotTools.Ordered(rebound.OrderRaw).Take(2));
        Assert.Equal(Text, ToolShortcuts.Bindings(rebound.BindingsRaw)[ScreenshotTool.Text]);
    }

    [Fact]
    public void ReplacingABindingKeepsOnlyTheNewOne()
    {
        var first = ToolShortcuts.AssigningBinding(Text, null, ScreenshotTool.Text, null, "");
        var second = ToolShortcuts.AssigningBinding(new ToolShortcut(KeyN), null, ScreenshotTool.Text, first.OrderRaw, first.BindingsRaw);
        Assert.Equal(KeyN, ToolShortcuts.Bindings(second.BindingsRaw)[ScreenshotTool.Text].VirtualKey);
    }

    [Fact]
    public void BindingsStayAttachedToTheirToolsThroughReorders()
    {
        var moved = ToolShortcuts.AssigningBinding(null, 1, ScreenshotTool.Crop, null, Raw);
        Assert.Equal(ScreenshotTool.Crop, ScreenshotTools.Ordered(moved.OrderRaw).First());
        Assert.Equal(2, ToolShortcuts.Bindings(moved.BindingsRaw).Count);
    }

    private static object? Registered(string key) => Defaults.Registered.First(p => p.Key == key).Value;

    [Fact]
    public void TheRegisteredDefaultsMeanTheCanonicalRailAndNoCustomKeys()
    {
        // Upstream registers Tool.defaultOrderStorage; the Windows registration is "", which Ordered() reads as the same order.
        Assert.Equal(ScreenshotTools.EditorTools, ScreenshotTools.Ordered((string?)Registered(DefaultsKey.ScreenshotToolOrder)));
        Assert.Equal("", Registered(DefaultsKey.ScreenshotToolShortcuts));
        Assert.Equal(true, Registered(DefaultsKey.ScreenshotToolShortcutsEnabled));
        Assert.Empty(ToolShortcuts.Bindings((string?)Registered(DefaultsKey.ScreenshotToolShortcuts)));
    }

    [Fact]
    public void TheRegisteredRememberedChoicesStartAGoodModel()
    {
        Assert.Equal(BlurStrength.DefaultLevel, Registered(DefaultsKey.ScreenshotLastBlurLevel));
        Assert.Equal(AnnotationSizes.DefaultTextSize, Registered(DefaultsKey.ScreenshotLastTextSize));
        Assert.Equal(AnnotationSizes.DefaultSpotlightDim, Registered(DefaultsKey.ScreenshotSpotlightDim));
        Assert.Equal("pixelate", Registered(DefaultsKey.ScreenshotLastBlurStyle));
        Assert.Equal("filled", Registered(DefaultsKey.ScreenshotLastArrowStyle));
        Assert.Equal("check", Registered(DefaultsKey.ScreenshotLastSticker));
        Assert.Equal("red", Registered(DefaultsKey.ScreenshotLastColor));
        Assert.Equal("medium", Registered(DefaultsKey.ScreenshotLastStroke));
        Assert.Equal(ScreenshotTool.Arrow, ScreenshotTools.StartingTool((string?)Registered(DefaultsKey.ScreenshotLastTool)));
    }

    // ---- Which keys name a rail slot (the layout oracle, with fake layouts) -----------------------------------------

    [Fact]
    public void OnUsABareOrNumpadDigitNamesASlotAndShiftOneTypesABang()
    {
        Assert.Equal(1, ToolShortcuts.ShortcutDigit(One));
        Assert.Equal(1, ToolShortcuts.ShortcutDigit(new ToolShortcut(0x61)));
        Assert.Equal(9, ToolShortcuts.ShortcutDigit(new ToolShortcut(0x39)));
        Assert.Null(ToolShortcuts.ShortcutDigit(new ToolShortcut(Key1, ShortcutModifiers.Shift)));
        Assert.Null(ToolShortcuts.ShortcutDigit(new ToolShortcut(Key1, ShortcutModifiers.Control)));
        Assert.Null(ToolShortcuts.ShortcutDigit(new ToolShortcut(Key0)));
        Assert.Null(ToolShortcuts.ShortcutDigit(Text));
    }

    [Fact]
    public void ASavedBindingOnADigitKeyIsSuspendedSoTheDigitKeepsItsSlot()
    {
        var raw = "text=" + One.StorageValue;
        Assert.Equal(One, ToolShortcuts.Bindings(raw)[ScreenshotTool.Text]);
        Assert.Empty(ToolShortcuts.ActiveBindings(raw));
        Assert.Equal(ScreenshotTool.Select, Resolve(One, number: 1, bindings: raw));
        var shifted = new ToolShortcut(Key1, ShortcutModifiers.Shift);
        Assert.Equal(shifted, ToolShortcuts.Bindings(ToolShortcuts.BindingsStorage(
            new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = shifted }))[ScreenshotTool.Text]);
    }

    [Fact]
    public void AzertyReadsOneFromShiftOnTheAmpersandKeyAndLeavesBareAmpersandAKey()
    {
        var french = new AzertyLayout();
        var shiftedOne = new ToolShortcut(Key1, ShortcutModifiers.Shift);
        Assert.Equal("&", french.TypedLabel(Key1, false, false));
        Assert.Equal("1", french.TypedLabel(Key1, true, false));
        Assert.Equal(1, ToolShortcuts.ShortcutDigit(shiftedOne, layout: french));
        Assert.Null(ToolShortcuts.ShortcutDigit(One, layout: french));
        var ampersand = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = One });
        Assert.Equal(One, ToolShortcuts.Bindings(ampersand)[ScreenshotTool.Text]);
        Assert.Empty(ToolShortcuts.ActiveBindings(ToolShortcuts.BindingsStorage(
            new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = shiftedOne }), layout: french));
        Assert.Null(ToolShortcuts.Rejection(One, ScreenshotTool.Text, "", _ => null, _ => null, _ => false));
    }

    [Fact]
    public void AzertyShiftAmpersandMovesTheToolIntoSlotOneInsteadOfSavingAKey()
    {
        var french = new AzertyLayout();
        var shiftedOne = new ToolShortcut(Key1, ShortcutModifiers.Shift);
        var ampersand = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = One });
        var moved = ToolShortcuts.AssigningBinding(shiftedOne, ToolShortcuts.ShortcutDigit(shiftedOne, layout: french),
                                                   ScreenshotTool.Text, null, ampersand);
        Assert.Equal(ScreenshotTool.Text, ScreenshotTools.Ordered(moved.OrderRaw).First());
        Assert.Empty(ToolShortcuts.Bindings(moved.BindingsRaw));
        Assert.Equal(ScreenshotTool.Text,
                     ToolShortcuts.ShortcutTool(shiftedOne.VirtualKey, shiftedOne.Modifiers, 1, moved.OrderRaw,
                                                moved.BindingsRaw, true, layout: french));
    }

    [Fact]
    public void FrenchNumericalCapsLockChangesRecordingFromASymbolToADigit()
    {
        var numerical = new AzertyLayout(numerical: true);
        Assert.Equal(1, ToolShortcuts.ShortcutDigit(One, capsLockOn: true, layout: numerical));
        Assert.Null(ToolShortcuts.ShortcutDigit(One, capsLockOn: false, layout: numerical));
        var original = ToolShortcuts.AssigningBinding(One, null, ScreenshotTool.Text, null, "");
        var locked = ToolShortcuts.AssigningBinding(One, ToolShortcuts.ShortcutDigit(One, true, numerical),
                                                    ScreenshotTool.Text, original.OrderRaw, original.BindingsRaw);
        Assert.Equal(ScreenshotTool.Text, ScreenshotTools.Ordered(locked.OrderRaw).First());
        Assert.Empty(ToolShortcuts.Bindings(locked.BindingsRaw));
        Assert.Equal(ScreenshotTool.Text,
                     ToolShortcuts.ShortcutTool(One.VirtualKey, None, 1, locked.OrderRaw, locked.BindingsRaw, true,
                                                capsLockOn: true, layout: numerical));
    }

    [Fact]
    public void CapsLockTemporarilyChangesAvailabilityAndLabelsNeverStorage()
    {
        var numerical = new AzertyLayout(numerical: true);
        var original = ToolShortcuts.AssigningBinding(One, null, ScreenshotTool.Text, null, "");
        Assert.Empty(ToolShortcuts.ActiveBindings(original.BindingsRaw, capsLockOn: true, layout: numerical));
        Assert.Equal("5", ToolShortcuts.ShortcutLabel(ScreenshotTool.Text, null, original.BindingsRaw, true, true, numerical));
        Assert.Equal(One, ToolShortcuts.ActiveBindings(original.BindingsRaw, capsLockOn: false, layout: numerical)[ScreenshotTool.Text]);
        Assert.Null(ToolShortcuts.ShortcutTool(One.VirtualKey, None, 1, null, original.BindingsRaw, false, true, numerical));
    }

    [Fact]
    public void KeyboardChangesSuspendDigitShapedBindingsWithoutDeletingThem()
    {
        var french = new AzertyLayout();
        var original = ToolShortcuts.AssigningBinding(One, null, ScreenshotTool.Text, null, "");
        Assert.Equal(One, ToolShortcuts.ActiveBindings(original.BindingsRaw, layout: french)[ScreenshotTool.Text]);
        // Back on a US layout the same saved key types a digit: it is suspended, not erased.
        Assert.Equal(One, ToolShortcuts.Bindings(original.BindingsRaw)[ScreenshotTool.Text]);
        Assert.Empty(ToolShortcuts.ActiveBindings(original.BindingsRaw));
        Assert.Equal("5", ToolShortcuts.ShortcutLabel(ScreenshotTool.Text, null, original.BindingsRaw, true));
        Assert.Equal(ScreenshotTool.Select,
                     ToolShortcuts.ShortcutTool(One.VirtualKey, None, 1, null, original.BindingsRaw, true));
    }

    [Fact]
    public void EditingAndClearingAnotherToolPreserveBindingsAcrossAKeyboardRoundTrip()
    {
        var french = new AzertyLayout();
        var original = ToolShortcuts.AssigningBinding(One, null, ScreenshotTool.Text, null, "");
        var edited = ToolShortcuts.AssigningBinding(PenKey, null, ScreenshotTool.Arrow, original.OrderRaw, original.BindingsRaw);
        var cleared = ToolShortcuts.AssigningBinding(null, null, ScreenshotTool.Arrow, edited.OrderRaw, edited.BindingsRaw);
        Assert.Equal(One, ToolShortcuts.ActiveBindings(edited.BindingsRaw, layout: french)[ScreenshotTool.Text]);
        Assert.Equal(One, ToolShortcuts.ActiveBindings(cleared.BindingsRaw, layout: french)[ScreenshotTool.Text]);
    }

    [Fact]
    public void AnInputMethodsPrintedDigitWinsOverAnUnderlyingPhysicalLetter()
    {
        var raw = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = Text });
        Assert.Equal(ScreenshotTool.Select, ToolShortcuts.ShortcutTool(KeyT, None, 1, null, raw, true));
    }

    [Fact]
    public void AnActiveCustomBindingKeepsItsToolsOldPositionShortcutOff()
    {
        var raw = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = Text });
        Assert.Null(ToolShortcuts.ShortcutTool(Key5, None, 5, null, raw, true));
    }

    [Fact]
    public void CustomToolKeysRouteAndLabelForOtherLayouts()
    {
        var layout = new LetterLayout();
        var raw = ToolShortcuts.BindingsStorage(new Dictionary<ScreenshotTool, ToolShortcut> { [ScreenshotTool.Text] = new ToolShortcut(0x51) });
        Assert.Equal(ScreenshotTool.Text, ToolShortcuts.ShortcutTool(0x51, None, null, null, raw, true, layout: layout));
        Assert.Equal("A", ToolShortcuts.ShortcutLabel(ScreenshotTool.Text, null, raw, true, layout: layout));
    }

    /// <summary>A layout where the physical Q key types A, as AZERTY does.</summary>
    private sealed class LetterLayout : IKeyboardLayout
    {
        public string? TypedLabel(int virtualKey, bool shift, bool capsLock) => virtualKey == 0x51 ? "a" : null;
    }

    [Fact]
    public void TheUsLayoutTypesTheExpectedCharacters()
    {
        var us = UsKeyboardLayout.Instance;
        Assert.Equal("1", us.TypedLabel(0x31, false, false));
        Assert.Equal("!", us.TypedLabel(0x31, true, false));
        Assert.Equal(")", us.TypedLabel(0x30, true, false));
        Assert.Equal("5", us.TypedLabel(0x65, false, false));
        Assert.Null(us.TypedLabel(0x65, true, false));
        Assert.Equal("a", us.TypedLabel(0x41, false, false));
        Assert.Equal("A", us.TypedLabel(0x41, true, false));
        Assert.Equal("A", us.TypedLabel(0x41, false, true));
        Assert.Null(us.TypedLabel(0x70, false, false));
    }
}
