using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace ThemeVsix
{
    // Visual Studio draws the text selection at a fixed 40% opacity, recolours selected text only
    // under Windows high contrast, and paints a lone caret in the "Plain Text" colour ("Caret
    // (Primary)" applies only with multiple carets). While a theme that defines the "ThemeVsix
    // Editor" category is active, the selection is painted opaque in the theme's colour, selected
    // text takes the theme's "Selected Text in High Contrast" colour, and the caret takes the
    // theme's caret colours. Other themes see stock behaviour.
    internal static class ThemeEditorColours
    {
        private static readonly Guid Category = new Guid("6c1f2a9e-4b7d-4e3a-9f51-8d2c7a0b3e64");

        private static bool loaded;

        // Written on the UI thread only; taggers may read them from any thread.
        private static volatile bool enabled;
        private static volatile Brush active;
        private static volatile Brush inactive;
        private static volatile Brush caret;
        private static volatile Brush caretText;

        public static event Action Changed;

        public static bool Enabled => enabled;

        public static Brush Selection(bool focused) => focused ? active : inactive;

        public static Brush Caret => caret;

        public static Brush CaretText => caretText;

        // Reads the theme once, and again on every theme change. Called from view creation.
        public static void EnsureLoaded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (loaded) return;
            loaded = true;
            VSColorTheme.ThemeChanged += _ => Refresh();
            Refresh();
        }

        private static void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A colour the theme does not define comes back opaque black, so only a theme that
            // declares the alpha-1 marker enables the component.
            bool on = Get("Enabled").A == 1;
            if (on)
            {
                active = Freeze(Get("SelectionBackground"));
                inactive = Freeze(Get("InactiveSelectionBackground"));
                caret = Freeze(Get("CaretForeground"));
                caretText = Freeze(Get("CaretText"));
            }
            enabled = on;
            Changed?.Invoke();
        }

        private static System.Drawing.Color Get(string name) =>
            VSColorTheme.GetThemedColor(new ThemeResourceKey(Category, name, ThemeResourceKeyType.BackgroundColor));

        private static Brush Freeze(System.Drawing.Color c)
        {
            var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class SelectionPainterFactory : IWpfTextViewCreationListener
    {
        public const string LayerName = "ThemeVsix.Selection." + ThemeIdentity.Name;

        // Above Visual Studio's translucent selection, below the text.
        [Export]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        internal AdornmentLayerDefinition Layer = null;

        public void TextViewCreated(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ThemeEditorColours.EnsureLoaded();
            new SelectionPainter(view);
            new CaretPainter(view);
        }
    }

    internal sealed class SelectionPainter
    {
        private readonly IWpfTextView view;
        private readonly IAdornmentLayer layer;
        private readonly IMultiSelectionBroker broker;

        public SelectionPainter(IWpfTextView view)
        {
            this.view = view;
            layer = view.GetAdornmentLayer(SelectionPainterFactory.LayerName);
            broker = view.GetMultiSelectionBroker();

            view.LayoutChanged += OnLayoutChanged;
            broker.MultiSelectionSessionChanged += OnChanged;
            view.GotAggregateFocus += OnChanged;
            view.LostAggregateFocus += OnChanged;
            ThemeEditorColours.Changed += Redraw;
            view.Closed += OnClosed;
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void OnChanged(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void Redraw()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            layer.RemoveAllAdornments();
            if (view.IsClosed || view.InLayout || !ThemeEditorColours.Enabled) return;

            Brush brush = ThemeEditorColours.Selection(view.HasAggregateFocus);
            foreach (var selection in broker.AllSelections)
            {
                VirtualSnapshotSpan extent = selection.Extent;
                SnapshotSpan span = extent.SnapshotSpan;

                if (!span.IsEmpty)
                {
                    Geometry geometry = view.TextViewLines.GetMarkerGeometry(span);
                    if (geometry != null) Add(span, geometry, brush);
                }

                // Past the end of a line (a box selection over a short line, or virtual space) the
                // selection has no characters, so the marker geometry above stops at the line end.
                if (extent.End.IsInVirtualSpace)
                {
                    ITextViewLine line = view.TextViewLines.GetTextViewLineContainingBufferPosition(extent.End.Position);
                    if (line == null) continue;
                    VirtualSnapshotPoint from = extent.Start.IsInVirtualSpace && extent.Start.Position == extent.End.Position
                        ? extent.Start
                        : new VirtualSnapshotPoint(line.End);
                    double left = line.GetExtendedCharacterBounds(from).Leading;
                    double right = line.GetExtendedCharacterBounds(extent.End).Leading;
                    if (right > left)
                        Add(new SnapshotSpan(extent.End.Position, 0),
                            new RectangleGeometry(new System.Windows.Rect(left, line.TextTop, right - left, line.TextHeight)), brush);
                }
            }
        }

        private void Add(SnapshotSpan anchor, Geometry geometry, Brush brush) =>
            layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, anchor, null,
                               new Path { Data = geometry, Fill = brush, IsHitTestVisible = false }, null);

        private void OnClosed(object sender, EventArgs e)
        {
            view.LayoutChanged -= OnLayoutChanged;
            broker.MultiSelectionSessionChanged -= OnChanged;
            view.GotAggregateFocus -= OnChanged;
            view.LostAggregateFocus -= OnChanged;
            ThemeEditorColours.Changed -= Redraw;
            view.Closed -= OnClosed;
        }
    }

    // Adds the theme's caret to Visual Studio's own "Caret" layer, above Visual Studio's caret, so it
    // blinks with it: the blink toggles that layer's opacity. A block caret (overwrite mode, or a wide
    // caret) also redraws the text under it in the theme's caret text colour, as VS Code does. That
    // text is Visual Studio's own glyph runs for the line, placed as Visual Studio places them. Text
    // drawn any other way lands on other pixels, so the character under the caret moved each time the
    // caret blinked: a FormattedText outline sat 1 px low at 120% zoom (VS 18.10), because the public
    // ITextViewLine.Baseline is rounded up and an outline is not hinted or pixel-snapped as glyphs are,
    // and even the character formatted on its own snaps its origin differently at fractional zoom.
    internal sealed class CaretPainter
    {
        private const string Tag = "ThemeVsix.Caret";

        private readonly IWpfTextView view;
        private readonly IAdornmentLayer layer;
        private readonly IMultiSelectionBroker broker;

        public CaretPainter(IWpfTextView view)
        {
            this.view = view;
            layer = view.GetAdornmentLayer(PredefinedAdornmentLayers.Caret);
            broker = view.GetMultiSelectionBroker();

            view.LayoutChanged += OnLayoutChanged;
            view.Caret.PositionChanged += OnCaretMoved;
            broker.MultiSelectionSessionChanged += OnChanged;
            view.VisualElement.IsKeyboardFocusedChanged += OnKeyboardFocusChanged;
            view.Options.OptionChanged += OnOptionChanged;
            ThemeEditorColours.Changed += Redraw;
            view.Closed += OnClosed;
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void OnOptionChanged(object sender, EditorOptionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void OnKeyboardFocusChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void OnChanged(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Redraw();
        }

        private void Redraw()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            layer.RemoveAdornmentsByTag(Tag);
            if (view.IsClosed || view.InLayout || !ThemeEditorColours.Enabled) return;

            // Visual Studio shows carets only while the view itself has keyboard focus (not, say, the
            // find box), and with several carets already uses "Caret (Primary)"/"(Secondary)".
            if (!view.VisualElement.IsKeyboardFocused || view.Caret.IsHidden || broker.HasMultipleSelections) return;
            if (!broker.TryGetSelectionPresentationProperties(broker.PrimarySelection, out var properties)) return;

            TextBounds bounds = properties.CaretBounds;
            if (bounds.Width <= 0 || bounds.TextHeight <= 0) return;
            var rect = new System.Windows.Rect(bounds.Left, bounds.TextTop, bounds.Width, bounds.TextHeight);
            VirtualSnapshotPoint insertion = broker.PrimarySelection.InsertionPoint;
            SnapshotPoint position = insertion.Position;

            // Text-relative adornments need a visual span; Visual Studio anchors its caret the same way.
            var anchor = new SnapshotSpan(position, 0);
            Add(anchor, new Path { Data = new RectangleGeometry(rect), Fill = ThemeEditorColours.Caret });

            ITextViewLine line = properties.ContainingTextViewLine;
            if (line == null || insertion.IsInVirtualSpace || position >= line.End) return;

            // Only a caret as wide as the character is a block; a thin caret leaves the glyph alone.
            TextBounds glyph = line.GetCharacterBounds(position);
            if (bounds.Width < glyph.Width * 0.75) return;

            // Whatever Visual Studio drew inside the block, a neighbour's overhang included, since the
            // block covers it too.
            Drawing text = LineText(line as IWpfTextViewLine, rect);
            if (text != null) Add(anchor, new CaretGlyphs(text, rect, line.LineTransform.VerticalScale, line.TextTop));
        }

        // The line's text as Visual Studio draws it (FormattedLine.RenderedLineVisual.RenderText, VS
        // 18.10), in line coordinates: each WPF TextLine at the running left edge, its baseline on the
        // line's. Only the TextLines that reach the block are replayed.
        private Drawing LineText(IWpfTextViewLine line, System.Windows.Rect block)
        {
            IFormattedLineSource source = view.FormattedLineSource;
            if (line == null || source == null) return null;

            double baseline = UnscaledBaseline(line, source.UseDisplayMode);
            // The public Baseline is Visual Studio's own baseline scaled and rounded up. When an
            // intra-text adornment set that baseline the runs alone do not give it, and a guess would
            // move the glyphs off Visual Studio's, so the block is left plain.
            if (Math.Ceiling(baseline * line.LineTransform.VerticalScale) != line.Baseline) return null;

            var drawn = new DrawingGroup();
            using (DrawingContext context = drawn.Open())
            {
                double left = line.Left;
                foreach (var textLine in line.TextLines)
                {
                    double right = left + textLine.WidthIncludingTrailingWhitespace;
                    if (right > block.Left && left < block.Right)
                        textLine.Draw(context, new System.Windows.Point(left, baseline - textLine.Baseline),
                                      System.Windows.Media.TextFormatting.InvertAxes.None);
                    left = right;
                }
            }

            DrawingGroup text = Recolour(drawn, ThemeEditorColours.CaretText);
            text.Freeze();
            return text;
        }

        // The line's baseline before any line transform, measured as TextInfoCache.GetTextInfo measures
        // each run's font (VS 18.10): the highest baseline of a FormattedText "Xg " among its runs.
        private static double UnscaledBaseline(IWpfTextViewLine line, bool displayMode)
        {
            var measured = new HashSet<System.Windows.Media.TextFormatting.TextRunProperties>();
            double baseline = 0;
            foreach (var textLine in line.TextLines)
            {
                foreach (var span in textLine.GetTextRunSpans())
                {
                    if (!(span.Value is System.Windows.Media.TextFormatting.TextCharacters run) || !measured.Add(run.Properties))
                        continue;
                    var font = run.Properties;
                    var sample = new FormattedText("Xg ", font.CultureInfo, System.Windows.FlowDirection.LeftToRight,
                        font.Typeface, font.FontRenderingEmSize, Brushes.Black, null,
                        displayMode ? TextFormattingMode.Display : TextFormattingMode.Ideal, font.PixelsPerDip);
                    baseline = Math.Max(baseline, sample.Baseline);
                }
            }
            return baseline;
        }

        // Visual Studio's glyph runs in the caret text colour. Run backgrounds, decorations and
        // embedded objects are dropped. DrawingGroup.Open returns its drawings frozen, so each run gets
        // a new drawing around the same GlyphRun.
        private static DrawingGroup Recolour(DrawingGroup drawn, Brush brush)
        {
            var result = new DrawingGroup
            {
                Transform = drawn.Transform,
                ClipGeometry = drawn.ClipGeometry,
                Opacity = drawn.Opacity,
                GuidelineSet = drawn.GuidelineSet,
            };
            foreach (Drawing child in drawn.Children)
            {
                if (child is GlyphRunDrawing run) result.Children.Add(new GlyphRunDrawing(brush, run.GlyphRun));
                else if (child is DrawingGroup group) result.Children.Add(Recolour(group, brush));
            }
            return result;
        }

        private void Add(SnapshotSpan anchor, System.Windows.UIElement element)
        {
            element.IsHitTestVisible = false;
            System.Windows.Controls.Panel.SetZIndex(element, 1);
            layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, anchor, Tag, element, null);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            view.LayoutChanged -= OnLayoutChanged;
            view.Caret.PositionChanged -= OnCaretMoved;
            broker.MultiSelectionSessionChanged -= OnChanged;
            view.VisualElement.IsKeyboardFocusedChanged -= OnKeyboardFocusChanged;
            view.Options.OptionChanged -= OnOptionChanged;
            ThemeEditorColours.Changed -= Redraw;
            view.Closed -= OnClosed;
        }
    }

    // Draws replayed line text clipped to the caret block, under the transform Visual Studio gives each
    // rendered line (RenderedLineVisual.SetTransform, VS 18.10), so its glyphs land on Visual Studio's.
    internal sealed class CaretGlyphs : System.Windows.UIElement
    {
        private readonly Drawing text;
        private readonly Geometry clip;
        private readonly Transform toView;

        public CaretGlyphs(Drawing text, System.Windows.Rect block, double verticalScale, double textTop)
        {
            this.text = text;
            clip = new RectangleGeometry(block);
            clip.Freeze();
            toView = new MatrixTransform(1, 0, 0, verticalScale, 0, textTop);
            toView.Freeze();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.PushClip(clip);
            drawingContext.PushTransform(toView);
            drawingContext.DrawDrawing(text);
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("any")]
    [TagType(typeof(ClassificationTag))]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class SelectedTextTaggerProvider : IViewTaggerProvider
    {
        // Visual Studio's own high-contrast selection classification; its format is ordered after
        // "Highest Priority", so it overrides syntax colours.
        private const string ClassificationTypeName = "HighContrastSelection";

        [Import]
        internal IClassificationTypeRegistryService Registry = null;

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView.TextBuffer != buffer) return null;
            IClassificationType type = Registry.GetClassificationType(ClassificationTypeName);
            if (type == null) return null;
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(SelectedTextTagger), () => new SelectedTextTagger(textView, type)) as ITagger<T>;
        }
    }

    internal sealed class SelectedTextTagger : ITagger<ClassificationTag>
    {
        private readonly ITextView view;
        private readonly IMultiSelectionBroker broker;
        private readonly ClassificationTag tag;
        // Captured on the UI thread when the selection changes and replaced whole, because
        // classification can ask for tags from other threads.
        private volatile List<SnapshotSpan> tagged = new List<SnapshotSpan>();

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public SelectedTextTagger(ITextView view, IClassificationType type)
        {
            this.view = view;
            broker = view.GetMultiSelectionBroker();
            tag = new ClassificationTag(type);
            broker.MultiSelectionSessionChanged += OnSelectionChanged;
            ThemeEditorColours.Changed += OnThemeChanged;
            view.Closed += OnClosed;
        }

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || !ThemeEditorColours.Enabled) yield break;

            ITextSnapshot snapshot = spans[0].Snapshot;
            foreach (SnapshotSpan selection in tagged)
            {
                SnapshotSpan selected = selection.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
                foreach (SnapshotSpan span in spans)
                {
                    SnapshotSpan? overlap = span.Overlap(selected);
                    if (overlap.HasValue) yield return new TagSpan<ClassificationTag>(overlap.Value, tag);
                }
            }
        }

        // Reclassifies what was selected before and what is selected now.
        private void OnSelectionChanged(object sender, EventArgs e)
        {
            var now = new List<SnapshotSpan>();
            foreach (var selection in broker.AllSelections)
                if (!selection.Extent.SnapshotSpan.IsEmpty) now.Add(selection.Extent.SnapshotSpan);

            Raise(tagged);
            Raise(now);
            tagged = now;
        }

        private void OnThemeChanged()
        {
            ITextSnapshot snapshot = view.TextSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private void Raise(List<SnapshotSpan> spans)
        {
            ITextSnapshot current = view.TextSnapshot;
            foreach (SnapshotSpan span in spans)
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span.TranslateTo(current, SpanTrackingMode.EdgeInclusive)));
        }

        private void OnClosed(object sender, EventArgs e)
        {
            broker.MultiSelectionSessionChanged -= OnSelectionChanged;
            ThemeEditorColours.Changed -= OnThemeChanged;
            view.Closed -= OnClosed;
        }
    }
}
