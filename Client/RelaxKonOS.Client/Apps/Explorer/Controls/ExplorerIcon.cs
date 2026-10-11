using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Controls;

/// <summary>Draws the generated, extension-aware Explorer PNG assets at the requested UI size.</summary>
public sealed class ExplorerIcon : Control
{
    public static readonly StyledProperty<FileSystemEntryType?> EntryTypeProperty =
        AvaloniaProperty.Register<ExplorerIcon, FileSystemEntryType?>(nameof(EntryType));
    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<ExplorerIcon, string?>(nameof(FileName));
    public static readonly StyledProperty<TreeNodeIconKind?> TreeIconKindProperty =
        AvaloniaProperty.Register<ExplorerIcon, TreeNodeIconKind?>(nameof(TreeIconKind));

    /// <summary>
    /// A category the caller classified itself, for a surface that does not hold a file-system
    /// entry. It outranks the other two because it states the answer instead of the question.
    /// </summary>
    public static readonly StyledProperty<ExplorerFileIconKind?> KindProperty =
        AvaloniaProperty.Register<ExplorerIcon, ExplorerFileIconKind?>(nameof(Kind));

    /// <summary>
    /// Marks a symbolic link. A link has no reliable extension-based identity — its target is
    /// unknown until the link is followed — so it draws the dedicated link asset instead.
    /// </summary>
    public static readonly StyledProperty<bool> LinkProperty =
        AvaloniaProperty.Register<ExplorerIcon, bool>(nameof(Link));

    static ExplorerIcon() => AffectsRender<ExplorerIcon>(
        EntryTypeProperty, FileNameProperty, TreeIconKindProperty, KindProperty, LinkProperty);

    public FileSystemEntryType? EntryType { get => GetValue(EntryTypeProperty); set => SetValue(EntryTypeProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public TreeNodeIconKind? TreeIconKind { get => GetValue(TreeIconKindProperty); set => SetValue(TreeIconKindProperty, value); }
    public ExplorerFileIconKind? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool Link { get => GetValue(LinkProperty); set => SetValue(LinkProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var assetName = Kind is { } kind
            ? ExplorerIconAssetResolver.ForKind(kind)
            : TreeIconKind is { } treeKind
                ? ExplorerIconAssetResolver.ForTreeNode(treeKind)
                : ExplorerIconAssetResolver.ForEntry(EntryType ?? FileSystemEntryType.File, FileName, Link);
        var bitmap = ExplorerIconAssetLoader.Load(assetName);
        if (bitmap is not null && Bounds.Width > 0 && Bounds.Height > 0)
            // Drawing coordinates are local to the control; Bounds also contains its parent offset.
            context.DrawImage(bitmap, new Rect(Bounds.Size));
    }
}
