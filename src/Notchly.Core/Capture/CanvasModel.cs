// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure behaviour of upstream ScreenshotEditorModel (Services/QuickTools/ScreenshotEditorController.swift) and
// of the editor view's pure rules (UI/Screenshot/ScreenshotEditorView.swift): drags (move, resize through eight handles,
// draw a new shape), crop with pixel snapping, canvas expansion, pasted image layers, selection styling, undo/redo history
// and dirty tracking, hover cursor and which style controls show.
//
// The model holds NO bitmaps. The base image is an opaque ImageAsset (identity, pixel size, byte cost, host payload).
// Anything that needs pixels goes through ICanvasHost: producing the cropped or expanded picture, measuring text, making
// blur samples, kicking off text and QR recognition. The default host is pure, so the whole model is unit-testable.
//
// Coordinates: image points, top-left origin, y down, the same as upstream's flipped editor canvas (see RectMath).
// Remembered values (last tool, colour, ...) are not written here: the model raises PreferenceChanged with the
// DefaultsKey constant and the host stores it.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Capture;

/// <summary>The pixel-side work the model delegates. Every member has a pure default so tests need no bitmaps.</summary>
public interface ICanvasHost
{
    /// <summary>The picture after growing to <paramref name="rect"/> (relative to the old canvas, covering it). Null on failure.</summary>
    ImageAsset? ExpandImage(ImageAsset image, RectD rect, CanvasFill fill) =>
        new((int)rect.Width, (int)rect.Height);

    /// <summary>The picture holding only <paramref name="rect"/> of <paramref name="image"/>, with pixels of its own. Null on failure.</summary>
    ImageAsset? CropImage(ImageAsset image, RectD rect) => new((int)rect.Width, (int)rect.Height);

    /// <summary>The box of a text annotation's text at the given size; the default is a rough estimate, the editor should measure for real.</summary>
    SizeD MeasureText(string text, int textSize, double scale)
    {
        double font = textSize * scale;
        return new SizeD(Math.Ceiling((text.Length == 0 ? 1 : text.Length) * font * 0.6) + 4, Math.Ceiling(font * 1.2));
    }

    /// <summary>Makes the sample a blur area of this style and level paints from, and says whether it is there. Erasing needs none.</summary>
    bool EnsureBlurSample(ImageAsset baseImage, BlurStyle style, int level) => true;

    /// <summary>Keeps a sample for each level in use and drops the rest.</summary>
    void SyncBlurSamples(ImageAsset baseImage, IReadOnlySet<int> mosaicLevels, IReadOnlySet<int> softBlurLevels) { }

    /// <summary>The base image changed: every sample belongs to the old one.</summary>
    void ResetBlurSamples() { }

    /// <summary>Start text recognition of the current base image; answer through <see cref="CanvasModel.ApplyRecognition"/>.</summary>
    void RecognizeText() { }

    void RecognizeQrCodes() { }

    void Beep() { }
}

public sealed class DefaultCanvasHost : ICanvasHost
{
    public static readonly DefaultCanvasHost Instance = new();
}

/// <summary>The remembered editor values a new model starts from (read from the DefaultsKey.ScreenshotLast* settings).</summary>
public sealed record CanvasPreferences(
    string? LastTool = null, string? LastColor = null, string? LastStroke = null, int LastTextSize = 0,
    int LastBlurLevel = 0, string? LastBlurStyle = null, bool LastBlurTextOnly = false,
    double SpotlightDim = AnnotationSizes.DefaultSpotlightDim, string? LastArrowStyle = null, string? LastSticker = null,
    bool AnnotationShadows = false);

/// <summary>What the pointer should look like over the canvas.</summary>
public enum CanvasCursor { Arrow, Crosshair, OpenHand, IBeam }

/// <summary>One undo or redo step: the base picture and the annotations to return to.</summary>
public sealed record HistoryState(ImageAsset Image, IReadOnlyList<Annotation> Annotations);

public sealed class CanvasModel : INotifyPropertyChanged
{
    public const int UndoLimit = 60;

    /// <summary>A mark costs far less than a frame, but a long session adds up and should not be free. An estimate, not a measurement.</summary>
    public const int UndoAnnotationBytes = 512;

    /// <summary>Undo also stops by memory, so a few full captures on a 5K or 6K display cannot pile up into gigabytes of history.</summary>
    public long UndoByteBudget { get; set; } = 256L << 20;

    public static readonly (double Min, double Max) ZoomRange = (0.05, 3);

    private readonly ICanvasHost host;
    private List<Annotation> annotations = [];
    private readonly List<HistoryState> undoStack = [];
    private readonly List<HistoryState> redoStack = [];
    private Dictionary<ImageAsset, List<RectD>> carriedRuns = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A remembered value changed: the DefaultsKey constant and the new value (string, int, double or bool) to store.</summary>
    public event Action<string, object>? PreferenceChanged;

    public CanvasModel(ImageAsset image, double scale, CanvasPreferences? preferences = null, ICanvasHost? host = null)
    {
        BaseImage = image;
        Scale = scale;
        this.host = host ?? DefaultCanvasHost.Instance;
        var prefs = preferences ?? new CanvasPreferences();
        tool = ScreenshotTools.StartingTool(prefs.LastTool);
        color = AnnotationColors.Sanitized(prefs.LastColor);
        stroke = StrokeWidths.Sanitized(prefs.LastStroke);
        textSize = AnnotationSizes.SanitizedTextSize(prefs.LastTextSize);
        blurLevel = BlurStrength.StartingLevel(prefs.LastBlurLevel);
        blurStyle = BlurStyles.Sanitized(prefs.LastBlurStyle);
        blurTextOnly = prefs.LastBlurTextOnly;
        spotlightDimStrength = AnnotationSizes.SanitizedSpotlightDim(prefs.SpotlightDim);
        arrowStyle = ArrowStyles.Sanitized(prefs.LastArrowStyle);
        sticker = Stickers.Sanitized(prefs.LastSticker);
        annotationShadowsEnabled = prefs.AnnotationShadows;
        RecordCleanState();
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Remember(string key, object value) => PreferenceChanged?.Invoke(key, value);

    // ---- Document --------------------------------------------------------------------------------------------------

    public ImageAsset BaseImage { get; private set; }

    /// <summary>Pixels per point of the capture (2 on a Retina-style display).</summary>
    public double Scale { get; }

    public SizeD ImageSize => BaseImage.PixelSize;

    /// <summary>Natural on-screen size in points (pixels over capture scale).</summary>
    public SizeD PointSize => new(BaseImage.PixelWidth / Scale, BaseImage.PixelHeight / Scale);

    public IReadOnlyList<Annotation> Annotations
    {
        get => annotations;
        set
        {
            annotations = value.ToList();
            Notify();
        }
    }

    public Annotation? SelectedAnnotation => annotations.FirstOrDefault(a => a.Id == selectedId);

    private Guid? selectedId;

    public Guid? SelectedId
    {
        get => selectedId;
        set
        {
            selectedId = value;
            Notify();
        }
    }

    private Guid? editingTextId;

    public Guid? EditingTextId
    {
        get => editingTextId;
        set
        {
            editingTextId = value;
            Notify();
        }
    }

    /// <summary>The shape a drag is drawing, until it lands.</summary>
    public Guid? DraftId { get; private set; }

    public bool CanUndo { get; private set; }
    public bool CanRedo { get; private set; }

    /// <summary>True while there is work that never left the app: closing then asks.</summary>
    public bool IsDirty { get; private set; }

    public IReadOnlyList<HistoryState> UndoStates => undoStack;
    public IReadOnlyList<HistoryState> RedoStates => redoStack;

    /// <summary>
    /// Fingerprints of the backdrop and watermark styles (the host supplies a sanitised encoding). Changing one counts
    /// as an edit for the dirty state, as upstream compares the styles.
    /// </summary>
    private string backdropKey = "", watermarkKey = "";

    public string BackdropKey
    {
        get => backdropKey;
        set { backdropKey = value; RefreshDirtyState(); Notify(); }
    }

    public string WatermarkKey
    {
        get => watermarkKey;
        set { watermarkKey = value; RefreshDirtyState(); Notify(); }
    }

    // ---- Tool and style state --------------------------------------------------------------------------------------

    private ScreenshotTool tool;

    public ScreenshotTool Tool
    {
        get => tool;
        set
        {
            var oldValue = tool;
            tool = value;
            Remember(DefaultsKey.ScreenshotLastTool, tool.Raw());
            if (oldValue == ScreenshotTool.ExpandCanvas && tool != ScreenshotTool.ExpandCanvas) CancelCanvasExpansion();
            if (tool != ScreenshotTool.Select) ClearTextSelection();
            if (tool != oldValue && tool != ScreenshotTool.Select)
            {
                SelectedId = null;
                EditingTextId = null;
            }
            if (tool == ScreenshotTool.Crop && oldValue != ScreenshotTool.Crop)
            {
                SelectedId = null;
                CropDraft = RectMath.FromSize(ImageSize);
            }
            else if (oldValue == ScreenshotTool.Crop)
            {
                CropDraft = null;
                CropLoupePoint = null;
            }
            Notify();
        }
    }

    private AnnotationColor color;

    public AnnotationColor Color
    {
        get => color;
        set { color = value; Remember(DefaultsKey.ScreenshotLastColor, color.Raw()); ApplyStyleToSelection(); Notify(); }
    }

    private StrokeWidth stroke;

    public StrokeWidth Stroke
    {
        get => stroke;
        set { stroke = value; Remember(DefaultsKey.ScreenshotLastStroke, stroke.Raw()); ApplyStyleToSelection(); Notify(); }
    }

    private int textSize;

    public int TextSize
    {
        get => textSize;
        set { textSize = value; Remember(DefaultsKey.ScreenshotLastTextSize, textSize); ApplyStyleToSelection(); Notify(); }
    }

    private int blurLevel;

    public int BlurLevel
    {
        get => blurLevel;
        set { blurLevel = value; Remember(DefaultsKey.ScreenshotLastBlurLevel, blurLevel); ApplyBlurToSelection(); Notify(); }
    }

    private BlurStyle blurStyle;

    public BlurStyle BlurStyle
    {
        get => blurStyle;
        set { blurStyle = value; Remember(DefaultsKey.ScreenshotLastBlurStyle, blurStyle.Raw()); ApplyBlurToSelection(); Notify(); }
    }

    private bool blurTextOnly;

    public bool BlurTextOnly
    {
        get => blurTextOnly;
        set { blurTextOnly = value; Remember(DefaultsKey.ScreenshotLastBlurTextOnly, blurTextOnly); ApplyBlurToSelection(); Notify(); }
    }

    private ArrowStyle arrowStyle;

    public ArrowStyle ArrowStyle
    {
        get => arrowStyle;
        set { arrowStyle = value; Remember(DefaultsKey.ScreenshotLastArrowStyle, arrowStyle.Raw()); ApplyStyleToSelection(); Notify(); }
    }

    private StickerKind sticker;

    public StickerKind Sticker
    {
        get => sticker;
        set { sticker = value; Remember(DefaultsKey.ScreenshotLastSticker, sticker.Raw()); ApplyStickerToSelection(); Notify(); }
    }

    private bool annotationShadowsEnabled;

    public bool AnnotationShadowsEnabled
    {
        get => annotationShadowsEnabled;
        set { annotationShadowsEnabled = value; Remember(DefaultsKey.ScreenshotAnnotationShadows, value); RefreshDirtyState(); Notify(); }
    }

    private double spotlightDimStrength;

    /// <summary>How dark a spotlight turns everything outside its regions, remembered between captures like the blur strength.</summary>
    public double SpotlightDimStrength
    {
        get => spotlightDimStrength;
        set { spotlightDimStrength = value; Remember(DefaultsKey.ScreenshotSpotlightDim, value); RefreshDirtyState(); Notify(); }
    }

    /// <summary>The binding of the dim slider: settles on the nearest 5% step.</summary>
    public void SetSpotlightDimStepped(double value) => SpotlightDimStrength = AnnotationSizes.SteppedSpotlightDim(value);

    // ---- Zoom ------------------------------------------------------------------------------------------------------

    /// <summary>Continuous zoom (view points per image pixel); null fits the window.</summary>
    public double? ZoomOverride { get; set; }

    /// <summary>What the view actually laid out last, so pinch and scroll zoom start from the visible scale even in fit mode.</summary>
    public double CurrentDisplayZoom { get; set; } = 0.5;

    public void AdjustZoom(double factor)
    {
        double current = ZoomOverride ?? CurrentDisplayZoom;
        ZoomOverride = Math.Min(Math.Max(current * factor, ZoomRange.Min), ZoomRange.Max);
        Notify(nameof(ZoomOverride));
    }

    public void SetZoom(double zoom)
    {
        ZoomOverride = Math.Min(Math.Max(zoom, ZoomRange.Min), ZoomRange.Max);
        Notify(nameof(ZoomOverride));
    }

    // ---- Recognized text -------------------------------------------------------------------------------------------

    /// <summary>Words recognized in the capture and selectable with the select tool.</summary>
    public IReadOnlyList<RecognizedWord> TextWords { get; private set; } = [];

    /// <summary>The recognized words joined into the runs text only blur areas cover, or null until recognition has read all of the current capture.</summary>
    public IReadOnlyList<RectD>? TextRuns { get; private set; }

    public IReadOnlyList<int> SelectedWordIndexes { get; private set; } = [];
    private PointD? textSelectionAnchor;

    public string SelectedText => AnnotationGeometry.JoinedWords(TextWords, SelectedWordIndexes);

    public void ClearTextSelection()
    {
        textSelectionAnchor = null;
        if (SelectedWordIndexes.Count > 0)
        {
            SelectedWordIndexes = [];
            Notify(nameof(SelectedWordIndexes));
        }
    }

    public int? WordIndexAt(PointD point)
    {
        for (int i = 0; i < TextWords.Count; i++)
        {
            if (TextWords[i].Rect.InsetBy(-2 * Scale, -2 * Scale).ContainsPoint(point)) return i;
        }
        return null;
    }

    /// <summary>
    /// The host finished recognizing <paramref name="forImage"/>: publish its words and runs (plus the runs a crop carried
    /// over), unless the editor has moved on to another picture. Returns whether they were taken.
    /// </summary>
    public bool ApplyRecognition(ImageAsset forImage, IReadOnlyList<RecognizedWord> words, IReadOnlyList<RectD>? runs)
    {
        if (!ReferenceEquals(forImage, BaseImage)) return false;
        TextWords = words;
        TextRuns = runs?.Concat(carriedRuns.TryGetValue(forImage, out var carried) ? carried : []).ToList();
        Notify(nameof(TextWords));
        Notify(nameof(TextRuns));
        return true;
    }

    // ---- Crop and expansion state ----------------------------------------------------------------------------------

    private RectD? cropDraft;

    public RectD? CropDraft
    {
        get => cropDraft;
        set { cropDraft = value; Notify(); }
    }

    /// <summary>Exact image pixel under a crop resize grip. Null while moving the whole crop so the loupe appears only when it adds precision.</summary>
    public PointD? CropLoupePoint { get; private set; }

    public CanvasFill ExpansionFill { get; set; } = CanvasFill.White;
    public RectD? ExpansionDraft { get; private set; }
    public bool ExpansionRejected { get; private set; }
    private SelectionHandle? expansionHandle;
    private PointD? expansionStartPoint;

    // ---- Gesture state, in image points ----------------------------------------------------------------------------

    private PointD dragStart;
    private RectD moveOrigin;
    private IReadOnlyList<PointD> movePoints = [];
    private SelectionHandle? activeHandle;
    private RectD? cropResizeOrigin, cropMoveOrigin, cropSelectionOrigin;
    private bool dragRegistered;
    private bool editingSelectedAnnotation;
    private Guid? newTextId;

    internal bool EditingSelectedAnnotation => editingSelectedAnnotation;
    internal SelectionHandle? ActiveHandle => activeHandle;
    internal PointD DragStart { get => dragStart; set => dragStart = value; }
    internal RectD MoveOrigin { get => moveOrigin; set => moveOrigin = value; }
    internal Guid? NewTextId => newTextId;

    // ---- Pasting and expanding -------------------------------------------------------------------------------------

    /// <summary>Adds a pasted picture as a movable image layer, centred on whole pixels. False when its size is not a valid canvas.</summary>
    public bool PasteImage(ImageAsset image)
    {
        if (!CanvasSupport.ValidSize(image.PixelSize)) return false;
        var annotation = new Annotation(ScreenshotTool.Image,
            rect: CanvasSupport.FittedImageRect(image.PixelSize, ImageSize), imageAsset: image);
        RegisterUndo();
        ClearTextSelection();
        Tool = ScreenshotTool.Select;
        annotations.Add(annotation);
        SelectedId = annotation.Id;
        Notify(nameof(Annotations));
        return true;
    }

    /// <summary>
    /// Grows the canvas to <paramref name="rect"/> (relative to the old canvas, covering it). Existing marks move with the
    /// content; counters and blur areas remember the size of the canvas they were drawn on.
    /// </summary>
    public bool CommitCanvasExpansion(RectD rect)
    {
        var oldSize = ImageSize;
        var oldBounds = RectMath.FromSize(oldSize);
        if (rect == oldBounds || !CanvasSupport.IsExpansion(rect, oldSize)) return false;
        if (host.ExpandImage(BaseImage, rect, ExpansionFill) is not { } expanded) return false;
        RegisterUndo();
        var moved = new List<Annotation>();
        foreach (var annotation in annotations)
        {
            var mark = annotation;
            // A spotlight region lights only its part on the image. Past the old edge it would land in the new margin and
            // light it, or start dimming the canvas while lighting nothing of the content.
            if (mark.Tool == ScreenshotTool.Spotlight)
            {
                if (mark.Rect.Standardized().Intersection(oldBounds) is not { } lit) continue;
                mark = mark with { Rect = lit };
            }
            mark = mark with
            {
                Rect = mark.Rect.OffsetBy(-rect.MinX(), -rect.MinY()),
                Points = mark.Points.Select(p => new PointD(p.X - rect.MinX(), p.Y - rect.MinY())).ToArray(),
            };
            if (mark.Tool is ScreenshotTool.Counter or ScreenshotTool.Pixelate)
                mark = mark with { RenderReferenceSize = mark.RenderReferenceSize ?? oldSize };
            moved.Add(mark);
        }
        annotations = moved;
        var previousRuns = TextRuns;
        BaseImage = expanded;
        if (previousRuns is not null)
            carriedRuns[expanded] = previousRuns.Select(r => r.OffsetBy(-rect.MinX(), -rect.MinY())).ToList();
        host.ResetBlurSamples();
        TextRuns = null;
        TextWords = [];
        ClearTextSelection();
        SelectedId = null;
        EditingTextId = null;
        CropDraft = null;
        PruneCarriedRuns();
        EnsureBlurSamplesForAnnotations();
        host.RecognizeText();
        host.RecognizeQrCodes();
        Notify(nameof(BaseImage));
        Notify(nameof(Annotations));
        return true;
    }

    public void CancelCanvasExpansion()
    {
        ExpansionDraft = null;
        expansionHandle = null;
        expansionStartPoint = null;
        ExpansionRejected = false;
        Notify(nameof(ExpansionDraft));
    }

    /// <summary>
    /// Escape backs out of Expand canvas the way it backs out of crop: a drag in progress is dropped first, then the tool
    /// itself is left, so the editor only closes once there is nothing left to leave.
    /// </summary>
    public void LeaveCanvasExpansion()
    {
        if (ExpansionDraft is not null || ExpansionRejected) CancelCanvasExpansion();
        else Tool = ScreenshotTool.Select;
    }

    // ---- Undo ------------------------------------------------------------------------------------------------------

    private void RegisterUndo(Guid? excluding = null)
    {
        var previous = excluding is { } id ? annotations.Where(a => a.Id != id).ToList() : annotations.ToList();
        undoStack.Add(new HistoryState(BaseImage, previous));
        redoStack.Clear();
        TrimHistory(afterUndo: false);
        RefreshUndoFlags();
        IsDirty = true;
        Notify(nameof(IsDirty));
    }

    /// <summary>
    /// Both directions share one budget (60 steps and 256 MiB of pixels, 512 B per annotation). Drop the most distant step
    /// first, preserving the immediate reverse operation even for an oversized image. States sharing one picture are
    /// charged once.
    /// </summary>
    private void TrimHistory(bool afterUndo)
    {
        var references = new Dictionary<ImageAsset, int>();
        long bytes = 0;

        void CountImage(ImageAsset image, int delta)
        {
            references.TryGetValue(image, out int previous);
            int next = previous + delta;
            references[image] = next;
            if (previous == 0) bytes += image.ByteCount;
            if (next == 0) bytes -= image.ByteCount;
        }

        void CountState(HistoryState state, int delta)
        {
            CountImage(state.Image, delta);
            bytes += (long)delta * state.Annotations.Count * UndoAnnotationBytes;
            foreach (var annotation in state.Annotations)
            {
                if (annotation.ImageAsset is { } image) CountImage(image, delta);
            }
        }

        foreach (var state in undoStack) CountState(state, 1);
        foreach (var state in redoStack) CountState(state, 1);
        while (undoStack.Count + redoStack.Count > 1
               && (undoStack.Count + redoStack.Count > UndoLimit || bytes > UndoByteBudget))
        {
            if (undoStack.Count > redoStack.Count || (undoStack.Count == redoStack.Count && afterUndo))
            {
                var dropped = undoStack[0];
                undoStack.RemoveAt(0);
                CountState(dropped, -1);
            }
            else
            {
                var dropped = redoStack[0];
                redoStack.RemoveAt(0);
                CountState(dropped, -1);
            }
        }
    }

    public void Undo()
    {
        if (undoStack.Count == 0) return;
        var last = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        redoStack.Add(new HistoryState(BaseImage, annotations.ToList()));
        TrimHistory(afterUndo: true);
        Restore(last);
    }

    public void Redo()
    {
        if (redoStack.Count == 0) return;
        var next = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        undoStack.Add(new HistoryState(BaseImage, annotations.ToList()));
        TrimHistory(afterUndo: false);
        Restore(next);
    }

    /// <summary>Keeps carried runs only for captures the editor can still show.</summary>
    private void PruneCarriedRuns()
    {
        var live = new HashSet<ImageAsset>() { BaseImage };
        foreach (var state in undoStack) live.Add(state.Image);
        foreach (var state in redoStack) live.Add(state.Image);
        carriedRuns = carriedRuns.Where(p => live.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value);
    }

    private void Restore(HistoryState state)
    {
        CancelCanvasExpansion();
        if (!ReferenceEquals(state.Image, BaseImage))
        {
            BaseImage = state.Image;
            host.ResetBlurSamples();
            // The runs belong to the other image. Text only areas cover all of themselves until this one is read.
            TextRuns = null;
            ClearTextSelection();
            host.RecognizeText();
            host.RecognizeQrCodes();
            Notify(nameof(BaseImage));
        }
        annotations = state.Annotations.ToList();
        EnsureBlurSamplesForAnnotations();
        SelectedId = null;
        EditingTextId = null;
        newTextId = null;
        CropDraft = null;
        RefreshUndoFlags();
        RefreshDirtyState();
        Notify(nameof(Annotations));
    }

    private void RefreshUndoFlags()
    {
        CanUndo = undoStack.Count > 0;
        CanRedo = redoStack.Count > 0;
        Notify(nameof(CanUndo));
        Notify(nameof(CanRedo));
    }

    // ---- Dirty state -----------------------------------------------------------------------------------------------

    private Guid cleanImageId;
    private List<SavedAnnotation> cleanAnnotations = [];
    private string cleanBackdropKey = "", cleanWatermarkKey = "";
    private bool cleanShadows;
    private double cleanSpotlightDim = AnnotationSizes.DefaultSpotlightDim;

    /// <summary>The capture left the app (copied, saved, shared): nothing unsaved remains.</summary>
    public void MarkExported() => RecordCleanState();

    private void RecordCleanState()
    {
        cleanImageId = BaseImage.Id;
        cleanAnnotations = annotations.Select(a => new SavedAnnotation(a)).ToList();
        cleanBackdropKey = backdropKey;
        cleanWatermarkKey = watermarkKey;
        cleanShadows = annotationShadowsEnabled;
        cleanSpotlightDim = spotlightDimStrength;
        IsDirty = false;
    }

    public void RefreshDirtyState()
    {
        IsDirty = BaseImage.Id != cleanImageId
            || !annotations.Select(a => new SavedAnnotation(a)).SequenceEqual(cleanAnnotations)
            || backdropKey != cleanBackdropKey
            || watermarkKey != cleanWatermarkKey
            || annotationShadowsEnabled != cleanShadows
            // Without a region the dim changes nothing in the output.
            || (spotlightDimStrength != cleanSpotlightDim && annotations.Any(a => a.Tool == ScreenshotTool.Spotlight));
        Notify(nameof(IsDirty));
    }

    /// <summary>Test seam: swaps the base picture without any of an edit's side effects.</summary>
    internal void ReplaceBaseImageForTests(ImageAsset image) => BaseImage = image;

    /// <summary>Test seam: installs history stacks as a long session would have left them.</summary>
    internal void SetHistoryForTests(IEnumerable<HistoryState> undo, IEnumerable<HistoryState> redo)
    {
        undoStack.Clear();
        undoStack.AddRange(undo);
        redoStack.Clear();
        redoStack.AddRange(redo);
        RefreshUndoFlags();
    }

    /// <summary>Test seam: the saved-annotation snapshot the dirty state compares against.</summary>
    internal void SetCleanAnnotations(IEnumerable<Annotation> saved) =>
        cleanAnnotations = saved.Select(a => new SavedAnnotation(a)).ToList();

    // ---- Selection styling -----------------------------------------------------------------------------------------

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < annotations.Count; i++) if (annotations[i].Id == id) return i;
        return -1;
    }

    private RectD TextBounds(string text, PointD origin, int size)
    {
        var measured = host.MeasureText(text.Length == 0 ? " " : text, size, Scale);
        return new RectD(origin.X, origin.Y, measured.Width, measured.Height);
    }

    /// <summary>Applies colour, thickness, text size or arrow style changes to the selected annotation.</summary>
    private void ApplyStyleToSelection()
    {
        if (selectedId is not { } selected) return;
        int index = IndexOf(selected);
        if (index < 0) return;
        var mark = annotations[index];
        if (mark.Tool == ScreenshotTool.Image) return;
        bool arrowStyleChanged = mark.Tool == ScreenshotTool.Arrow && mark.ArrowStyle != arrowStyle;
        bool textSizeChanged = mark.Tool == ScreenshotTool.Text && mark.TextSize != textSize;
        // Marks without a thickness control keep theirs, so picking one never records an edit nobody made.
        bool usesStroke = AnnotationSupport.SelectionStyleFor(mark).Stroke is not null;
        bool strokeChanged = usesStroke && mark.Stroke != stroke;
        if (mark.Color == color && !strokeChanged && !arrowStyleChanged && !textSizeChanged) return;
        if (selected != newTextId) RegisterUndo();
        mark = mark with { Color = color };
        if (usesStroke) mark = mark with { Stroke = stroke };
        if (mark.Tool == ScreenshotTool.Arrow)
        {
            if (arrowStyleChanged && arrowStyle == ArrowStyle.Scribbly)
                mark = mark with { ScribbleSeed = AnnotationSupport.RandomScribbleSeed() };
            mark = mark with { ArrowStyle = arrowStyle };
        }
        if (mark.Tool == ScreenshotTool.Text)
            mark = mark with { TextSize = textSize, Rect = TextBounds(mark.Text, mark.Rect.Origin(), textSize) };
        annotations[index] = mark;
        Notify(nameof(Annotations));
    }

    private void ApplyBlurToSelection()
    {
        if (selectedId is not { } selected) return;
        int index = IndexOf(selected);
        if (index < 0 || annotations[index].Tool != ScreenshotTool.Pixelate) return;
        var mark = annotations[index];
        if (mark.BlurLevel == blurLevel && mark.BlurStyle == blurStyle && mark.BlurTextOnly == blurTextOnly) return;
        // The sample must exist before the mark points at it, or the redraw would show the area uncovered. Without one the
        // area keeps its look.
        if (!host.EnsureBlurSample(BaseImage, blurStyle, blurLevel))
        {
            selectedId = null;
            SyncControls(mark);
            selectedId = selected;
            return;
        }
        RegisterUndo();
        annotations[index] = mark with { BlurLevel = blurLevel, BlurStyle = blurStyle, BlurTextOnly = blurTextOnly };
        EnsureBlurSamplesForAnnotations();
        Notify(nameof(Annotations));
    }

    private void ApplyStickerToSelection()
    {
        if (selectedId is not { } selected) return;
        int index = IndexOf(selected);
        if (index < 0 || annotations[index].Tool != ScreenshotTool.Sticker || annotations[index].Text == sticker.Raw()) return;
        RegisterUndo();
        annotations[index] = annotations[index] with { Text = sticker.Raw() };
        Notify(nameof(Annotations));
    }

    /// <summary>Copies only the controls that have meaning for the picked annotation. Unused values remain available as defaults for the next new mark.</summary>
    private void SyncControls(Annotation annotation)
    {
        var style = AnnotationSupport.SelectionStyleFor(annotation);
        if (style.Color is { } c) Color = c;
        if (style.Stroke is { } s) Stroke = s;
        if (style.TextSize is { } t) TextSize = t;
        if (style.BlurLevel is { } l) BlurLevel = l;
        if (style.BlurStyle is { } b) BlurStyle = b;
        if (style.BlurTextOnly is { } o) BlurTextOnly = o;
        if (style.ArrowStyle is { } a) ArrowStyle = a;
        if (annotation.Tool == ScreenshotTool.Sticker) Sticker = Stickers.Sanitized(annotation.Text);
    }

    private void EnsureBlurSamplesForAnnotations() =>
        host.SyncBlurSamples(BaseImage, AnnotationGeometry.MosaicLevels(annotations), AnnotationGeometry.SoftBlurLevels(annotations));

    // ---- Gestures (image-point coordinates) ------------------------------------------------------------------------

    /// <summary>The press at the start of a drag. <paramref name="point"/> is in image points.</summary>
    public void BeginDrag(PointD point)
    {
        dragStart = point;
        dragRegistered = false;
        editingSelectedAnnotation = false;
        bool creating = tool is not (ScreenshotTool.Select or ScreenshotTool.Crop or ScreenshotTool.ExpandCanvas);
        // A spotlight region claims only its handles and edge, so a drag inside the one just drawn lights another region.
        if (creating && SelectedAnnotationOwns(point, includeInterior: tool != ScreenshotTool.Spotlight))
        {
            editingSelectedAnnotation = true;
            BeginSelectDrag(point);
            return;
        }
        if (creating) SelectedId = null;
        switch (tool)
        {
            case ScreenshotTool.Select:
                BeginSelectDrag(point);
                break;
            case ScreenshotTool.Crop:
                var bounds = RectMath.FromSize(ImageSize);
                if (cropDraft is { } draft && AnnotationGeometry.HandleAt(point, draft, 14 * Scale) is { } handle)
                {
                    activeHandle = handle;
                    cropResizeOrigin = draft;
                    CropLoupePoint = handle.Position(draft);
                }
                else if (cropDraft is { } fresh && AnnotationGeometry.StartsNewCropSelection(point, fresh, bounds))
                {
                    cropSelectionOrigin = fresh;
                }
                else if (cropDraft is { } moving && moving.ContainsPoint(point))
                {
                    cropMoveOrigin = moving;
                }
                break;
            case ScreenshotTool.Arrow or ScreenshotTool.Line:
                AddDraft(new Annotation(tool, points: [point, point], color: color, stroke: stroke, arrowStyle: arrowStyle));
                break;
            case ScreenshotTool.Freehand:
                AddDraft(new Annotation(tool, points: [point], color: color, stroke: stroke));
                break;
            case ScreenshotTool.Rect or ScreenshotTool.Ellipse or ScreenshotTool.Highlight or ScreenshotTool.Pixelate
                or ScreenshotTool.Redact or ScreenshotTool.Spotlight:
                if (tool == ScreenshotTool.Pixelate) host.EnsureBlurSample(BaseImage, blurStyle, blurLevel);
                AddDraft(new Annotation(tool, rect: new RectD(point.X, point.Y, 0, 0), color: color, stroke: stroke,
                    blurLevel: blurLevel, blurStyle: blurStyle, blurTextOnly: blurTextOnly));
                break;
            case ScreenshotTool.ExpandCanvas:
                var full = RectMath.FromSize(ImageSize);
                ExpansionRejected = false;
                ExpansionDraft = full;
                expansionHandle = CanvasSupport.ExpansionHandleAt(point, full,
                    CanvasSupport.ExpansionGripReach / Math.Max(CurrentDisplayZoom, 0.001));
                expansionStartPoint = expansionHandle is null ? null : point;
                Notify(nameof(ExpansionDraft));
                break;
        }
    }

    private void AddDraft(Annotation annotation)
    {
        annotations.Add(annotation);
        DraftId = annotation.Id;
        Notify(nameof(Annotations));
    }

    private void BeginSelectDrag(PointD point)
    {
        if (SelectedAnnotation is { } selected)
        {
            double tolerance = 12 * Scale;
            if (selected.Tool.ResizesWithHandles()
                && (selected.Tool == ScreenshotTool.Image
                    ? CanvasSupport.ImageHandleAt(point, selected.Rect, tolerance)
                    : AnnotationGeometry.HandleAt(point, selected.Rect, tolerance)) is { } handle)
            {
                activeHandle = handle;
                moveOrigin = selected.Rect;
                return;
            }
            if (selected.Points.Count >= 2)
            {
                if (RectMath.Distance(point, selected.Points[0]) < tolerance)
                {
                    activeHandle = SelectionHandle.TopLeft;
                    movePoints = selected.Points;
                    return;
                }
                if (RectMath.Distance(point, selected.Points[1]) < tolerance)
                {
                    activeHandle = SelectionHandle.BottomRight;
                    movePoints = selected.Points;
                    return;
                }
            }
        }
        var hitId = HitTest(point);
        SelectedId = null;
        if (hitId is { } id && annotations.FirstOrDefault(a => a.Id == id) is { } hit)
        {
            SyncControls(hit);
            SelectedId = id;
            moveOrigin = hit.Rect;
            movePoints = hit.Points;
            ClearTextSelection();
        }
        else if (WordIndexAt(point) is { } word)
        {
            // A drag over recognized text selects intersecting words.
            textSelectionAnchor = point;
            SelectedWordIndexes = [word];
            Notify(nameof(SelectedWordIndexes));
        }
    }

    public void ContinueDrag(PointD point)
    {
        if (editingSelectedAnnotation)
        {
            ContinueSelectDrag(point);
            return;
        }
        switch (tool)
        {
            case ScreenshotTool.Select:
                ContinueSelectDrag(point);
                break;
            case ScreenshotTool.Crop:
                var bounds = RectMath.FromSize(ImageSize);
                if (activeHandle is { } handle && cropResizeOrigin is { } origin)
                {
                    var rect = AnnotationGeometry.ResizedRect(origin, handle, point);
                    var snapped = AnnotationGeometry.PixelSnappedCropRect(rect, bounds);
                    CropDraft = snapped;
                    CropLoupePoint = handle.Position(snapped);
                }
                else if (cropSelectionOrigin is not null)
                {
                    CropDraft = AnnotationGeometry.PixelSnappedCropRect(AnnotationGeometry.SelectionRect(dragStart, point), bounds);
                }
                else if (cropMoveOrigin is { } moving)
                {
                    var delta = new PointD(point.X - dragStart.X, point.Y - dragStart.Y);
                    CropDraft = AnnotationGeometry.PixelSnappedCropRect(AnnotationGeometry.MovedRect(moving, delta, bounds), bounds);
                }
                break;
            case ScreenshotTool.Arrow or ScreenshotTool.Line:
                UpdateDraft(a => a with { Points = [dragStart, point] });
                break;
            case ScreenshotTool.Freehand:
                UpdateDraft(a => a with { Points = a.Points.Append(point).ToArray() });
                break;
            case ScreenshotTool.Rect or ScreenshotTool.Ellipse or ScreenshotTool.Highlight or ScreenshotTool.Pixelate
                or ScreenshotTool.Redact or ScreenshotTool.Spotlight:
                UpdateDraft(a => a with { Rect = AnnotationGeometry.SelectionRect(dragStart, point) });
                break;
            case ScreenshotTool.ExpandCanvas:
                if (expansionHandle is { } grip && expansionStartPoint is { } start)
                {
                    var anchor = grip.Position(RectMath.FromSize(ImageSize));
                    var target = new PointD(anchor.X + point.X - start.X, anchor.Y + point.Y - start.Y);
                    ExpansionDraft = CanvasSupport.ExpandedRect(ImageSize, grip, target);
                    ExpansionRejected = ExpansionDraft is null;
                    Notify(nameof(ExpansionDraft));
                }
                break;
        }
    }

    internal void ContinueSelectDrag(PointD point)
    {
        if (textSelectionAnchor is { } anchor)
        {
            SelectedWordIndexes = AnnotationGeometry.WordSelection(anchor, point, TextWords.Select(w => w.Rect).ToList());
            Notify(nameof(SelectedWordIndexes));
            return;
        }
        if (selectedId is not { } selected) return;
        int index = IndexOf(selected);
        if (index < 0) return;
        if (!dragRegistered)
        {
            RegisterUndo();
            dragRegistered = true;
        }
        var mark = annotations[index];
        if (activeHandle is { } handle)
        {
            if (mark.Points.Count >= 2)
            {
                var points = movePoints.ToArray();
                if (handle == SelectionHandle.TopLeft) points[0] = point;
                else points[1] = point;
                annotations[index] = mark with { Points = points };
            }
            else
            {
                annotations[index] = mark with
                {
                    Rect = mark.Tool == ScreenshotTool.Image
                        ? CanvasSupport.ResizedImageRect(moveOrigin, handle, point)
                        : AnnotationGeometry.ResizedRect(moveOrigin, handle, point),
                };
            }
            Notify(nameof(Annotations));
            return;
        }
        var delta = new PointD(point.X - dragStart.X, point.Y - dragStart.Y);
        if (mark.Tool == ScreenshotTool.Image)
        {
            // Whole pixels, so the moved layer still exports pixel for pixel.
            annotations[index] = mark with
            {
                Rect = new RectD(RectMath.Rounded(moveOrigin.MinX() + delta.X), RectMath.Rounded(moveOrigin.MinY() + delta.Y),
                                 moveOrigin.Width, moveOrigin.Height),
            };
        }
        else if (mark.Points.Count == 0)
        {
            annotations[index] = mark with { Rect = moveOrigin.OffsetBy(delta.X, delta.Y) };
        }
        else
        {
            annotations[index] = mark with { Points = movePoints.Select(p => new PointD(p.X + delta.X, p.Y + delta.Y)).ToArray() };
        }
        Notify(nameof(Annotations));
    }

    /// <summary>
    /// The release. <paramref name="isTap"/> is decided by the view in screen points (see <see cref="EditorDrag"/>), so a
    /// click stays a click at any zoom level; deciding it here in image pixels made taps on zoomed-out captures read as drags.
    /// </summary>
    public void EndDrag(PointD point, bool isTap)
    {
        try
        {
            EndDragCore(point, isTap);
        }
        finally
        {
            DraftId = null;
            activeHandle = null;
            cropResizeOrigin = null;
            cropMoveOrigin = null;
            cropSelectionOrigin = null;
            CropLoupePoint = null;
            dragRegistered = false;
            editingSelectedAnnotation = false;
        }
    }

    private void EndDragCore(PointD point, bool isTap)
    {
        if (editingSelectedAnnotation)
        {
            FinishSelectDrag(point, isTap);
            return;
        }
        switch (tool)
        {
            case ScreenshotTool.Text:
            {
                if (!isTap) return;
                if (SelectExistingAnnotation(point)) return;
                var annotation = new Annotation(ScreenshotTool.Text, color: color, stroke: stroke, textSize: textSize);
                annotation = annotation with { Rect = TextBounds("", point, textSize) };
                annotations.Add(annotation);
                newTextId = annotation.Id;
                SelectedId = annotation.Id;
                EditingTextId = annotation.Id;
                Notify(nameof(Annotations));
                break;
            }
            case ScreenshotTool.Sticker:
            {
                if (!isTap) return;
                if (SelectExistingAnnotation(point)) return;
                RegisterUndo();
                var bounds = RectMath.FromSize(ImageSize);
                double side = AnnotationGeometry.StickerSide(ImageSize, Scale);
                var annotation = new Annotation(ScreenshotTool.Sticker,
                    rect: AnnotationGeometry.StickerRect(point, side, bounds), text: sticker.Raw(), color: color, stroke: stroke);
                annotations.Add(annotation);
                SelectedId = annotation.Id;
                Notify(nameof(Annotations));
                break;
            }
            case ScreenshotTool.Counter:
            {
                if (!isTap) return;
                if (SelectExistingAnnotation(point)) return;
                RegisterUndo();
                var annotation = new Annotation(ScreenshotTool.Counter, rect: new RectD(point.X, point.Y, 0, 0),
                    color: color, stroke: stroke, number: 1);
                annotations.Add(annotation);
                annotations = AnnotationSupport.RenumberingCounters(annotations);
                SelectedId = annotation.Id;
                Notify(nameof(Annotations));
                break;
            }
            case ScreenshotTool.Arrow or ScreenshotTool.Line or ScreenshotTool.Rect or ScreenshotTool.Ellipse
                or ScreenshotTool.Highlight or ScreenshotTool.Pixelate or ScreenshotTool.Redact or ScreenshotTool.Spotlight
                or ScreenshotTool.Freehand:
                if (DraftId is { } draft) FinishDrawing(draft, isTap);
                if (isTap) SelectExistingAnnotation(point);
                break;
            case ScreenshotTool.ExpandCanvas:
                if (ExpansionRejected)
                {
                    host.Beep();
                }
                else if (ExpansionDraft is { } expansion && expansion.Size() != ImageSize)
                {
                    if (!CommitCanvasExpansion(expansion)) host.Beep();
                }
                CancelCanvasExpansion();
                break;
            case ScreenshotTool.Select:
                FinishSelectDrag(point, isTap);
                break;
            case ScreenshotTool.Crop:
                if (isTap && cropSelectionOrigin is { } origin) CropDraft = origin;
                break;
        }
    }

    /// <summary>A click only selects. It must leave both directions of history intact.</summary>
    private void FinishDrawing(Guid id, bool isTap)
    {
        if (isTap)
        {
            annotations.RemoveAll(a => a.Id == id);
            Notify(nameof(Annotations));
        }
        else
        {
            RegisterUndo(excluding: id);
            SelectedId = id;
        }
    }

    /// <summary>
    /// The visible selection remains directly editable after creation. A new tool or a gesture outside it ends that
    /// priority and creates normally. Without the interior, an area mark answers only at its handles and edge.
    /// </summary>
    public bool SelectedAnnotationOwns(PointD point, bool includeInterior = true)
    {
        if (SelectedAnnotation is not { } selected) return false;
        double tolerance = 12 * Scale;
        if (selected.Tool.ResizesWithHandles()
            && (selected.Tool == ScreenshotTool.Image
                ? CanvasSupport.ImageHandleAt(point, selected.Rect, tolerance)
                : AnnotationGeometry.HandleAt(point, selected.Rect, tolerance)) is not null)
            return true;
        if (selected.Points.Take(2).Any(p => RectMath.Distance(point, p) < tolerance)) return true;
        return HitTest(point, includeInterior) == selected.Id;
    }

    private void FinishSelectDrag(PointD point, bool isTap)
    {
        textSelectionAnchor = null;
        if (!isTap || dragRegistered) return;
        var hitId = HitTest(point);
        SelectedId = null;
        if (hitId is { } id && annotations.FirstOrDefault(a => a.Id == id) is { } hit)
        {
            SyncControls(hit);
            SelectedId = id;
            EditingTextId = hit.Tool == ScreenshotTool.Text ? id : null;
            ClearTextSelection();
        }
        else if (WordIndexAt(point) is { } word)
        {
            SelectedWordIndexes = [word];
            Notify(nameof(SelectedWordIndexes));
        }
        else
        {
            ClearTextSelection();
        }
    }

    /// <summary>
    /// A click on an existing mark always means "edit this", even while a creation tool is active. Real drags still create
    /// with the active tool. Sticker selection updates the style picker without mutating the mark; text enters its inline
    /// editor immediately.
    /// </summary>
    private bool SelectExistingAnnotation(PointD point)
    {
        if (HitTest(point, includeShapeInteriors: false) is not { } hitId
            || annotations.FirstOrDefault(a => a.Id == hitId) is not { } hit)
        {
            SelectedId = null;
            return false;
        }
        SelectedId = null;
        SyncControls(hit);
        SelectedId = hitId;
        EditingTextId = hit.Tool == ScreenshotTool.Text ? hitId : null;
        ClearTextSelection();
        Tool = ScreenshotTool.Select;
        return true;
    }

    private void UpdateDraft(Func<Annotation, Annotation> mutate)
    {
        if (DraftId is not { } id) return;
        int index = IndexOf(id);
        if (index < 0) return;
        annotations[index] = mutate(annotations[index]);
        Notify(nameof(Annotations));
    }

    /// <summary>
    /// The topmost annotation under a point, 10 points of tolerance. <paramref name="includeShapeInteriors"/> false is the
    /// creation-tap variant: area shapes (boxes, ellipses, highlights, censors) only answer near their edge, so their
    /// inside stays free for placing a new text, sticker or counter.
    /// </summary>
    public Guid? HitTest(PointD point, bool includeShapeInteriors = true)
    {
        double tolerance = 10 * Scale;
        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            var annotation = annotations[i];
            switch (annotation.Tool)
            {
                case ScreenshotTool.Arrow or ScreenshotTool.Line:
                    if (annotation.Points.Count < 2) continue;
                    if (AnnotationGeometry.Distance(point, annotation.Points[0], annotation.Points[1])
                        <= tolerance + annotation.Stroke.Width() * Scale / 2)
                        return annotation.Id;
                    break;
                case ScreenshotTool.Freehand:
                    foreach (var pathPoint in annotation.Points)
                    {
                        if (RectMath.Distance(point, pathPoint) <= tolerance) return annotation.Id;
                    }
                    break;
                case ScreenshotTool.Counter:
                {
                    double radius = AnnotationGeometry.CounterDiameter(annotation.RenderReferenceSize ?? ImageSize, 1) / 2;
                    if (RectMath.Distance(point, new PointD(annotation.Rect.MidX, annotation.Rect.MidY())) <= radius + 4 * Scale)
                        return annotation.Id;
                    break;
                }
                case ScreenshotTool.Rect or ScreenshotTool.Ellipse or ScreenshotTool.Highlight or ScreenshotTool.Pixelate
                    or ScreenshotTool.Redact or ScreenshotTool.Spotlight:
                {
                    var outer = annotation.Rect.InsetBy(-tolerance / 2, -tolerance / 2);
                    if (!outer.ContainsPoint(point)) continue;
                    if (includeShapeInteriors) return annotation.Id;
                    // Edge ring only: a shape too small to have a meaningful interior stays fully tappable.
                    var inner = annotation.Rect.InsetBy(tolerance, tolerance);
                    if (inner.IsEmpty() || !inner.ContainsPoint(point)) return annotation.Id;
                    break;
                }
                case ScreenshotTool.Image:
                    if (includeShapeInteriors && annotation.Rect.InsetBy(-tolerance / 2, -tolerance / 2).ContainsPoint(point))
                        return annotation.Id;
                    break;
                case ScreenshotTool.Text or ScreenshotTool.Sticker:
                    if (annotation.Rect.InsetBy(-tolerance / 2, -tolerance / 2).ContainsPoint(point)) return annotation.Id;
                    break;
            }
        }
        return null;
    }

    // ---- Edits -----------------------------------------------------------------------------------------------------

    public void DeleteSelected()
    {
        if (selectedId is not { } selected || IndexOf(selected) < 0) return;
        RegisterUndo();
        annotations.RemoveAll(a => a.Id == selected);
        annotations = AnnotationSupport.RenumberingCounters(annotations);
        SelectedId = null;
        EditingTextId = null;
        if (newTextId == selected) newTextId = null;
        Notify(nameof(Annotations));
    }

    /// <summary>
    /// Moves the selected annotation one step through the drawing order, so a box drawn last can sit behind text written
    /// first. Counters are numbered by their place in the array, so moving one past another renumbers both.
    /// </summary>
    public void MoveSelected(LayerMove move)
    {
        if (selectedId is not { } selected) return;
        var reordered = AnnotationSupport.Reordering(annotations, selected, move);
        if (reordered.Select(a => a.Id).SequenceEqual(annotations.Select(a => a.Id))) return;
        RegisterUndo();
        annotations = AnnotationSupport.RenumberingCounters(reordered);
        Notify(nameof(Annotations));
    }

    /// <summary>Settles the inline text editor: trims, resizes the box, drops an empty new text, and records one undo step.</summary>
    public void CommitText(Guid id, string text)
    {
        int index = IndexOf(id);
        if (index < 0) return;
        EditingTextId = null;
        var trimmed = text.Trim();
        bool isNew = newTextId == id;
        if (isNew && trimmed.Length == 0)
        {
            annotations.RemoveAt(index);
            if (selectedId == id) SelectedId = null;
            newTextId = null;
            RefreshUndoFlags();
            RefreshDirtyState();
            Notify(nameof(Annotations));
            return;
        }
        if (annotations[index].Text == trimmed)
        {
            newTextId = null;
            return;
        }
        RegisterUndo(excluding: isNew ? id : null);
        // Registering may have shifted nothing in the live list, but index is looked up again to stay honest.
        index = IndexOf(id);
        if (trimmed.Length == 0)
        {
            annotations.RemoveAt(index);
            if (selectedId == id) SelectedId = null;
            Notify(nameof(Annotations));
            return;
        }
        var mark = annotations[index];
        annotations[index] = mark with { Text = trimmed, Rect = TextBounds(trimmed, mark.Rect.Origin(), mark.TextSize) };
        newTextId = null;
        Notify(nameof(Annotations));
    }

    /// <summary>
    /// Cuts the picture to the crop draft, snapped to whole pixels. A draft that rounds to the whole canvas, is under 8
    /// pixels a side, or cannot be cropped by the host just leaves the crop tool, keeping the buffer and the history.
    /// </summary>
    public void ApplyCrop()
    {
        if (cropDraft is not { } draft)
        {
            Tool = ScreenshotTool.Select;
            return;
        }
        var full = RectMath.FromSize(ImageSize);
        var cropRect = AnnotationGeometry.PixelSnappedCropRect(draft, full);
        if (cropRect == full || cropRect.Width < 8 || cropRect.Height < 8
            || host.CropImage(BaseImage, cropRect) is not { } cropped)
        {
            Tool = ScreenshotTool.Select;
            return;
        }
        RegisterUndo();
        var previousImage = BaseImage;
        BaseImage = cropped;
        host.ResetBlurSamples();
        ClearTextSelection();
        var croppedBounds = RectMath.FromSize(cropped.PixelSize);
        TextWords = TextWords
            .Select(w => w with { Rect = w.Rect.OffsetBy(-cropRect.MinX(), -cropRect.MinY()) })
            .Where(w => w.Rect.Intersects(croppedBounds)).ToList();
        // Moved runs still cover the cropped capture's text, including what the words alone miss, and stay once it is
        // read again. If the old one was never read, text only areas keep waiting for recognition.
        carriedRuns.TryGetValue(previousImage, out var previousCarried);
        TextRuns = AnnotationGeometry.CroppedRuns(TextRuns, cropRect);
        var carried = TextRuns ?? AnnotationGeometry.CroppedRuns(previousCarried, cropRect);
        if (carried is { Count: > 0 }) carriedRuns[cropped] = carried.ToList();
        PruneCarriedRuns();
        annotations = annotations.Select(a => a with
        {
            Rect = a.Rect.OffsetBy(-cropRect.MinX(), -cropRect.MinY()),
            Points = a.Points.Select(p => new PointD(p.X - cropRect.MinX(), p.Y - cropRect.MinY())).ToArray(),
        }).ToList();
        EnsureBlurSamplesForAnnotations();
        CropDraft = null;
        SelectedId = null;
        Tool = ScreenshotTool.Select;
        host.RecognizeText();
        host.RecognizeQrCodes();
        Notify(nameof(BaseImage));
        Notify(nameof(Annotations));
    }

    // ---- Rules the editor view reads --------------------------------------------------------------------------------

    /// <summary>
    /// The pointer over the canvas. The same rule a press follows: a spotlight region offers only its handles and edge
    /// while its tool is active, and expanding the canvas never moves a mark.
    /// </summary>
    public CanvasCursor CursorAt(PointD point)
    {
        if (tool != ScreenshotTool.Select && tool != ScreenshotTool.ExpandCanvas
            && SelectedAnnotationOwns(point, includeInterior: tool != ScreenshotTool.Spotlight))
            return CanvasCursor.OpenHand;
        if (tool != ScreenshotTool.Select) return CanvasCursor.Crosshair;
        // Recognized text under the cursor reads as text.
        return WordIndexAt(point) is not null ? CanvasCursor.IBeam : CanvasCursor.Arrow;
    }

    public bool ShowsColorControls => tool switch
    {
        ScreenshotTool.Arrow or ScreenshotTool.Line or ScreenshotTool.Rect or ScreenshotTool.Ellipse
            or ScreenshotTool.Freehand or ScreenshotTool.Highlight or ScreenshotTool.Text or ScreenshotTool.Counter
            or ScreenshotTool.Redact => true,
        ScreenshotTool.Select => SelectedAnnotation is { } s
            && s.Tool is not (ScreenshotTool.Sticker or ScreenshotTool.Image or ScreenshotTool.Pixelate or ScreenshotTool.Spotlight),
        _ => false,
    };

    private bool ShowsFor(ScreenshotTool kind) =>
        tool == kind || (tool == ScreenshotTool.Select && SelectedAnnotation?.Tool == kind);

    /// <summary>A picked region keeps the dim control, so its strength changes without going back to the tool.</summary>
    public bool ShowsSpotlightControls => ShowsFor(ScreenshotTool.Spotlight);
    public bool ShowsBlurControls => ShowsFor(ScreenshotTool.Pixelate);

    /// <summary>Text takes a point size where shapes take a thickness.</summary>
    public bool ShowsTextSizeControls => ShowsFor(ScreenshotTool.Text);

    public bool ShowsArrowStyleControls => ShowsFor(ScreenshotTool.Arrow);

    /// <summary>Depth only means something when there is another shape to pass; a shape still being drawn counts once it lands.</summary>
    public bool ShowsLayerControls => annotations.Count(a => a.Id != DraftId) > 1;

    /// <summary>Each direction dims on its own once the shape reaches that end.</summary>
    public bool CanMoveSelected(LayerMove move) => selectedId is { } id && AnnotationSupport.CanReorder(annotations, id, move);
}
