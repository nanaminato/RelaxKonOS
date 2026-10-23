using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Rendering;

namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>
/// Read-only unified diff: a two-column gutter, full-width row bands and red/green line ink.
/// </summary>
/// <remarks>
/// The view is a plain editor plus renderers rather than a list of row controls, so a patch with
/// tens of thousands of lines costs one document and one visual line per visible row. The price is
/// the invariant enforced in <see cref="DiffRowMap"/>: one document line per patch line, which is
/// what lets a renderer index its metadata by document line number.
/// </remarks>
internal partial class DiffView : UserControl
{
    public static readonly StyledProperty<DiffDocument?> DocumentProperty =
        AvaloniaProperty.Register<DiffView, DiffDocument?>(nameof(Document));

    private readonly DiffStyle _style;
    private readonly DiffLineNumberMargin _gutter;
    private DiffRowMap _rows = DiffRowMap.Empty;

    static DiffView()
    {
        // Class handlers are registered once per type; registering per instance would stack one
        // more handler on every view ever created.
        DocumentProperty.Changed.AddClassHandler<DiffView>((view, _) => view.ShowDocument());
    }

    public DiffView()
    {
        InitializeComponent();

        _style = DiffStyle.Create(this);
        _gutter = new DiffLineNumberMargin(() => _rows, _style);
        Editor.TextArea.LeftMargins.Insert(0, _gutter);

        var textView = Editor.TextArea.TextView;
        textView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => _rows, _style));
        textView.LineTransformers.Add(new DiffLineInkTransformer(() => _rows, _style));

        // A palette or system-style switch swaps the whole colour dictionary instead of changing it,
        // so the resolved brushes have to be read again; the text view is repainted from them.
        ResourcesChanged += (_, _) => ReloadStyle();
        ActualThemeVariantChanged += (_, _) => ReloadStyle();
    }

    /// <summary>The parsed patch to show. Null and empty documents render as an empty editor.</summary>
    public DiffDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The document can be assigned before the view reaches the resource chain, in which case
        // DiffStyle resolved its fallback literals; attaching is the first moment the tokens exist.
        ReloadStyle();
    }

    private void ShowDocument()
    {
        _rows = DiffRowMap.Create(Document);
        Editor.Text = _rows.Text;
        _gutter.InvalidateMeasure();
        Editor.TextArea.TextView.Redraw();
    }

    private void ReloadStyle()
    {
        _style.Reload(this);
        Editor.TextArea.TextView.Redraw();
        _gutter.InvalidateMeasure();
    }
}
