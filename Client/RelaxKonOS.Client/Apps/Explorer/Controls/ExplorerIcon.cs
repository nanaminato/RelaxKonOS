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

    static ExplorerIcon() => AffectsRender<ExplorerIcon>(EntryTypeProperty, FileNameProperty, TreeIconKindProperty);

    public FileSystemEntryType? EntryType { get => GetValue(EntryTypeProperty); set => SetValue(EntryTypeProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public TreeNodeIconKind? TreeIconKind { get => GetValue(TreeIconKindProperty); set => SetValue(TreeIconKindProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var assetName = TreeIconKind is { } treeKind
            ? ExplorerIconAssetResolver.ForTreeNode(treeKind)
            : ExplorerIconAssetResolver.ForEntry(EntryType ?? FileSystemEntryType.File, FileName);
        var bitmap = ExplorerIconAssetLoader.Load(assetName);
        if (bitmap is not null && Bounds.Width > 0 && Bounds.Height > 0)
            context.DrawImage(bitmap, Bounds);
    }
}
