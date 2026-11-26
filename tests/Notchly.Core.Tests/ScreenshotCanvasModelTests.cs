// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/ScreenshotCanvasModelTests.swift (history budget, paste, expansion, crop) and the pure parts of
// ScreenshotSpotlightEditingTests.swift, plus coverage of the editor model's drag, hit-test, style and history behaviour
// that upstream exercises through its extracted controller. The model holds no bitmaps: pictures are ImageAsset ids with a
// pixel size, and a recording host stands in for the pixel work.
using Notchly.Core.Capture;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using static Notchly.Core.Tests.CanvasTestKit;

namespace Notchly.Core.Tests;

internal sealed class RecordingHost : ICanvasHost
{
    public int Beeps, TextRecognitions, QrReads, SampleResets;
    public bool BlurSampleSucceeds = true;
    public bool CropSucceeds = true;
    public readonly List<(RectD Rect, CanvasFill Fill)> Expansions = [];
    public readonly List<RectD> Crops = [];
    public readonly List<(BlurStyle Style, int Level)> EnsuredSamples = [];
    public readonly List<(HashSet<int> Mosaic, HashSet<int> Soft)> Syncs = [];

    public ImageAsset? ExpandImage(ImageAsset image, RectD rect, CanvasFill fill)
    {
        Expansions.Add((rect, fill));
        return new ImageAsset((int)rect.Width, (int)rect.Height);
    }

    public ImageAsset? CropImage(ImageAsset image, RectD rect)
    {
        Crops.Add(rect);
        return CropSucceeds ? new ImageAsset((int)rect.Width, (int)rect.Height) : null;
    }

    public bool EnsureBlurSample(ImageAsset baseImage, BlurStyle style, int level)
    {
        EnsuredSamples.Add((style, level));
        return BlurSampleSucceeds;
    }

    public void SyncBlurSamples(ImageAsset baseImage, IReadOnlySet<int> mosaicLevels, IReadOnlySet<int> softBlurLevels) =>
        Syncs.Add((mosaicLevels.ToHashSet(), softBlurLevels.ToHashSet()));

    public void ResetBlurSamples() => SampleResets++;
    public void RecognizeText() => TextRecognitions++;
    public void RecognizeQrCodes() => QrReads++;
    public void Beep() => Beeps++;
}

public class ScreenshotCanvasHistoryTests
{
    private static ImageAsset Frame(int w = 128, int h = 128) => new(w, h);

    [Fact]
    public void StatesSharingOneCaptureStayBoundedBySteps()
    {
        var model = Model(40, 40);
        var pasted = Image(4, 4);
        for (int i = 0; i < CanvasModel.UndoLimit + 3; i++) Assert.True(model.PasteImage(pasted));
        Assert.Equal(CanvasModel.UndoLimit, model.UndoStates.Count);
    }

    [Fact]
    public void OnlyRecentDistinctFramesFitTheUndoPixelBudget()
    {
        var frames = Enumerable.Range(0, 6).Select(_ => Frame()).ToList();
        var pasted = Image(4, 4);
        var big = new CanvasModel(frames[0], 1) { UndoByteBudget = frames[0].ByteCount * 4 + 4096 };
        foreach (var image in frames)
        {
            big.ReplaceBaseImageForTests(image);
            Assert.True(big.PasteImage(pasted));
            big.Annotations = [];
        }
        Assert.Equal(4, big.UndoStates.Count);
        Assert.Same(frames[2], big.UndoStates[0].Image);
    }

    [Fact]
    public void ImagesRetainedOnlyByPastedAnnotationsShareTheHistoryBudget()
    {
        var frames = Enumerable.Range(0, 6).Select(_ => Frame()).ToList();
        var pasted = Image(4, 4);
        var layers = new CanvasModel(pasted, 1) { UndoByteBudget = frames[0].ByteCount * 4 + 4096 };
        foreach (var image in frames)
        {
            Assert.True(layers.PasteImage(image));
            Assert.True(layers.PasteImage(pasted));
            layers.Annotations = [];
        }
        var retained = layers.UndoStates.SelectMany(s => s.Annotations.Select(a => a.ImageAsset).OfType<ImageAsset>()).ToHashSet();
        Assert.Equal(4, retained.Count);
        Assert.DoesNotContain(frames[0], retained);
    }

    [Fact]
    public void AHighDepthImageIsChargedItsActualRowStride()
    {
        var pasted = Image(4, 4);
        var padded = new ImageAsset(32, 32, byteCount: 512L * 32);
        var depth = new CanvasModel(pasted, 1) { UndoByteBudget = padded.ByteCount + 1024 };
        Assert.True(depth.PasteImage(padded));
        Assert.True(depth.PasteImage(pasted));
        depth.Annotations = [];
        depth.ReplaceBaseImageForTests(new ImageAsset(32, 32));
        Assert.True(depth.PasteImage(pasted));
        Assert.All(depth.UndoStates, s => Assert.DoesNotContain(s.Annotations, a => ReferenceEquals(a.ImageAsset, padded)));
    }

    [Fact]
    public void UndoBudgetsBothStacksAndKeepsTheImmediateRedo()
    {
        var frames = Enumerable.Range(0, 6).Select(_ => Frame()).ToList();
        var mixed = new CanvasModel(frames[5], 1) { UndoByteBudget = frames[0].ByteCount * 2 + 512 };
        mixed.SetHistoryForTests(frames.Take(2).Select(f => new HistoryState(f, [])), [new HistoryState(frames[3], [])]);
        mixed.Undo();
        Assert.Equal(2, mixed.UndoStates.Count + mixed.RedoStates.Count);
        Assert.Same(frames[5], mixed.RedoStates[^1].Image);
        mixed.Redo();
        Assert.Same(frames[5], mixed.BaseImage);
    }

    [Fact]
    public void AnOversizedSingleStepIsNeverDropped()
    {
        var model = new CanvasModel(Frame(), 1) { UndoByteBudget = 1 };
        Assert.True(model.PasteImage(Image(4, 4)));
        Assert.Single(model.UndoStates);
    }

    [Fact]
    public void ATapWithoutADrawingDropsTheDraftAndPreservesUndoAndRedo()
    {
        var frames = Enumerable.Range(0, 3).Select(_ => Frame()).ToList();
        var click = new CanvasModel(frames[1], 1);
        click.SetHistoryForTests([new HistoryState(frames[0], [])], [new HistoryState(frames[2], [])]);
        var existing = Mark(ScreenshotTool.Rect, R(5, 5, 24, 24));
        click.Tool = ScreenshotTool.Rect;
        click.Annotations = [existing];
        click.SelectedId = existing.Id;
        click.BeginDrag(P(60, 60));
        click.EndDrag(P(60, 60), isTap: true);
        Assert.Equal([existing.Id], click.Annotations.Select(a => a.Id));
        Assert.Null(click.SelectedId);
        Assert.Null(click.DraftId);
        Assert.Single(click.UndoStates);
        Assert.Single(click.RedoStates);
    }

    [Fact]
    public void PressingToDrawClearsTheSelectionAndTheShapeTakesItsDraggedBounds()
    {
        var frames = Enumerable.Range(0, 3).Select(_ => Frame()).ToList();
        var click = new CanvasModel(frames[1], 1);
        click.SetHistoryForTests([new HistoryState(frames[0], [])], [new HistoryState(frames[2], [])]);
        var existing = Mark(ScreenshotTool.Rect, R(5, 5, 24, 24));
        click.Tool = ScreenshotTool.Rect;
        click.Annotations = [existing];
        click.SelectedId = existing.Id;
        click.BeginDrag(P(60, 60));
        var drawing = click.DraftId;
        Assert.NotNull(drawing);
        Assert.Equal(drawing, click.Annotations[^1].Id);
        Assert.Null(click.SelectedId);
        click.ContinueDrag(P(95, 110));
        Assert.Null(click.SelectedId);
        Assert.Equal(R(60, 60, 35, 50), click.Annotations[^1].Rect);
        Assert.Single(click.UndoStates);
        Assert.Single(click.RedoStates);
        click.EndDrag(P(95, 110), isTap: false);
        Assert.Equal(drawing, click.SelectedId);
        Assert.Null(click.DraftId);
        Assert.Equal(2, click.Annotations.Count);
        Assert.Equal([existing.Id], click.UndoStates[^1].Annotations.Select(a => a.Id));
        Assert.Empty(click.RedoStates);
    }

    [Theory]
    [InlineData(0, 0, 128, 128)]
    [InlineData(-4, -3, 136, 134)]
    public void ACropThatClampsToTheFullCanvasKeepsItsBufferAndHistory(double x, double y, double w, double h)
    {
        var frame = Frame();
        var model = new CanvasModel(frame, 1);
        var before = model.UndoStates.Count;
        model.CropDraft = R(x, y, w, h);
        model.ApplyCrop();
        Assert.Same(frame, model.BaseImage);
        Assert.Equal(before, model.UndoStates.Count);
        Assert.Equal(ScreenshotTool.Select, model.Tool);
    }

    [Fact]
    public void ACropThatRoundsToTheFullCanvasKeepsItsBufferAndHistory()
    {
        var frame = Frame();
        var model = new CanvasModel(frame, 1);
        model.CropDraft = R(0.2, 0.4, 127.6, 127.2);
        model.ApplyCrop();
        Assert.Same(frame, model.BaseImage);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void PasteAppendsAndSelectsASingleObjectAndBothEditsUndoSeparately()
    {
        var baseImage = Image(40, 40);
        var host = new RecordingHost();
        var model = new CanvasModel(baseImage, 1, host: host);
        var pasted = Image(20, 10);
        Assert.True(model.PasteImage(pasted));
        var id = model.SelectedId;
        Assert.Equal(id, model.HitTest(P(20, 20)));
        Assert.Null(model.HitTest(P(20, 20), includeShapeInteriors: false));
        Assert.Single(model.Annotations);
        Assert.Equal(id, model.Annotations[0].Id);
        Assert.True(model.CommitCanvasExpansion(R(-10, -20, 50, 60)));
        Assert.Equal(P(20, 35), model.Annotations[0].Rect.Origin());
        Assert.Equal(2, model.UndoStates.Count);
        model.Undo();
        Assert.Same(baseImage, model.BaseImage);
        Assert.Equal(id, model.Annotations[0].Id);
        model.Undo();
        Assert.Empty(model.Annotations);
        model.Redo();
        model.Redo();
        Assert.Equal(50, model.BaseImage.PixelWidth);
        Assert.Equal(id, model.Annotations[0].Id);
    }

    [Fact]
    public void AnInvalidExpansionIsRejectedAndLeavesImageAndHistoryUnchanged()
    {
        var model = Model(40, 40);
        Assert.True(model.PasteImage(Image(20, 10)));
        var count = model.UndoStates.Count;
        var current = model.BaseImage;
        Assert.False(model.CommitCanvasExpansion(R(0, 0, 1, 1)));
        Assert.Same(current, model.BaseImage);
        Assert.Equal(count, model.UndoStates.Count);
    }

    [Fact]
    public void ExpandingToTheCurrentCanvasChangesNothing()
    {
        var model = Model(40, 40);
        Assert.False(model.CommitCanvasExpansion(R(0, 0, 40, 40)));
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void AHostThatCannotMakeThePictureFailsTheExpansionCleanly()
    {
        var model = new CanvasModel(Image(40, 40), 1, host: new NoPixelsHost());
        Assert.False(model.CommitCanvasExpansion(R(0, 0, 60, 40)));
        Assert.Empty(model.UndoStates);
    }

    private sealed class NoPixelsHost : ICanvasHost
    {
        public ImageAsset? ExpandImage(ImageAsset image, RectD rect, CanvasFill fill) => null;
    }

    [Fact]
    public void ExpansionPassesTheChosenFillToTheHost()
    {
        var host = new RecordingHost();
        var model = new CanvasModel(Image(40, 40), 1, host: host) { ExpansionFill = CanvasFill.Transparent };
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 60, 40)));
        Assert.Equal((R(0, 0, 60, 40), CanvasFill.Transparent), host.Expansions.Single());
    }

    [Fact]
    public void ExpansionRestartsRecognitionAndForgetsTheOldWords()
    {
        var host = new RecordingHost();
        var model = new CanvasModel(Image(40, 40), 1, host: host);
        model.ApplyRecognition(model.BaseImage, [new RecognizedWord("a", R(0, 0, 5, 5), 0)], [R(0, 0, 5, 5)]);
        Assert.True(model.CommitCanvasExpansion(R(-10, 0, 50, 40)));
        Assert.Empty(model.TextWords);
        Assert.Null(model.TextRuns);
        Assert.Equal(1, host.TextRecognitions);
        Assert.Equal(1, host.QrReads);
        Assert.Equal(1, host.SampleResets);
    }

    [Fact]
    public void ExpansionCarriesTheOldRunsIntoTheNewCoordinatesAndPublishesThemAfterRecognition()
    {
        var model = Model(40, 40);
        model.ApplyRecognition(model.BaseImage, [], [R(5, 5, 10, 10)]);
        Assert.True(model.CommitCanvasExpansion(R(-10, -20, 50, 60)));
        Assert.True(model.ApplyRecognition(model.BaseImage, [], []));
        Assert.Equal([R(15, 25, 10, 10)], model.TextRuns);
    }

    [Fact]
    public void RecognitionForAStalePictureIsIgnored()
    {
        var model = Model(40, 40);
        var old = model.BaseImage;
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 60, 40)));
        Assert.False(model.ApplyRecognition(old, [new RecognizedWord("a", R(0, 0, 1, 1), 0)], []));
        Assert.Empty(model.TextWords);
    }

    [Fact]
    public void AMarkKeepsTheCanvasSizeItWasDrawnOnAfterExpansion()
    {
        var model = Model(800, 800);
        var counter = new Annotation(ScreenshotTool.Counter, R(100, 100, 0, 0), number: 1);
        var area = new Annotation(ScreenshotTool.Pixelate, R(5, 5, 30, 30));
        var arrow = Mark(ScreenshotTool.Arrow, points: [P(1, 1), P(2, 2)]);
        model.Annotations = [counter, area, arrow];
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 1600, 1600)));
        Assert.Equal(new SizeD(800, 800), model.Annotations[0].RenderReferenceSize);
        Assert.Equal(new SizeD(800, 800), model.Annotations[1].RenderReferenceSize);
        Assert.Null(model.Annotations[2].RenderReferenceSize);
        model.Undo();
        Assert.Null(model.Annotations[0].RenderReferenceSize);
    }

    [Fact]
    public void AnExistingReferenceSizeSurvivesASecondExpansion()
    {
        var model = Model(100, 100);
        model.Annotations = [new Annotation(ScreenshotTool.Counter, R(10, 10, 0, 0), number: 1)];
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 200, 100)));
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 300, 100)));
        Assert.Equal(new SizeD(100, 100), model.Annotations[0].RenderReferenceSize);
    }

    [Fact]
    public void AnExpandedCanvasKeepsTheCounterItsOriginalHitSize()
    {
        var model = Model(800, 800);
        model.Annotations = [new Annotation(ScreenshotTool.Counter, R(100, 100, 0, 0), number: 1)];
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 1600, 1600)));
        Assert.NotNull(model.HitTest(P(119, 100)));
        // On the 1600 canvas the badge would reach 33 + 4 points; it must stay at the original 17 + 4.
        Assert.Null(model.HitTest(P(125, 100)));
    }

    [Fact]
    public void ABlurAreaKeepsItsSizeOfCanvasForItsSample()
    {
        var model = Model(40, 40);
        model.Annotations = [new Annotation(ScreenshotTool.Pixelate, R(5, 5, 30, 30))];
        Assert.True(model.CommitCanvasExpansion(R(-20, -20, 80, 80)));
        Assert.Equal(new SizeD(40, 40), model.Annotations[0].RenderReferenceSize);
        Assert.Equal(R(25, 25, 30, 30), model.Annotations[0].Rect);
    }

    [Fact]
    public void ExpansionResyncsTheBlurSamplesToTheMarksInUse()
    {
        var host = new RecordingHost();
        var model = new CanvasModel(Image(40, 40), 1, host: host);
        model.Annotations =
        [
            new Annotation(ScreenshotTool.Pixelate, R(1, 1, 5, 5), blurLevel: 4),
            new Annotation(ScreenshotTool.Pixelate, R(1, 1, 5, 5), blurLevel: 2, blurStyle: BlurStyle.Blur),
        ];
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 60, 40)));
        var sync = host.Syncs.Last();
        Assert.Equal(new HashSet<int> { 4 }, sync.Mosaic);
        Assert.Equal(new HashSet<int> { 2 }, sync.Soft);
    }

    [Fact]
    public void ASpotlightRegionMovesWithTheContent()
    {
        var model = Model(40, 40);
        var below = new Annotation(ScreenshotTool.Image, R(25, 25, 10, 10), imageAsset: Image(10, 10));
        var spot = new Annotation(ScreenshotTool.Spotlight, R(5, 5, 10, 10));
        var above = new Annotation(ScreenshotTool.Image, R(25, 5, 10, 10), imageAsset: Image(10, 10));
        model.Annotations = [below, spot, above];
        Assert.True(model.CommitCanvasExpansion(R(-20, 0, 60, 40)));
        Assert.Equal(R(25, 5, 10, 10), model.Annotations[1].Rect);
        Assert.Null(model.Annotations[1].RenderReferenceSize);
    }

    [Fact]
    public void ASpotlightRegionPastTheOldEdgeLightsOnlyTheContentItCovered()
    {
        var model = Model(40, 40);
        model.Annotations = [new Annotation(ScreenshotTool.Spotlight, R(30, 5, 16, 10))];
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 60, 40)));
        Assert.Equal([R(30, 5, 10, 10)], model.Annotations.Select(a => a.Rect));
    }

    [Fact]
    public void ASpotlightRegionOffTheImageIsDroppedByAnExpansionTowardIt()
    {
        var model = Model(40, 40);
        model.Annotations = [new Annotation(ScreenshotTool.Spotlight, R(45, 5, 10, 10))];
        Assert.True(model.CommitCanvasExpansion(R(0, 0, 60, 40)));
        Assert.Empty(model.Annotations);
    }

    [Fact]
    public void AnExpansionShiftsEveryPointAndRect()
    {
        var model = Model(40, 40);
        model.Annotations = [new Annotation(ScreenshotTool.Arrow, points: [P(1, 2), P(3, 4)])];
        Assert.True(model.CommitCanvasExpansion(R(-5, -10, 45, 50)));
        Assert.Equal([P(6, 12), P(8, 14)], model.Annotations[0].Points);
    }

    [Fact]
    public void LeavingExpansionDropsTheDragFirstThenTheTool()
    {
        var model = Model(40, 40);
        model.Tool = ScreenshotTool.ExpandCanvas;
        model.BeginDrag(P(20, 20));
        Assert.NotNull(model.ExpansionDraft);
        model.LeaveCanvasExpansion();
        Assert.Equal(ScreenshotTool.ExpandCanvas, model.Tool);
        Assert.Null(model.ExpansionDraft);
        model.LeaveCanvasExpansion();
        Assert.Equal(ScreenshotTool.Select, model.Tool);
        Assert.Equal(new SizeD(40, 40), model.ImageSize);
    }

    [Fact]
    public void APastedImageLandsOnWholePixelsAndDragsInWholePixels()
    {
        var model = Model(40, 40);
        Assert.True(model.PasteImage(Image(31, 20)));
        var pasted = model.Annotations[0].Rect;
        Assert.Equal(R(4, 10, 31, 20), pasted);
        model.MoveOrigin = pasted;
        model.DragStart = P(10, 10);
        model.ContinueSelectDrag(P(12.3, 13.7));
        Assert.Equal(R(pasted.MinX() + 2, pasted.MinY() + 4, 31, 20), model.Annotations[0].Rect);
    }

    [Fact]
    public void AnImageTooBigForTheCanvasBudgetCannotBePasted()
    {
        var model = Model(40, 40);
        Assert.False(model.PasteImage(new ImageAsset(70_000, 1_000)));
        Assert.Empty(model.Annotations);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void PastingSwitchesToSelectAndSelectsTheLayer()
    {
        var model = Model(40, 40);
        model.Tool = ScreenshotTool.Text;
        Assert.True(model.PasteImage(Image(10, 10)));
        Assert.Equal(ScreenshotTool.Select, model.Tool);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
        Assert.Equal(ScreenshotTool.Image, model.Annotations[0].Tool);
    }

    [Fact]
    public void ALargerPasteShrinksToFitTheCanvas()
    {
        var model = Model(40, 40);
        Assert.True(model.PasteImage(Image(100, 50)));
        Assert.Equal(R(0, 10, 40, 20), model.Annotations[0].Rect);
    }
}

public class ScreenshotCanvasDragTests
{
    [Theory]
    [InlineData(ScreenshotTool.Arrow)]
    [InlineData(ScreenshotTool.Line)]
    public void ALineToolDrawsFromThePressToTheRelease(ScreenshotTool tool)
    {
        var model = Model();
        model.Tool = tool;
        model.BeginDrag(P(10, 10));
        Assert.Equal([P(10, 10), P(10, 10)], model.Annotations[0].Points);
        model.ContinueDrag(P(50, 60));
        Assert.Equal([P(10, 10), P(50, 60)], model.Annotations[0].Points);
        Assert.Empty(model.UndoStates);
        model.EndDrag(P(50, 60), isTap: false);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
        Assert.Single(model.UndoStates);
        Assert.Empty(model.UndoStates[0].Annotations);
        Assert.True(model.CanUndo);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void ANewArrowTakesTheCurrentColourStrokeAndStyle()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Arrow;
        model.Color = AnnotationColor.Blue;
        model.Stroke = StrokeWidth.Large;
        model.ArrowStyle = ArrowStyle.Open;
        model.BeginDrag(P(1, 1));
        var arrow = model.Annotations[0];
        Assert.Equal((AnnotationColor.Blue, StrokeWidth.Large, ArrowStyle.Open), (arrow.Color, arrow.Stroke, arrow.ArrowStyle));
    }

    [Fact]
    public void ADraggedScribblyArrowGetsASeed()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Arrow;
        model.ArrowStyle = ArrowStyle.Scribbly;
        model.BeginDrag(P(1, 1));
        Assert.NotEqual(0ul, model.Annotations[0].ScribbleSeed);
    }

    [Fact]
    public void TheFreehandToolAppendsEveryPoint()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Freehand;
        model.BeginDrag(P(5, 5));
        model.ContinueDrag(P(6, 7));
        model.ContinueDrag(P(9, 9));
        Assert.Equal([P(5, 5), P(6, 7), P(9, 9)], model.Annotations[0].Points);
        model.EndDrag(P(9, 9), isTap: false);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
    }

    [Fact]
    public void AClosedPenStrokeIsKeptWhenTheViewSaysItIsNotATap()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Freehand;
        model.BeginDrag(P(50, 50));
        model.ContinueDrag(P(80, 50));
        model.ContinueDrag(P(50, 50));
        model.EndDrag(P(50, 50), isTap: false);
        Assert.Single(model.Annotations);
    }

    [Theory]
    [InlineData(ScreenshotTool.Rect)]
    [InlineData(ScreenshotTool.Ellipse)]
    [InlineData(ScreenshotTool.Highlight)]
    [InlineData(ScreenshotTool.Pixelate)]
    [InlineData(ScreenshotTool.Redact)]
    [InlineData(ScreenshotTool.Spotlight)]
    public void AnAreaToolDrawsTheRectBetweenPressAndRelease(ScreenshotTool tool)
    {
        var model = Model();
        model.Tool = tool;
        model.BeginDrag(P(10, 10));
        Assert.Equal(R(10, 10, 0, 0), model.Annotations[0].Rect);
        model.ContinueDrag(P(30, 50));
        Assert.Equal(R(10, 10, 20, 40), model.Annotations[0].Rect);
        model.ContinueDrag(P(2, 4));
        Assert.Equal(R(2, 4, 8, 6), model.Annotations[0].Rect);
        model.EndDrag(P(2, 4), isTap: false);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
    }

    [Theory]
    [InlineData(ScreenshotTool.Arrow)]
    [InlineData(ScreenshotTool.Line)]
    [InlineData(ScreenshotTool.Rect)]
    [InlineData(ScreenshotTool.Ellipse)]
    [InlineData(ScreenshotTool.Highlight)]
    [InlineData(ScreenshotTool.Pixelate)]
    [InlineData(ScreenshotTool.Redact)]
    [InlineData(ScreenshotTool.Spotlight)]
    [InlineData(ScreenshotTool.Freehand)]
    public void ATapWithADrawingToolDiscardsTheDraftAndRecordsNothing(ScreenshotTool tool)
    {
        var model = Model();
        model.Tool = tool;
        model.BeginDrag(P(30, 30));
        model.EndDrag(P(30, 30), isTap: true);
        Assert.Empty(model.Annotations);
        Assert.Empty(model.UndoStates);
        Assert.False(model.IsDirty);
        Assert.Null(model.DraftId);
    }

    [Fact]
    public void ANewAreaMarkTakesTheBlurSettingsAndEnsuresItsSample()
    {
        var host = new RecordingHost();
        var model = new CanvasModel(Image(100, 100), 1, host: host);
        model.Tool = ScreenshotTool.Pixelate;
        model.BlurLevel = 5;
        model.BlurStyle = BlurStyle.Blur;
        model.BlurTextOnly = true;
        model.BeginDrag(P(1, 1));
        var area = model.Annotations[0];
        Assert.Equal((5, BlurStyle.Blur, true), (area.BlurLevel, area.BlurStyle, area.BlurTextOnly));
        Assert.Contains((BlurStyle.Blur, 5), host.EnsuredSamples);
    }

    // ---- Text, sticker, counter --------------------------------------------------------------------------------

    [Fact]
    public void ATapWithTheTextToolStartsAnEmptyNewTextAndTheInlineEditor()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(20, 30));
        model.EndDrag(P(20, 30), isTap: true);
        var text = model.Annotations.Single();
        Assert.Equal(ScreenshotTool.Text, text.Tool);
        Assert.Equal(P(20, 30), text.Rect.Origin());
        Assert.True(text.Rect.Width > 0 && text.Rect.Height > 0);
        Assert.Equal(text.Id, model.SelectedId);
        Assert.Equal(text.Id, model.EditingTextId);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ADragWithTheTextToolCreatesNothing()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(20, 30));
        model.EndDrag(P(60, 70), isTap: false);
        Assert.Empty(model.Annotations);
    }

    [Fact]
    public void CommittingANewTextRecordsOneStepThatUndoesTheWholeText()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(20, 30));
        model.EndDrag(P(20, 30), isTap: true);
        var id = model.Annotations[0].Id;
        model.CommitText(id, "  Hello  ");
        Assert.Equal("Hello", model.Annotations[0].Text);
        Assert.True(model.Annotations[0].Rect.Width > 4);
        Assert.Null(model.EditingTextId);
        Assert.Single(model.UndoStates);
        Assert.Empty(model.UndoStates[0].Annotations);
        model.Undo();
        Assert.Empty(model.Annotations);
    }

    [Fact]
    public void CommittingEmptyNewTextRemovesItWithoutHistory()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(20, 30));
        model.EndDrag(P(20, 30), isTap: true);
        model.CommitText(model.Annotations[0].Id, "   ");
        Assert.Empty(model.Annotations);
        Assert.Null(model.SelectedId);
        Assert.Empty(model.UndoStates);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void EditingExistingTextRecordsAStepOnlyWhenItChanges()
    {
        var model = Model();
        var text = new Annotation(ScreenshotTool.Text, R(10, 10, 40, 20), text: "Hi");
        model.Annotations = [text];
        model.CommitText(text.Id, "Hi");
        Assert.Empty(model.UndoStates);
        model.CommitText(text.Id, "Hello");
        Assert.Equal("Hello", model.Annotations[0].Text);
        Assert.Single(model.UndoStates);
        Assert.Equal("Hi", model.UndoStates[0].Annotations[0].Text);
    }

    [Fact]
    public void ClearingExistingTextRemovesItAsOneStep()
    {
        var model = Model();
        var text = new Annotation(ScreenshotTool.Text, R(10, 10, 40, 20), text: "Hi");
        model.Annotations = [text];
        model.SelectedId = text.Id;
        model.CommitText(text.Id, "");
        Assert.Empty(model.Annotations);
        Assert.Null(model.SelectedId);
        Assert.Single(model.UndoStates);
    }

    [Fact]
    public void ATapWithTheStickerToolPlacesAStickerCentredOnThePointAndSelectsIt()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Sticker;
        model.Sticker = StickerKind.Fire;
        model.BeginDrag(P(50, 50));
        model.EndDrag(P(50, 50), isTap: true);
        var sticker = model.Annotations.Single();
        Assert.Equal(R(27.5, 27.5, 45, 45), sticker.Rect);
        Assert.Equal("fire", sticker.Text);
        Assert.Equal(sticker.Id, model.SelectedId);
        Assert.Single(model.UndoStates);
    }

    [Fact]
    public void AStickerPlacedAtTheEdgeStaysInsideTheImage()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Sticker;
        model.BeginDrag(P(2, 2));
        model.EndDrag(P(2, 2), isTap: true);
        Assert.Equal(R(0, 0, 45, 45), model.Annotations[0].Rect);
    }

    [Fact]
    public void ATapOnAnExistingMarkSelectsItInsteadOfPlacingAnotherAndSwitchesToSelect()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Sticker;
        model.BeginDrag(P(50, 50));
        model.EndDrag(P(50, 50), isTap: true);
        model.SelectedId = null;
        model.BeginDrag(P(55, 55));
        model.EndDrag(P(55, 55), isTap: true);
        Assert.Single(model.Annotations);
        Assert.Equal(ScreenshotTool.Select, model.Tool);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
    }

    [Fact]
    public void ATapInsideTheVisibleSelectionKeepsEditingItWithTheCreationToolActive()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Sticker;
        model.BeginDrag(P(50, 50));
        model.EndDrag(P(50, 50), isTap: true);
        model.BeginDrag(P(55, 55));
        model.EndDrag(P(55, 55), isTap: true);
        Assert.Single(model.Annotations);
        Assert.Equal(ScreenshotTool.Sticker, model.Tool);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
    }

    [Fact]
    public void ATapOnExistingTextOpensItsEditor()
    {
        var model = Model();
        var text = new Annotation(ScreenshotTool.Text, R(10, 10, 40, 20), text: "Hi");
        model.Annotations = [text];
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(20, 15));
        model.EndDrag(P(20, 15), isTap: true);
        Assert.Single(model.Annotations);
        Assert.Equal(text.Id, model.EditingTextId);
        Assert.Equal(ScreenshotTool.Select, model.Tool);
    }

    [Fact]
    public void ATapInsideAShapeStillPlacesNewTextBecauseInteriorsStayFree()
    {
        var model = Model();
        model.Annotations = [Mark(ScreenshotTool.Rect, R(10, 10, 80, 80))];
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(50, 50));
        model.EndDrag(P(50, 50), isTap: true);
        Assert.Equal(2, model.Annotations.Count);
        Assert.Equal(ScreenshotTool.Text, model.Annotations[1].Tool);
    }

    [Fact]
    public void CountersNumberInCreationOrderAndRenumberOnDelete()
    {
        var model = Model(400, 400);
        model.Tool = ScreenshotTool.Counter;
        foreach (var point in new[] { P(50, 50), P(150, 50), P(250, 50) })
        {
            model.BeginDrag(point);
            model.EndDrag(point, isTap: true);
        }
        Assert.Equal([1, 2, 3], model.Annotations.Select(a => a.Number));
        model.SelectedId = model.Annotations[0].Id;
        model.DeleteSelected();
        Assert.Equal([1, 2], model.Annotations.Select(a => a.Number));
        Assert.Null(model.SelectedId);
    }

    [Fact]
    public void ATapNearAnExistingCounterSelectsItInsteadOfAddingOne()
    {
        var model = Model(400, 400);
        model.Tool = ScreenshotTool.Counter;
        model.BeginDrag(P(50, 50));
        model.EndDrag(P(50, 50), isTap: true);
        model.BeginDrag(P(60, 50));
        model.EndDrag(P(60, 50), isTap: true);
        Assert.Single(model.Annotations);
    }

    // ---- Delete and layer order --------------------------------------------------------------------------------

    [Fact]
    public void DeletingNeedsASelectionAndIsOneUndoStep()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(10, 10, 20, 20));
        model.Annotations = [rect];
        model.DeleteSelected();
        Assert.Single(model.Annotations);
        model.SelectedId = rect.Id;
        model.DeleteSelected();
        Assert.Empty(model.Annotations);
        Assert.Single(model.UndoStates);
        model.Undo();
        Assert.Equal([rect.Id], model.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void MovingTheSelectionThroughTheOrderRecordsOneStepAndRenumbersCounters()
    {
        var model = Model(400, 400);
        var first = new Annotation(ScreenshotTool.Counter, R(10, 10, 0, 0), number: 1);
        var second = new Annotation(ScreenshotTool.Counter, R(50, 10, 0, 0), number: 2);
        model.Annotations = [first, second];
        model.SelectedId = first.Id;
        Assert.True(model.CanMoveSelected(LayerMove.Forward));
        Assert.False(model.CanMoveSelected(LayerMove.Backward));
        model.MoveSelected(LayerMove.Forward);
        Assert.Equal([second.Id, first.Id], model.Annotations.Select(a => a.Id));
        Assert.Equal([1, 2], model.Annotations.Select(a => a.Number));
        Assert.Equal(2, model.Annotations[1].Number);
        Assert.Single(model.UndoStates);
        model.MoveSelected(LayerMove.Forward);
        Assert.Single(model.UndoStates);
        Assert.False(model.CanMoveSelected(LayerMove.Forward));
    }

    [Fact]
    public void LayerControlsAppearOnlyWithAnotherShapeToPass()
    {
        var model = Model();
        Assert.False(model.ShowsLayerControls);
        model.Annotations = [Mark(ScreenshotTool.Rect, R(1, 1, 5, 5))];
        Assert.False(model.ShowsLayerControls);
        model.Annotations = [Mark(ScreenshotTool.Rect, R(1, 1, 5, 5)), Mark(ScreenshotTool.Rect, R(9, 9, 5, 5))];
        Assert.True(model.ShowsLayerControls);
    }

    [Fact]
    public void AShapeBeingDrawnDoesNotCountForTheLayerControlsUntilItLands()
    {
        var model = Model();
        model.Annotations = [Mark(ScreenshotTool.Rect, R(1, 1, 5, 5))];
        model.Tool = ScreenshotTool.Rect;
        model.BeginDrag(P(50, 50));
        Assert.False(model.ShowsLayerControls);
        model.ContinueDrag(P(60, 60));
        model.EndDrag(P(60, 60), isTap: false);
        Assert.True(model.ShowsLayerControls);
    }

    // ---- Hit testing ------------------------------------------------------------------------------------------

    [Fact]
    public void ALineAnswersWithinTheToleranceAndHalfItsStroke()
    {
        var model = Model();
        var arrow = new Annotation(ScreenshotTool.Arrow, points: [P(10, 10), P(90, 10)], stroke: StrokeWidth.Medium);
        model.Annotations = [arrow];
        Assert.Equal(arrow.Id, model.HitTest(P(50, 22)));
        Assert.Null(model.HitTest(P(50, 22.5)));
        Assert.Equal(arrow.Id, model.HitTest(P(2, 10)));
        Assert.Null(model.HitTest(P(-3, 10)));
    }

    [Fact]
    public void ATwoPointShapeWithFewerPointsIsNeverHit()
    {
        var model = Model();
        model.Annotations = [new Annotation(ScreenshotTool.Line, points: [P(10, 10)])];
        Assert.Null(model.HitTest(P(10, 10)));
    }

    [Fact]
    public void AStrokeWidthWidensTheHitBand()
    {
        var model = Model();
        var thick = new Annotation(ScreenshotTool.Line, points: [P(10, 10), P(90, 10)], stroke: StrokeWidth.Large);
        model.Annotations = [thick];
        Assert.Equal(thick.Id, model.HitTest(P(50, 23.5)));
        Assert.Null(model.HitTest(P(50, 24)));
    }

    [Fact]
    public void AFreehandStrokeAnswersNearAnyOfItsPoints()
    {
        var model = Model();
        var pen = new Annotation(ScreenshotTool.Freehand, points: [P(10, 10), P(20, 20), P(30, 30)]);
        model.Annotations = [pen];
        Assert.Equal(pen.Id, model.HitTest(P(35, 30)));
        Assert.Null(model.HitTest(P(40, 45)));
    }

    [Fact]
    public void AnAreaShapeAnswersInsideAndNearItsEdge()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(20, 20, 40, 40));
        model.Annotations = [rect];
        Assert.Equal(rect.Id, model.HitTest(P(40, 40)));
        Assert.Equal(rect.Id, model.HitTest(P(16, 40)));
        Assert.Null(model.HitTest(P(14, 40)));
    }

    [Fact]
    public void CreationTapsSeeAnAreaShapeOnlyAtItsEdgeRing()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(20, 20, 40, 40));
        model.Annotations = [rect];
        Assert.Null(model.HitTest(P(40, 40), includeShapeInteriors: false));
        Assert.Equal(rect.Id, model.HitTest(P(22, 40), includeShapeInteriors: false));
        Assert.Equal(rect.Id, model.HitTest(P(58, 40), includeShapeInteriors: false));
    }

    [Fact]
    public void AShapeTooSmallForAnInteriorStaysFullyTappable()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(20, 20, 15, 15));
        model.Annotations = [rect];
        Assert.Equal(rect.Id, model.HitTest(P(27, 27), includeShapeInteriors: false));
    }

    [Fact]
    public void TheTopmostMarkWins()
    {
        var model = Model();
        var below = Mark(ScreenshotTool.Rect, R(10, 10, 60, 60));
        var above = Mark(ScreenshotTool.Highlight, R(30, 30, 20, 20));
        model.Annotations = [below, above];
        Assert.Equal(above.Id, model.HitTest(P(40, 40)));
        Assert.Equal(below.Id, model.HitTest(P(15, 15)));
    }

    [Fact]
    public void TextAndStickersAnswerWithinHalfTheTolerance()
    {
        var model = Model();
        var text = new Annotation(ScreenshotTool.Text, R(20, 20, 30, 10));
        model.Annotations = [text];
        Assert.Equal(text.Id, model.HitTest(P(16, 25), includeShapeInteriors: false));
        Assert.Null(model.HitTest(P(14, 25)));
        var sticker = new Annotation(ScreenshotTool.Sticker, R(60, 60, 20, 20));
        model.Annotations = [sticker];
        Assert.Equal(sticker.Id, model.HitTest(P(70, 70), includeShapeInteriors: false));
    }

    [Fact]
    public void AnImageLayerAnswersOnlyWhenInteriorsCount()
    {
        var model = Model();
        var layer = new Annotation(ScreenshotTool.Image, R(20, 20, 40, 40), imageAsset: Image(4, 4));
        model.Annotations = [layer];
        Assert.Equal(layer.Id, model.HitTest(P(40, 40)));
        Assert.Null(model.HitTest(P(40, 40), includeShapeInteriors: false));
    }

    [Fact]
    public void ACounterAnswersWithinItsBadgeRadiusPlusFour()
    {
        var model = Model();
        var counter = new Annotation(ScreenshotTool.Counter, R(50, 50, 0, 0), number: 1);
        model.Annotations = [counter];
        Assert.Equal(counter.Id, model.HitTest(P(64, 50)));
        Assert.Null(model.HitTest(P(66, 50)));
    }

    [Fact]
    public void TheToleranceScalesWithTheCaptureScale()
    {
        var model = Model(200, 200, scale: 2);
        var arrow = new Annotation(ScreenshotTool.Line, points: [P(10, 10), P(90, 10)], stroke: StrokeWidth.Medium);
        model.Annotations = [arrow];
        // 10 * 2 + 4 * 2 / 2 = 24.
        Assert.Equal(arrow.Id, model.HitTest(P(50, 34)));
        Assert.Null(model.HitTest(P(50, 35)));
    }

    [Theory]
    [InlineData(ScreenshotTool.Select)]
    [InlineData(ScreenshotTool.Crop)]
    [InlineData(ScreenshotTool.ExpandCanvas)]
    public void ToolsThatMakeNoMarkAreNeverHit(ScreenshotTool tool)
    {
        var model = Model();
        model.Annotations = [new Annotation(tool, R(10, 10, 50, 50))];
        Assert.Null(model.HitTest(P(30, 30)));
    }

    // ---- The select tool ---------------------------------------------------------------------------------------

    private static (CanvasModel Model, Annotation Rect) SelectedRect()
    {
        var model = Model(200, 200);
        var rect = Mark(ScreenshotTool.Rect, R(20, 20, 40, 40));
        model.Annotations = [rect];
        model.Tool = ScreenshotTool.Select;
        model.SelectedId = rect.Id;
        return (model, rect);
    }

    [Fact]
    public void DraggingTheSelectionMovesItAndRecordsOneStep()
    {
        var (model, rect) = SelectedRect();
        model.BeginDrag(P(40, 40));
        model.ContinueDrag(P(50, 45));
        model.ContinueDrag(P(60, 50));
        Assert.Equal(R(40, 30, 40, 40), model.Annotations[0].Rect);
        model.EndDrag(P(60, 50), isTap: false);
        Assert.Single(model.UndoStates);
        Assert.Equal(rect.Rect, model.UndoStates[0].Annotations[0].Rect);
    }

    [Fact]
    public void DraggingAHandleResizesTheSelection()
    {
        var (model, _) = SelectedRect();
        model.BeginDrag(P(60, 60));
        Assert.Equal(SelectionHandle.BottomRight, model.ActiveHandle);
        model.ContinueDrag(P(80, 90));
        Assert.Equal(R(20, 20, 60, 70), model.Annotations[0].Rect);
        model.EndDrag(P(80, 90), isTap: false);
        Assert.Null(model.ActiveHandle);
    }

    [Fact]
    public void DraggingAHandleAcrossTheOppositeEdgeFlipsTheSelection()
    {
        var (model, _) = SelectedRect();
        model.BeginDrag(P(60, 60));
        model.ContinueDrag(P(10, 10));
        Assert.Equal(R(10, 10, 10, 10), model.Annotations[0].Rect);
    }

    [Fact]
    public void DraggingALineEndMovesJustThatEnd()
    {
        var model = Model(200, 200);
        var line = new Annotation(ScreenshotTool.Line, points: [P(10, 10), P(50, 50)]);
        model.Annotations = [line];
        model.SelectedId = line.Id;
        model.Tool = ScreenshotTool.Select;
        model.SelectedId = line.Id;
        model.BeginDrag(P(52, 52));
        model.ContinueDrag(P(70, 40));
        Assert.Equal([P(10, 10), P(70, 40)], model.Annotations[0].Points);
        model.EndDrag(P(70, 40), isTap: false);
        model.BeginDrag(P(11, 11));
        model.ContinueDrag(P(0, 0));
        Assert.Equal([P(0, 0), P(70, 40)], model.Annotations[0].Points);
    }

    [Fact]
    public void DraggingALineBodyMovesBothEnds()
    {
        var model = Model(200, 200);
        var line = new Annotation(ScreenshotTool.Line, points: [P(10, 10), P(90, 10)]);
        model.Annotations = [line];
        model.Tool = ScreenshotTool.Select;
        model.BeginDrag(P(50, 12));
        Assert.Equal(line.Id, model.SelectedId);
        model.ContinueDrag(P(60, 32));
        Assert.Equal([P(20, 30), P(100, 30)], model.Annotations[0].Points);
    }

    [Fact]
    public void PressingAnUnselectedMarkSelectsItAndSyncsTheControls()
    {
        var model = Model(200, 200);
        var rect = new Annotation(ScreenshotTool.Rect, R(20, 20, 40, 40), color: AnnotationColor.Blue, stroke: StrokeWidth.Large);
        model.Annotations = [rect];
        model.Tool = ScreenshotTool.Select;
        model.BeginDrag(P(40, 40));
        Assert.Equal(rect.Id, model.SelectedId);
        Assert.Equal(AnnotationColor.Blue, model.Color);
        Assert.Equal(StrokeWidth.Large, model.Stroke);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ATapOnEmptyCanvasClearsTheSelection()
    {
        var (model, _) = SelectedRect();
        model.BeginDrag(P(150, 150));
        model.EndDrag(P(150, 150), isTap: true);
        Assert.Null(model.SelectedId);
    }

    [Fact]
    public void ATapOnATextMarkWithSelectOpensItsEditor()
    {
        var model = Model(200, 200);
        var text = new Annotation(ScreenshotTool.Text, R(20, 20, 40, 20), text: "Hi", textSize: 36);
        model.Annotations = [text];
        model.Tool = ScreenshotTool.Select;
        model.BeginDrag(P(30, 30));
        model.EndDrag(P(30, 30), isTap: true);
        Assert.Equal(text.Id, model.SelectedId);
        Assert.Equal(text.Id, model.EditingTextId);
        Assert.Equal(36, model.TextSize);
    }

    [Fact]
    public void ASelectionDragNeverRegistersUndoForTheWordSelection()
    {
        var model = Model(200, 100);
        model.Tool = ScreenshotTool.Select;
        Assert.True(model.ApplyRecognition(model.BaseImage,
            [new RecognizedWord("ola", R(0, 0, 40, 10), 0), new RecognizedWord("mundo", R(50, 0, 40, 10), 0)], []));
        model.BeginDrag(P(5, 5));
        Assert.Equal([0], model.SelectedWordIndexes);
        model.ContinueDrag(P(60, 5));
        Assert.Equal([0, 1], model.SelectedWordIndexes);
        Assert.Equal("ola mundo", model.SelectedText);
        model.EndDrag(P(60, 5), isTap: false);
        Assert.Empty(model.UndoStates);
        model.ClearTextSelection();
        Assert.Empty(model.SelectedWordIndexes);
    }

    [Fact]
    public void ARecognizedWordUnderTheCursorReadsAsText()
    {
        var model = Model(200, 100);
        model.Tool = ScreenshotTool.Select;
        model.ApplyRecognition(model.BaseImage, [new RecognizedWord("ola", R(10, 10, 40, 10), 0)], []);
        Assert.Equal(CanvasCursor.IBeam, model.CursorAt(P(20, 15)));
        Assert.Equal(CanvasCursor.Arrow, model.CursorAt(P(150, 80)));
    }

    // ---- Spotlight editing (ScreenshotSpotlightEditingTests) ----------------------------------------------------

    private static readonly RectD SpotRect = R(40, 20, 120, 60);

    private static CanvasModel SpotModel(ScreenshotTool tool, ScreenshotTool selected)
    {
        var model = Model(200, 100);
        model.Tool = tool;
        var mark = Mark(selected, SpotRect);
        model.Annotations = [mark];
        model.SelectedId = mark.Id;
        return model;
    }

    [Fact]
    public void TheSpotlightToolResizesAFreshRegionFromItsHandlesAndMovesItByItsEdge()
    {
        var corner = SpotModel(ScreenshotTool.Spotlight, ScreenshotTool.Spotlight);
        corner.BeginDrag(P(SpotRect.MinX(), SpotRect.MinY()));
        var edge = SpotModel(ScreenshotTool.Spotlight, ScreenshotTool.Spotlight);
        edge.BeginDrag(P(SpotRect.MinX(), SpotRect.MinY() + 15));
        Assert.True(corner.EditingSelectedAnnotation);
        Assert.Single(corner.Annotations);
        Assert.Equal(SelectionHandle.TopLeft, corner.ActiveHandle);
        Assert.True(edge.EditingSelectedAnnotation);
        Assert.Single(edge.Annotations);
    }

    [Fact]
    public void ADragInsideAFreshSpotlightRegionStartsAnotherRegion()
    {
        var inside = SpotModel(ScreenshotTool.Spotlight, ScreenshotTool.Spotlight);
        inside.BeginDrag(P(SpotRect.MidX, SpotRect.MidY()));
        Assert.False(inside.EditingSelectedAnnotation);
        Assert.Equal(2, inside.Annotations.Count);
        Assert.Null(inside.SelectedId);
        Assert.Empty(inside.UndoStates);
        Assert.Equal(ScreenshotTool.Spotlight, inside.Annotations[^1].Tool);
        Assert.Equal(inside.Annotations[^1].Id, inside.DraftId);
    }

    [Fact]
    public void OtherRectangleToolsStillMoveAFreshMarkFromInsideIt()
    {
        var box = SpotModel(ScreenshotTool.Rect, ScreenshotTool.Rect);
        box.BeginDrag(P(SpotRect.MidX, SpotRect.MidY()));
        Assert.True(box.EditingSelectedAnnotation);
        Assert.Single(box.Annotations);
    }

    [Fact]
    public void ExpandingTheCanvasNeverMovesAPickedMarkAndTheHoverNeverOffersTo()
    {
        var expanding = SpotModel(ScreenshotTool.ExpandCanvas, ScreenshotTool.Rect);
        var inside = P(SpotRect.MidX, SpotRect.MidY());
        expanding.BeginDrag(inside);
        Assert.False(expanding.EditingSelectedAnnotation);
        Assert.Single(expanding.Annotations);
        Assert.Equal(R(0, 0, 200, 100), expanding.ExpansionDraft);
        Assert.Equal(CanvasCursor.Crosshair, expanding.CursorAt(inside));
    }

    [Fact]
    public void TheSpotlightHoverOffersAFreshRegionsHandlesAndEdgeAndDrawsInside()
    {
        var hovered = SpotModel(ScreenshotTool.Spotlight, ScreenshotTool.Spotlight);
        Assert.Equal(CanvasCursor.Crosshair, hovered.CursorAt(P(SpotRect.MidX, SpotRect.MidY())));
        Assert.Equal(CanvasCursor.OpenHand, hovered.CursorAt(P(SpotRect.MinX(), SpotRect.MinY())));
        Assert.Equal(CanvasCursor.OpenHand, hovered.CursorAt(P(SpotRect.MinX(), SpotRect.MinY() + 15)));
        var box = SpotModel(ScreenshotTool.Rect, ScreenshotTool.Rect);
        Assert.Equal(CanvasCursor.OpenHand, box.CursorAt(P(SpotRect.MidX, SpotRect.MidY())));
    }

    [Fact]
    public void APickedSpotlightRegionShowsTheDimControlInsteadOfColourAndThickness()
    {
        var pickedRegion = SpotModel(ScreenshotTool.Select, ScreenshotTool.Spotlight);
        var pickedBox = SpotModel(ScreenshotTool.Select, ScreenshotTool.Rect);
        var corner = SpotModel(ScreenshotTool.Spotlight, ScreenshotTool.Spotlight);
        corner.BeginDrag(P(SpotRect.MinX(), SpotRect.MinY()));
        var nothing = Model();
        nothing.Tool = ScreenshotTool.Select;
        Assert.False(pickedRegion.ShowsColorControls);
        Assert.True(pickedRegion.ShowsSpotlightControls);
        Assert.True(pickedBox.ShowsColorControls);
        Assert.False(pickedBox.ShowsSpotlightControls);
        Assert.True(corner.ShowsSpotlightControls);
        Assert.False(corner.ShowsColorControls);
        Assert.False(nothing.ShowsSpotlightControls);
    }

    [Fact]
    public void ReplacingAnImageInTheSameAnnotationIsDirty()
    {
        var model = Model();
        var mark = new Annotation(ScreenshotTool.Image, imageAsset: Image(2, 2));
        model.Annotations = [mark];
        model.SetCleanAnnotations([mark]);
        model.RefreshDirtyState();
        Assert.False(model.IsDirty);
        model.Annotations = [mark with { ImageAsset = Image(2, 2) }];
        model.RefreshDirtyState();
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void MovingTheDimMarksTheEditChangedOnlyWhenASpotlightRegionExists()
    {
        var empty = Model();
        empty.SpotlightDimStrength = 0.8;
        Assert.False(empty.IsDirty);
        var lit = Model();
        var region = new Annotation(ScreenshotTool.Spotlight, SpotRect);
        lit.Annotations = [region];
        lit.SetCleanAnnotations([region]);
        lit.RefreshDirtyState();
        Assert.False(lit.IsDirty);
        lit.SpotlightDimStrength = 0.8;
        Assert.True(lit.IsDirty);
        lit.SpotlightDimStrength = 0.5;
        Assert.False(lit.IsDirty);
    }

    [Fact]
    public void MovingTheDimRemembersItForTheNextCapture()
    {
        var model = Model();
        var remembered = new Dictionary<string, object>();
        model.PreferenceChanged += (key, value) => remembered[key] = value;
        model.SpotlightDimStrength = 0.35;
        Assert.Equal(0.35, remembered[DefaultsKey.ScreenshotSpotlightDim]);
    }

    [Fact]
    public void TheContinuousDimSliderSettlesOnTheNearestFivePercentStep()
    {
        var model = Model();
        model.SetSpotlightDimStepped(0.52);
        Assert.Equal(0.5, model.SpotlightDimStrength);
        model.SetSpotlightDimStepped(0.68);
        Assert.Equal(0.7, model.SpotlightDimStrength);
        model.SetSpotlightDimStepped(1.3);
        Assert.Equal(1, model.SpotlightDimStrength);
    }
}

public class ScreenshotCanvasCropTests
{
    private static CanvasModel Cropping(RecordingHost? host = null)
    {
        var model = new CanvasModel(Image(100, 100), 1, host: host);
        model.Tool = ScreenshotTool.Crop;
        return model;
    }

    [Fact]
    public void TheCropToolOpensWithTheFullImageAndClearsTheSelection()
    {
        var model = Model();
        var mark = Mark(ScreenshotTool.Rect, R(1, 1, 5, 5));
        model.Annotations = [mark];
        model.SelectedId = mark.Id;
        model.Tool = ScreenshotTool.Crop;
        Assert.Equal(R(0, 0, 100, 100), model.CropDraft);
        Assert.Null(model.SelectedId);
    }

    [Fact]
    public void LeavingTheCropToolDropsTheDraft()
    {
        var model = Cropping();
        model.Tool = ScreenshotTool.Select;
        Assert.Null(model.CropDraft);
        Assert.Null(model.CropLoupePoint);
    }

    [Fact]
    public void DraggingInsideTheFullCropStartsANewSnappedSelection()
    {
        var model = Cropping();
        model.BeginDrag(P(20, 10));
        model.ContinueDrag(P(60, 70));
        Assert.Equal(R(20, 10, 40, 60), model.CropDraft);
        model.ContinueDrag(P(60.4, 70.6));
        Assert.Equal(R(20, 10, 40, 61), model.CropDraft);
        model.EndDrag(P(60.4, 70.6), isTap: false);
        Assert.Equal(R(20, 10, 40, 61), model.CropDraft);
    }

    [Fact]
    public void ATapWhileStartingANewSelectionRestoresTheDraft()
    {
        var model = Cropping();
        model.BeginDrag(P(20, 10));
        model.ContinueDrag(P(60, 70));
        model.EndDrag(P(60, 70), isTap: false);
        model.BeginDrag(P(90, 90));
        model.ContinueDrag(P(95, 95));
        Assert.Equal(R(90, 90, 5, 5), model.CropDraft);
        model.EndDrag(P(95, 95), isTap: true);
        Assert.Equal(R(20, 10, 40, 60), model.CropDraft);
    }

    [Fact]
    public void DraggingACropHandleResizesOnPixelBoundariesAndShowsTheLoupe()
    {
        var model = Cropping();
        model.BeginDrag(P(20, 10));
        model.ContinueDrag(P(60, 70));
        model.EndDrag(P(60, 70), isTap: false);
        model.BeginDrag(P(20, 10));
        Assert.Equal(P(20, 10), model.CropLoupePoint);
        model.ContinueDrag(P(30.4, 15.2));
        Assert.Equal(R(30, 15, 30, 55), model.CropDraft);
        Assert.Equal(P(30, 15), model.CropLoupePoint);
        model.EndDrag(P(30.4, 15.2), isTap: false);
        Assert.Null(model.CropLoupePoint);
    }

    [Fact]
    public void DraggingInsideAnAdjustedCropMovesItAndStopsAtTheImageEdges()
    {
        var model = Cropping();
        model.BeginDrag(P(20, 10));
        model.ContinueDrag(P(60, 70));
        model.EndDrag(P(60, 70), isTap: false);
        model.BeginDrag(P(40, 40));
        model.ContinueDrag(P(50, 45));
        Assert.Equal(R(30, 15, 40, 60), model.CropDraft);
        model.ContinueDrag(P(200, 200));
        Assert.Equal(R(60, 40, 40, 60), model.CropDraft);
        Assert.Null(model.CropLoupePoint);
    }

    [Fact]
    public void ACropMovesEveryMarkAndShrinksTheCanvas()
    {
        var host = new RecordingHost();
        var model = Cropping(host);
        model.Tool = ScreenshotTool.Select;
        model.Annotations =
        [
            Mark(ScreenshotTool.Rect, R(30, 30, 10, 10)),
            Mark(ScreenshotTool.Arrow, points: [P(25, 15), P(55, 65)]),
        ];
        model.Tool = ScreenshotTool.Crop;
        model.BeginDrag(P(20, 10));
        model.ContinueDrag(P(60, 70));
        model.EndDrag(P(60, 70), isTap: false);
        model.ApplyCrop();
        Assert.Equal(new SizeD(40, 60), model.ImageSize);
        Assert.Equal(R(10, 20, 10, 10), model.Annotations[0].Rect);
        Assert.Equal([P(5, 5), P(35, 55)], model.Annotations[1].Points);
        Assert.Equal(ScreenshotTool.Select, model.Tool);
        Assert.Null(model.CropDraft);
        Assert.Null(model.SelectedId);
        Assert.Single(model.UndoStates);
        Assert.Equal(R(20, 10, 40, 60), host.Crops.Single());
        Assert.Equal(1, host.TextRecognitions);
        Assert.Equal(1, host.SampleResets);
    }

    [Fact]
    public void ACropIsUndoneBackToTheWholePicture()
    {
        var model = Cropping();
        var original = model.BaseImage;
        model.BeginDrag(P(20, 10));
        model.ContinueDrag(P(60, 70));
        model.EndDrag(P(60, 70), isTap: false);
        model.ApplyCrop();
        Assert.False(ReferenceEquals(original, model.BaseImage));
        model.Undo();
        Assert.Same(original, model.BaseImage);
        Assert.False(model.IsDirty);
    }

    [Theory]
    [InlineData(10, 10, 7, 50)]
    [InlineData(10, 10, 50, 7)]
    public void ACropUnderEightPixelsASideLeavesTheToolWithoutCutting(double x, double y, double w, double h)
    {
        var host = new RecordingHost();
        var model = Cropping(host);
        model.CropDraft = R(x, y, w, h);
        model.ApplyCrop();
        Assert.Empty(host.Crops);
        Assert.Equal(ScreenshotTool.Select, model.Tool);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ApplyingWithoutADraftJustLeavesTheTool()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Rect;
        model.ApplyCrop();
        Assert.Equal(ScreenshotTool.Select, model.Tool);
    }

    [Fact]
    public void ACropTheHostCannotMakeKeepsTheDocument()
    {
        var host = new RecordingHost { CropSucceeds = false };
        var model = Cropping(host);
        var image = model.BaseImage;
        model.CropDraft = R(10, 10, 50, 50);
        model.ApplyCrop();
        Assert.Same(image, model.BaseImage);
        Assert.Empty(model.UndoStates);
        Assert.Equal(ScreenshotTool.Select, model.Tool);
    }

    [Fact]
    public void ACropMovesTheRecognizedWordsAndRunsAndDropsThoseOutsideIt()
    {
        var model = Cropping();
        model.ApplyRecognition(model.BaseImage,
        [
            new RecognizedWord("in", R(30, 30, 10, 10), 0),
            new RecognizedWord("out", R(80, 80, 10, 10), 1),
        ], [R(28, 28, 14, 14), R(78, 78, 14, 14)]);
        model.CropDraft = R(20, 20, 40, 40);
        model.ApplyCrop();
        Assert.Equal(["in"], model.TextWords.Select(w => w.Text));
        Assert.Equal(R(10, 10, 10, 10), model.TextWords[0].Rect);
        Assert.Equal([R(8, 8, 14, 14)], model.TextRuns);
        // Recognition of the cropped picture later keeps the carried runs next to what it reads.
        Assert.True(model.ApplyRecognition(model.BaseImage, [], [R(1, 1, 2, 2)]));
        Assert.Equal([R(1, 1, 2, 2), R(8, 8, 14, 14)], model.TextRuns);
    }
}

public class ScreenshotCanvasExpandDragTests
{
    private static CanvasModel Expanding(RecordingHost? host = null)
    {
        var model = new CanvasModel(Image(40, 40), 1, host: host) { CurrentDisplayZoom = 1 };
        model.Tool = ScreenshotTool.ExpandCanvas;
        return model;
    }

    [Fact]
    public void TheGripReachGrowsWhenZoomedOutSoSmallScreenGripsStillLand()
    {
        var model = new CanvasModel(Image(400, 400), 1) { CurrentDisplayZoom = 0.1 };
        model.Tool = ScreenshotTool.ExpandCanvas;
        // 10 screen points at 0.1x is 100 image points: a press 40 below the bottom edge still picks the bottom grip.
        model.BeginDrag(P(200, 440));
        model.ContinueDrag(P(200, 480));
        Assert.Equal(R(0, 0, 400, 440), model.ExpansionDraft);
    }

    [Fact]
    public void DraggingTheBottomGripGrowsTheCanvasWhenReleased()
    {
        var host = new RecordingHost();
        var model = Expanding(host);
        model.BeginDrag(P(20, 40));
        model.ContinueDrag(P(20, 60));
        Assert.Equal(R(0, 0, 40, 60), model.ExpansionDraft);
        Assert.False(model.ExpansionRejected);
        model.EndDrag(P(20, 60), isTap: false);
        Assert.Equal(new SizeD(40, 60), model.ImageSize);
        Assert.Null(model.ExpansionDraft);
        Assert.Single(model.UndoStates);
        Assert.Equal(0, host.Beeps);
    }

    [Fact]
    public void DraggingTheTopLeftGripMovesTheOldContentDownAndRight()
    {
        var model = Expanding();
        model.Annotations = [Mark(ScreenshotTool.Rect, R(5, 5, 10, 10))];
        model.BeginDrag(P(0, 0));
        model.ContinueDrag(P(-10, -20));
        model.EndDrag(P(-10, -20), isTap: false);
        Assert.Equal(new SizeD(50, 60), model.ImageSize);
        Assert.Equal(R(15, 25, 10, 10), model.Annotations[0].Rect);
    }

    [Fact]
    public void ADragFromTheInteriorExpandsNothing()
    {
        var model = Expanding();
        model.BeginDrag(P(20, 20));
        Assert.Equal(R(0, 0, 40, 40), model.ExpansionDraft);
        model.ContinueDrag(P(30, 30));
        model.EndDrag(P(30, 30), isTap: false);
        Assert.Equal(new SizeD(40, 40), model.ImageSize);
        Assert.Empty(model.UndoStates);
        Assert.Null(model.ExpansionDraft);
    }

    [Fact]
    public void AnInwardDragCommitsNothing()
    {
        var model = Expanding();
        model.BeginDrag(P(20, 40));
        model.ContinueDrag(P(20, 10));
        model.EndDrag(P(20, 10), isTap: false);
        Assert.Equal(new SizeD(40, 40), model.ImageSize);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ADragPastTheCanvasBudgetIsRejectedWithABeep()
    {
        var host = new RecordingHost();
        var model = Expanding(host);
        model.BeginDrag(P(20, 40));
        model.ContinueDrag(P(20, 1_000_000_000));
        Assert.True(model.ExpansionRejected);
        Assert.Null(model.ExpansionDraft);
        model.EndDrag(P(20, 1_000_000_000), isTap: false);
        Assert.Equal(1, host.Beeps);
        Assert.Equal(new SizeD(40, 40), model.ImageSize);
        Assert.False(model.ExpansionRejected);
    }

    [Fact]
    public void LeavingTheExpandToolCancelsTheDraft()
    {
        var model = Expanding();
        model.BeginDrag(P(20, 40));
        model.ContinueDrag(P(20, 60));
        model.Tool = ScreenshotTool.Rect;
        Assert.Null(model.ExpansionDraft);
    }

    [Fact]
    public void UndoCancelsAnExpansionDraftInFlight()
    {
        var model = Expanding();
        model.Tool = ScreenshotTool.Select;
        model.Annotations = [Mark(ScreenshotTool.Rect, R(1, 1, 5, 5))];
        model.SelectedId = model.Annotations[0].Id;
        model.DeleteSelected();
        model.Tool = ScreenshotTool.ExpandCanvas;
        model.BeginDrag(P(20, 40));
        model.Undo();
        Assert.Null(model.ExpansionDraft);
    }
}

public class ScreenshotCanvasStyleSelectionTests
{
    private static (CanvasModel Model, RecordingHost Host) WithMark(Annotation mark)
    {
        var host = new RecordingHost();
        var model = new CanvasModel(Image(100, 100), 1, host: host);
        model.Annotations = [mark];
        model.SelectedId = mark.Id;
        return (model, host);
    }

    [Fact]
    public void ChangingTheColourEditsTheSelectionAsOneUndoStep()
    {
        var (model, _) = WithMark(Mark(ScreenshotTool.Rect, R(10, 10, 20, 20)));
        model.Color = AnnotationColor.Green;
        Assert.Equal(AnnotationColor.Green, model.Annotations[0].Color);
        Assert.Single(model.UndoStates);
        Assert.Equal(AnnotationColor.Red, model.UndoStates[0].Annotations[0].Color);
        model.Color = AnnotationColor.Green;
        Assert.Single(model.UndoStates);
    }

    [Fact]
    public void ChangingTheStrokeEditsMarksThatHaveAThickness()
    {
        var (model, _) = WithMark(Mark(ScreenshotTool.Ellipse, R(10, 10, 20, 20)));
        model.Stroke = StrokeWidth.Large;
        Assert.Equal(StrokeWidth.Large, model.Annotations[0].Stroke);
        Assert.Single(model.UndoStates);
    }

    [Fact]
    public void PickingAMarkWithoutAThicknessNeverRecordsAThicknessEdit()
    {
        var (model, _) = WithMark(Mark(ScreenshotTool.Highlight, R(10, 10, 20, 20)));
        model.Stroke = StrokeWidth.Large;
        Assert.Equal(StrokeWidth.Medium, model.Annotations[0].Stroke);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ChangingTheTextSizeResizesTheTextBox()
    {
        var (model, _) = WithMark(new Annotation(ScreenshotTool.Text, R(10, 10, 40, 22), text: "Hi", textSize: 19));
        var before = model.Annotations[0].Rect;
        model.TextSize = 48;
        Assert.Equal(48, model.Annotations[0].TextSize);
        Assert.True(model.Annotations[0].Rect.Height > before.Height);
        Assert.Equal(before.Origin(), model.Annotations[0].Rect.Origin());
    }

    [Fact]
    public void AStyleChangeOnABrandNewTextIsFoldedIntoItsCreationStep()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Text;
        model.BeginDrag(P(10, 10));
        model.EndDrag(P(10, 10), isTap: true);
        model.Color = AnnotationColor.Purple;
        Assert.Equal(AnnotationColor.Purple, model.Annotations[0].Color);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ChangingTheArrowStyleOnlyAffectsArrowsAndASeedAppearsForScribbly()
    {
        var (model, _) = WithMark(Mark(ScreenshotTool.Arrow, points: [P(1, 1), P(50, 50)]));
        model.ArrowStyle = ArrowStyle.Scribbly;
        Assert.Equal(ArrowStyle.Scribbly, model.Annotations[0].ArrowStyle);
        Assert.NotEqual(0ul, model.Annotations[0].ScribbleSeed);
        var (line, _) = WithMark(Mark(ScreenshotTool.Line, points: [P(1, 1), P(50, 50)]));
        line.ArrowStyle = ArrowStyle.Open;
        Assert.Equal(ArrowStyle.Filled, line.Annotations[0].ArrowStyle);
        Assert.Empty(line.UndoStates);
    }

    [Fact]
    public void ImageLayersIgnoreStyleChanges()
    {
        var (model, _) = WithMark(new Annotation(ScreenshotTool.Image, R(0, 0, 10, 10), imageAsset: Image(2, 2)));
        model.Color = AnnotationColor.Blue;
        Assert.Equal(AnnotationColor.Red, model.Annotations[0].Color);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ChangingTheBlurSettingsEditsAPickedBlurAreaAndEnsuresTheSample()
    {
        var (model, host) = WithMark(new Annotation(ScreenshotTool.Pixelate, R(10, 10, 30, 30)));
        model.BlurLevel = 5;
        Assert.Equal(5, model.Annotations[0].BlurLevel);
        Assert.Contains((BlurStyle.Pixelate, 5), host.EnsuredSamples);
        Assert.Single(model.UndoStates);
        model.BlurStyle = BlurStyle.Erase;
        model.BlurTextOnly = true;
        Assert.Equal((BlurStyle.Erase, true), (model.Annotations[0].BlurStyle, model.Annotations[0].BlurTextOnly));
        Assert.Equal(3, model.UndoStates.Count);
    }

    [Fact]
    public void AnUnchangedBlurSettingRecordsNothing()
    {
        var (model, _) = WithMark(new Annotation(ScreenshotTool.Pixelate, R(10, 10, 30, 30)));
        model.BlurLevel = 3;
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void ABlurSampleThatCannotBeMadeLeavesTheAreaAndRevertsTheControls()
    {
        var (model, host) = WithMark(new Annotation(ScreenshotTool.Pixelate, R(10, 10, 30, 30)));
        host.BlurSampleSucceeds = false;
        model.BlurLevel = 5;
        Assert.Equal(3, model.Annotations[0].BlurLevel);
        Assert.Equal(3, model.BlurLevel);
        Assert.Empty(model.UndoStates);
        Assert.Equal(model.Annotations[0].Id, model.SelectedId);
    }

    [Fact]
    public void ChangingTheStickerEditsAPickedStickerOnly()
    {
        var (model, _) = WithMark(new Annotation(ScreenshotTool.Sticker, R(10, 10, 30, 30), text: "check"));
        model.Sticker = StickerKind.Heart;
        Assert.Equal("heart", model.Annotations[0].Text);
        Assert.Single(model.UndoStates);
        model.Sticker = StickerKind.Heart;
        Assert.Single(model.UndoStates);
        var (rect, _) = WithMark(Mark(ScreenshotTool.Rect, R(10, 10, 20, 20)));
        rect.Sticker = StickerKind.Star;
        Assert.Empty(rect.UndoStates);
    }

    [Fact]
    public void WithoutASelectionStyleChangesOnlyRememberTheChoice()
    {
        var model = Model();
        model.Annotations = [Mark(ScreenshotTool.Rect, R(10, 10, 20, 20))];
        model.Color = AnnotationColor.Orange;
        Assert.Equal(AnnotationColor.Red, model.Annotations[0].Color);
        Assert.Empty(model.UndoStates);
    }

    [Fact]
    public void EveryChoiceIsAnnouncedWithItsSettingsKey()
    {
        var model = Model();
        var seen = new List<(string Key, object Value)>();
        model.PreferenceChanged += (key, value) => seen.Add((key, value));
        model.Tool = ScreenshotTool.Text;
        model.Color = AnnotationColor.Blue;
        model.Stroke = StrokeWidth.Small;
        model.TextSize = 24;
        model.BlurLevel = 4;
        model.BlurStyle = BlurStyle.Blur;
        model.BlurTextOnly = true;
        model.ArrowStyle = ArrowStyle.Open;
        model.Sticker = StickerKind.Party;
        model.AnnotationShadowsEnabled = true;
        Assert.Equal(
        [
            (DefaultsKey.ScreenshotLastTool, (object)"text"),
            (DefaultsKey.ScreenshotLastColor, "blue"),
            (DefaultsKey.ScreenshotLastStroke, "small"),
            (DefaultsKey.ScreenshotLastTextSize, 24),
            (DefaultsKey.ScreenshotLastBlurLevel, 4),
            (DefaultsKey.ScreenshotLastBlurStyle, "blur"),
            (DefaultsKey.ScreenshotLastBlurTextOnly, true),
            (DefaultsKey.ScreenshotLastArrowStyle, "open"),
            (DefaultsKey.ScreenshotLastSticker, "party"),
            (DefaultsKey.ScreenshotAnnotationShadows, true),
        ], seen);
    }

    [Fact]
    public void TheSelectionStyleControlsFollowThePickedMark()
    {
        var model = Model();
        model.Annotations = [Mark(ScreenshotTool.Arrow, points: [P(1, 1), P(9, 9)])];
        model.Tool = ScreenshotTool.Select;
        Assert.False(model.ShowsArrowStyleControls);
        model.SelectedId = model.Annotations[0].Id;
        Assert.True(model.ShowsArrowStyleControls);
        Assert.True(model.ShowsColorControls);
        Assert.False(model.ShowsBlurControls);
        Assert.False(model.ShowsTextSizeControls);
        model.Tool = ScreenshotTool.Text;
        Assert.True(model.ShowsTextSizeControls);
        model.Tool = ScreenshotTool.Pixelate;
        Assert.True(model.ShowsBlurControls);
        Assert.False(model.ShowsColorControls);
    }

    [Theory]
    [InlineData(ScreenshotTool.Arrow, true)]
    [InlineData(ScreenshotTool.Line, true)]
    [InlineData(ScreenshotTool.Rect, true)]
    [InlineData(ScreenshotTool.Ellipse, true)]
    [InlineData(ScreenshotTool.Freehand, true)]
    [InlineData(ScreenshotTool.Highlight, true)]
    [InlineData(ScreenshotTool.Text, true)]
    [InlineData(ScreenshotTool.Counter, true)]
    [InlineData(ScreenshotTool.Redact, true)]
    [InlineData(ScreenshotTool.Sticker, false)]
    [InlineData(ScreenshotTool.Pixelate, false)]
    [InlineData(ScreenshotTool.Crop, false)]
    [InlineData(ScreenshotTool.Spotlight, false)]
    [InlineData(ScreenshotTool.ExpandCanvas, false)]
    [InlineData(ScreenshotTool.Select, false)]
    public void ColourControlsShowForTheToolsThatUseThem(ScreenshotTool tool, bool shows)
    {
        var model = Model();
        model.Tool = tool;
        Assert.Equal(shows, model.ShowsColorControls);
    }
}

public class ScreenshotCanvasLifecycleTests
{
    [Fact]
    public void ANewModelStartsCleanWithNothingToUndo()
    {
        var model = Model();
        Assert.False(model.IsDirty);
        Assert.False(model.CanUndo);
        Assert.False(model.CanRedo);
        Assert.Empty(model.Annotations);
        Assert.Equal(ScreenshotTool.Arrow, model.Tool);
    }

    [Fact]
    public void UndoWithEmptyHistoryDoesNothing()
    {
        var model = Model();
        model.Undo();
        model.Redo();
        Assert.False(model.CanUndo);
        Assert.False(model.CanRedo);
    }

    [Fact]
    public void UndoAndRedoWalkTheHistory()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Rect;
        model.BeginDrag(P(10, 10));
        model.ContinueDrag(P(40, 40));
        model.EndDrag(P(40, 40), isTap: false);
        Assert.True(model.CanUndo);
        Assert.False(model.CanRedo);
        model.Undo();
        Assert.Empty(model.Annotations);
        Assert.False(model.CanUndo);
        Assert.True(model.CanRedo);
        Assert.Null(model.SelectedId);
        model.Redo();
        Assert.Single(model.Annotations);
        Assert.True(model.CanUndo);
        Assert.False(model.CanRedo);
    }

    [Fact]
    public void ANewEditAfterUndoDropsTheRedoBranch()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Rect;
        model.BeginDrag(P(10, 10));
        model.ContinueDrag(P(40, 40));
        model.EndDrag(P(40, 40), isTap: false);
        model.Undo();
        model.BeginDrag(P(50, 50));
        model.ContinueDrag(P(60, 60));
        model.EndDrag(P(60, 60), isTap: false);
        Assert.False(model.CanRedo);
    }

    [Fact]
    public void UndoRestoresAnnotationsByValue()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(10, 10, 60, 60));
        model.Annotations = [rect];
        model.Tool = ScreenshotTool.Select;
        model.SelectedId = rect.Id;
        model.BeginDrag(P(40, 40));
        model.ContinueDrag(P(60, 60));
        model.EndDrag(P(60, 60), isTap: false);
        Assert.Equal(R(30, 30, 60, 60), model.Annotations[0].Rect);
        model.Undo();
        Assert.Equal(R(10, 10, 60, 60), model.Annotations[0].Rect);
        model.Redo();
        Assert.Equal(R(30, 30, 60, 60), model.Annotations[0].Rect);
    }

    [Fact]
    public void UndoClearsTheSelectionAndTheCropDraft()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(10, 10, 20, 20));
        model.Annotations = [rect];
        model.SelectedId = rect.Id;
        model.DeleteSelected();
        model.Tool = ScreenshotTool.Crop;
        model.Undo();
        Assert.Null(model.CropDraft);
        Assert.Null(model.SelectedId);
    }

    [Fact]
    public void UndoingBackToTheSavedStateIsClean()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Rect;
        model.BeginDrag(P(10, 10));
        model.ContinueDrag(P(40, 40));
        model.EndDrag(P(40, 40), isTap: false);
        Assert.True(model.IsDirty);
        model.Undo();
        Assert.False(model.IsDirty);
        model.Redo();
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void ExportingMarksTheCurrentStateClean()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Rect;
        model.BeginDrag(P(10, 10));
        model.ContinueDrag(P(40, 40));
        model.EndDrag(P(40, 40), isTap: false);
        model.MarkExported();
        Assert.False(model.IsDirty);
        model.Undo();
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void ChangingTheBackdropOrWatermarkOrShadowsCountsAsAnEdit()
    {
        var model = Model();
        model.BackdropKey = "gradient";
        Assert.True(model.IsDirty);
        model.BackdropKey = "";
        Assert.False(model.IsDirty);
        model.WatermarkKey = "mark";
        Assert.True(model.IsDirty);
        model.MarkExported();
        Assert.False(model.IsDirty);
        model.AnnotationShadowsEnabled = true;
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void ACropMakesTheModelDirtyAndItsUndoClean()
    {
        var model = Model();
        model.CropDraft = R(10, 10, 50, 50);
        model.ApplyCrop();
        Assert.True(model.IsDirty);
        model.Undo();
        Assert.False(model.IsDirty);
    }

    // ---- Starting values -----------------------------------------------------------------------------------------

    [Fact]
    public void RememberedChoicesSeedANewModel()
    {
        var prefs = new CanvasPreferences(LastTool: "pixelate", LastColor: "blue", LastStroke: "large", LastTextSize: 36,
            LastBlurLevel: 5, LastBlurStyle: "erase", LastBlurTextOnly: true, SpotlightDim: 0.7, LastArrowStyle: "doubleEnded",
            LastSticker: "fire", AnnotationShadows: true);
        var model = Model(prefs: prefs);
        Assert.Equal(ScreenshotTool.Pixelate, model.Tool);
        Assert.Equal(AnnotationColor.Blue, model.Color);
        Assert.Equal(StrokeWidth.Large, model.Stroke);
        Assert.Equal(36, model.TextSize);
        Assert.Equal(5, model.BlurLevel);
        Assert.Equal(BlurStyle.Erase, model.BlurStyle);
        Assert.True(model.BlurTextOnly);
        Assert.Equal(0.7, model.SpotlightDimStrength);
        Assert.Equal(ArrowStyle.DoubleEnded, model.ArrowStyle);
        Assert.Equal(StickerKind.Fire, model.Sticker);
        Assert.True(model.AnnotationShadowsEnabled);
    }

    [Fact]
    public void BrokenRememberedChoicesFallBackToSafeDefaults()
    {
        var prefs = new CanvasPreferences(LastTool: "crop", LastColor: "mauve", LastStroke: "?", LastTextSize: 5000,
            LastBlurLevel: 1, LastBlurStyle: "smudge", SpotlightDim: double.NaN, LastArrowStyle: "x", LastSticker: "y");
        var model = Model(prefs: prefs);
        Assert.Equal(ScreenshotTool.Arrow, model.Tool);
        Assert.Equal(AnnotationColor.Red, model.Color);
        Assert.Equal(StrokeWidth.Medium, model.Stroke);
        Assert.Equal(96, model.TextSize);
        Assert.Equal(3, model.BlurLevel);
        Assert.Equal(BlurStyle.Pixelate, model.BlurStyle);
        Assert.Equal(0.5, model.SpotlightDimStrength);
        Assert.Equal(ArrowStyle.Filled, model.ArrowStyle);
        Assert.Equal(StickerKind.Check, model.Sticker);
    }

    [Fact]
    public void AnUnsetTextSizeStartsAtTheDefault() => Assert.Equal(19, Model().TextSize);

    // ---- Tool switching ------------------------------------------------------------------------------------------

    [Fact]
    public void SwitchingToADifferentToolClearsTheSelectionAndTheTextEditor()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(10, 10, 20, 20));
        model.Annotations = [rect];
        model.Tool = ScreenshotTool.Rect;
        model.SelectedId = rect.Id;
        model.EditingTextId = rect.Id;
        model.Tool = ScreenshotTool.Arrow;
        Assert.Null(model.SelectedId);
        Assert.Null(model.EditingTextId);
    }

    [Fact]
    public void SwitchingToSelectKeepsTheSelection()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(10, 10, 20, 20));
        model.Annotations = [rect];
        model.SelectedId = rect.Id;
        model.Tool = ScreenshotTool.Select;
        Assert.Equal(rect.Id, model.SelectedId);
    }

    [Fact]
    public void ReassigningTheSameToolKeepsTheSelection()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(10, 10, 20, 20));
        model.Annotations = [rect];
        model.SelectedId = rect.Id;
        model.Tool = ScreenshotTool.Arrow;
        model.SelectedId = rect.Id;
        model.Tool = ScreenshotTool.Arrow;
        Assert.Equal(rect.Id, model.SelectedId);
    }

    [Fact]
    public void AToolOtherThanSelectClearsTheWordSelection()
    {
        var model = Model(200, 100);
        model.Tool = ScreenshotTool.Select;
        model.ApplyRecognition(model.BaseImage, [new RecognizedWord("a", R(0, 0, 20, 10), 0)], []);
        model.BeginDrag(P(5, 5));
        Assert.NotEmpty(model.SelectedWordIndexes);
        model.Tool = ScreenshotTool.Rect;
        Assert.Empty(model.SelectedWordIndexes);
    }

    // ---- Zoom and notifications ---------------------------------------------------------------------------------

    [Fact]
    public void ZoomStartsFromTheVisibleScaleAndClampsToItsRange()
    {
        var model = Model();
        Assert.Null(model.ZoomOverride);
        model.AdjustZoom(2);
        Assert.Equal(1, model.ZoomOverride);
        model.AdjustZoom(100);
        Assert.Equal(3, model.ZoomOverride);
        model.AdjustZoom(0.0001);
        Assert.Equal(0.05, model.ZoomOverride);
        model.SetZoom(10);
        Assert.Equal(3, model.ZoomOverride);
        model.SetZoom(1.5);
        Assert.Equal(1.5, model.ZoomOverride);
    }

    [Fact]
    public void ZoomStartsFromThePinnedZoomOnceOverridden()
    {
        var model = Model();
        model.SetZoom(1);
        model.CurrentDisplayZoom = 0.2;
        model.AdjustZoom(1.25);
        Assert.Equal(1.25, model.ZoomOverride);
    }

    [Fact]
    public void SizesFollowTheCaptureScale()
    {
        var model = Model(800, 600, scale: 2);
        Assert.Equal(new SizeD(800, 600), model.ImageSize);
        Assert.Equal(new SizeD(400, 300), model.PointSize);
    }

    [Fact]
    public void TheModelRaisesPropertyChangesForTheEditorView()
    {
        var model = Model();
        var names = new List<string?>();
        model.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        model.Tool = ScreenshotTool.Rect;
        model.BeginDrag(P(10, 10));
        model.ContinueDrag(P(40, 40));
        model.EndDrag(P(40, 40), isTap: false);
        Assert.Contains("Tool", names);
        Assert.Contains("Annotations", names);
        Assert.Contains("SelectedId", names);
        Assert.Contains("CanUndo", names);
        Assert.Contains("IsDirty", names);
    }

    [Fact]
    public void ACursorIsACrosshairForDrawingToolsAndAnArrowForSelect()
    {
        var model = Model();
        model.Tool = ScreenshotTool.Rect;
        Assert.Equal(CanvasCursor.Crosshair, model.CursorAt(P(5, 5)));
        model.Tool = ScreenshotTool.Select;
        Assert.Equal(CanvasCursor.Arrow, model.CursorAt(P(5, 5)));
    }

    [Fact]
    public void ACreationToolOffersTheOpenHandOverItsSelectedMark()
    {
        var model = Model();
        var rect = Mark(ScreenshotTool.Rect, R(20, 20, 40, 40));
        model.Annotations = [rect];
        model.Tool = ScreenshotTool.Rect;
        model.SelectedId = rect.Id;
        Assert.Equal(CanvasCursor.OpenHand, model.CursorAt(P(40, 40)));
        Assert.Equal(CanvasCursor.Crosshair, model.CursorAt(P(90, 90)));
    }

    [Fact]
    public void ADefaultHostMeasuresTextRoughlyAndGrowsWithSize()
    {
        ICanvasHost host = DefaultCanvasHost.Instance;
        var small = host.MeasureText("Hi", 12, 1);
        var large = host.MeasureText("Hi", 48, 1);
        Assert.True(large.Height > small.Height);
        Assert.True(large.Width > small.Width);
        Assert.True(host.MeasureText("Hi", 12, 2).Width > small.Width);
    }
}
