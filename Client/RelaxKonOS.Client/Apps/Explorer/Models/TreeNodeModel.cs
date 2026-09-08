// 懒加载 + dummy child 模式移植自 Jaya TreeNodeModel（BSD-3）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>导航树节点。移植自 Jaya <c>TreeNodeModel</c> 的懒加载模式：展开时填充子节点。
/// 路径为 null 表示"Computer"根节点；驱动器节点的 Path 是盘符根；目录节点的 Path 是完整目录路径。</summary>
public sealed partial class TreeNodeModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isLoading;
    private bool _hasLoadedChildren;
    private bool _hasDummyChild = true;

    public TreeNodeModel(string? label, string? path,
        TreeNodeIconKind iconKind = TreeNodeIconKind.Folder,
        bool isDrive = false, bool isComputer = false)
    {
        Label = label;
        Path = path;
        IconKind = iconKind;
        IsDrive = isDrive;
        IsComputer = isComputer;
        Children = new ObservableCollection<TreeNodeModel>();
    }

    public string? Label { get; }
    public string? Path { get; }
    public bool IsDrive { get; }
    public bool IsComputer { get; }

    /// <summary>图标种类，驱动 View 中的 emoji 渲染（converter 见 EntryConverters）。</summary>
    public TreeNodeIconKind IconKind { get; }

    /// <summary>是否为"网络"占位节点（当前不实现浏览，点击仅显示状态栏文本不导航）。</summary>
    public bool IsNetwork => IconKind == TreeNodeIconKind.Network;

    /// <summary>是否为顶层特殊位置快捷入口（主目录组下的叶子节点：桌面/文档/下载/图片/音乐/视频）。</summary>
    public bool IsSpecialLocation =>
        IconKind is >= TreeNodeIconKind.Desktop and <= TreeNodeIconKind.Videos;

    /// <summary>
    /// Indicates the internal placeholder used to make an unloaded node expandable.
    /// Placeholders do not represent a filesystem location and must never be navigated to.
    /// </summary>
    public bool IsPlaceholder => Label is null && Path is null && !IsDrive && !IsComputer;

    /// <summary>子节点。加载前含一个 dummy 占位项以保证显示展开箭头；首次展开后替换为真实子目录。</summary>
    public ObservableCollection<TreeNodeModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_hasLoadedChildren)
            {
                _ = ExpandRequested?.Invoke(this);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>子节点是否已懒加载完成（首次展开后为 true）。VM 同步选中下钻时用于判断是否需触发加载。</summary>
    public bool HasLoadedChildren => _hasLoadedChildren;

    /// <summary>树节点首次展开时触发，调用方应填充 Children 并设 IsLoading=false。</summary>
    public Func<TreeNodeModel, Task>? ExpandRequested { get; set; }

    /// <summary>标记子节点已加载完成（移除 dummy）。</summary>
    public void MarkChildrenLoaded()
    {
        if (_hasDummyChild && Children.Count > 0 && ReferenceEquals(Children[0].Label, null))
        {
            // dummy child has null Label; remove it
            Children.Clear();
        }
        _hasDummyChild = false;
        _hasLoadedChildren = true;
    }

    /// <summary>添加 dummy 占位子节点以显示展开箭头。</summary>
    public void AddDummyChild()
    {
        if (Children.Count == 0)
        {
            Children.Add(new TreeNodeModel(null, null));
            _hasDummyChild = true;
        }
    }
}
