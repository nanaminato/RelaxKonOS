// 数据流移植自 Jaya ExplorerViewModel / NavigationViewModel / AddressbarViewModel / ToolbarViewModel /
// StatusbarViewModel（BSD-3），合并为单一 VM 适配 RemoteOS DI 约定（去 ServiceLocator/EventAggregator）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Enumeration;
using System.Windows.Input;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Client.Localization;
using Client.Services.Auth;
using Client.Apps.Explorer.Models;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.ViewModels;

/// <summary>RemoteExplorer 主视图模型。移植自 Jaya <c>ExplorerViewModel</c> + <c>NavigationViewModel</c> +
/// <c>AddressbarViewModel</c> + <c>ToolbarViewModel</c> + <c>StatusbarViewModel</c>（合并以适配 RemoteOS DI 约定，
/// 避免引入 Jaya 的 ServiceLocator/EventAggregator 反射基础设施）。
///
/// 数据流：导航树选中目录 → <see cref="NavigateToAsync"/> → <see cref="IExplorerClient.GetDirectoryAsync"/>
/// → 填充 <see cref="Entries"/> 网格 + <see cref="AddressbarPath"/>。双击目录进入；双击文件下载。
/// 历史栈支持前进/后退/向上。文件操作（删除/重命名/复制/移动/新建/上传/下载）通过对话框回调与宿主交互。
///
/// 导航树结构（参考 Windows File Explorer Navigation Pane）：主目录组节点（家目录 + 静态快捷入口：桌面/文档/下载/图片/音乐/视频）
/// + 此电脑节点（盘符懒加载）+ 网络占位节点。路径变化时由 <see cref="SyncTreeSelectionAsync"/> 反向同步树选中（防循环）。</summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly IExplorerClient _client;
    private readonly IRemoteFileClipboard _fileClipboard;
    private readonly ExplorerPickerOptions? _pickerOptions;
    private readonly Action<IReadOnlyList<string>>? _selectPaths;
    private bool _isUpdatingPickerText;
    private bool _pickerInitialized;
    private readonly List<string?> _history = new();
    private int _historyIndex = -1;

    /// <summary>路径变化时同步树选中的抑制标志：避免 SyncTreeSelectionAsync 设 SelectedNode 触发 OnSelectedNodeChanged
    /// 再调 NavigateToAsync 形成循环（重复 API 调用 + 重复历史入栈）。</summary>
    private bool _isSyncingTreeSelection;

    /// <summary>
    /// Creates the Explorer view model. Supplying picker options enables selection mode:
    /// folders remain navigable while confirmation returns the selected remote paths to the host.
    /// </summary>
    public ExplorerViewModel(
        IExplorerClient client,
        ExplorerPickerOptions? pickerOptions = null,
        Action<IReadOnlyList<string>>? selectPaths = null,
        IRemoteFileClipboard? fileClipboard = null)
    {
        _client = client;
        _fileClipboard = fileClipboard ?? new RemoteFileClipboard();
        _pickerOptions = pickerOptions;
        _selectPaths = selectPaths;
        Nodes = new ObservableCollection<TreeNodeModel>();
        Entries = new ObservableCollection<FileSystemEntryDto>();
        SelectedEntries = new ObservableCollection<FileSystemEntryDto>();
        Filters = new ObservableCollection<ExplorerFileFilter>(pickerOptions?.Filters?.Count > 0
            ? pickerOptions.Filters
            : [ExplorerFileFilter.AllFiles]);
        SelectedFilter = Filters[0];
        PickerEntryName = pickerOptions?.DefaultFileName ?? string.Empty;
        _pickerInitialized = true;
    }

    /// <summary>导航树根节点集合（主目录组 / 此电脑 / 网络占位）。</summary>
    public ObservableCollection<TreeNodeModel> Nodes { get; }

    /// <summary>Explorer 网格条目（当前目录的子目录 + 文件）。</summary>
    public ObservableCollection<FileSystemEntryDto> Entries { get; }
    /// <summary>Entries currently selected in the picker; supports multi-file selection.</summary>
    public ObservableCollection<FileSystemEntryDto> SelectedEntries { get; }
    public ObservableCollection<ExplorerFileFilter> Filters { get; }
    [ObservableProperty] private string? _addressbarPath;
    [ObservableProperty] private string _statusText = LocalizedText.Get("explorer.status.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FileSystemEntryDto? _selectedEntry;
    [ObservableProperty] private TreeNodeModel? _selectedNode;
    [ObservableProperty] private ExplorerFileFilter? _selectedFilter;
    [ObservableProperty] private string _pickerEntryName = string.Empty;

    // 对话框/宿主回调（由 ExplorerApp 注入）
    /// <summary>请求文本输入对话框。参数：(title, prompt, defaultValue, confirmLabel) → 返回输入或 null（取消）。</summary>
    public Func<string, string, string, string, Task<string?>>? RequestTextInputAsync { get; set; }
    /// <summary>请求确认对话框。参数：(title, message, confirmLabel) → 返回 true/false。</summary>
    public Func<string, string, string, Task<bool>>? RequestConfirmAsync { get; set; }
    /// <summary>请求选择客户端宿主机文件（可多选）。</summary>
    public Func<Task<IReadOnlyList<LocalUploadSource>>>? RequestLocalUploadFilesAsync { get; set; }
    /// <summary>请求选择客户端宿主机文件夹（可多选）。</summary>
    public Func<Task<IReadOnlyList<LocalUploadSource>>>? RequestLocalUploadFoldersAsync { get; set; }
    /// <summary>读取宿主机剪贴板中的文件/文件夹。</summary>
    public Func<Task<IReadOnlyList<LocalUploadSource>>>? RequestClipboardUploadSourcesAsync { get; set; }
    /// <summary>请求本地保存路径（用于下载目标）。参数：默认文件名。返回本地路径或 null。</summary>
    public Func<string, Task<string?>>? RequestLocalSaveFileAsync { get; set; }
    /// <summary>Ensures direct or short-lived elevated access before a protected file is opened or downloaded.</summary>
    public Func<string, FileElevationCapability, Task<bool>>? RequestFileElevationAsync { get; set; }
    /// <summary>Requests a five-minute elevated directory grant after a mutating operation was denied.</summary>
    public Func<IReadOnlyList<string>, FileElevationCapability, Task<bool>>? RequestFileOperationElevationAsync { get; set; }
    /// <summary>使用默认程序打开一个远程文件。</summary>
    public Func<FileSystemEntryDto, Task>? OpenFileAsync { get; set; }
    /// <summary>选择程序后打开一个远程文件。</summary>
    public Func<FileSystemEntryDto, Task>? RequestOpenWithAsync { get; set; }
    /// <summary>在指定远程目录中打开内置终端。</summary>
    public Func<string, Task>? OpenTerminalAtPathAsync { get; set; }
    /// <summary>显示远程文件或目录的属性。</summary>
    public Func<FilePropertiesDto, Task>? ShowPropertiesAsync { get; set; }
    /// <summary>显示消息（About 等）。参数：(title, message)。</summary>
    public Func<string, string, Task>? ShowMessageAsync { get; set; }
    /// <summary>关闭 Explorer 窗口。</summary>
    public Action? CloseAction { get; set; }
    /// <summary>Cancels the surrounding picker dialog when file-picker mode is active.</summary>
    public Action? CancelAction { get; set; }

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _history.Count - 1;
    public bool CanGoUp => !string.IsNullOrEmpty(AddressbarPath);
    public bool HasSelection => SelectedEntries.Count != 0 || SelectedEntry is not null;
    public bool CanOpenTerminal => SelectedEntry is { } entry && IsFolder(entry)
        || !string.IsNullOrWhiteSpace(AddressbarPath);
    public bool IsPickerMode => _pickerOptions is not null && _selectPaths is not null;
    public bool IsFolderPickerMode => IsPickerMode && _pickerOptions!.Mode == ExplorerPickerMode.SelectFolder;
    public bool IsSaveFilePickerMode => IsPickerMode && _pickerOptions!.Mode == ExplorerPickerMode.SaveFile;
    public bool IsFilePickerMode => IsPickerMode && !IsFolderPickerMode;
    public bool AllowMultipleFiles => IsFilePickerMode && _pickerOptions!.AllowMultiple;
    public DataGridSelectionMode EntrySelectionMode => !IsPickerMode || AllowMultipleFiles
        ? DataGridSelectionMode.Extended
        : DataGridSelectionMode.Single;
    public string PickerEntryLabel => IsFolderPickerMode ? LocalizedText.Get("explorer.picker.folder_label") : LocalizedText.Get("explorer.picker.file_name_label");
    public string PickerConfirmLabel => IsFolderPickerMode
        ? LocalizedText.Get("explorer.picker.select_folder")
        : IsSaveFilePickerMode ? LocalizedText.Get("common.save") : LocalizedText.Get("common.open");
    public bool CanConfirmPicker => IsFolderPickerMode
        ? SelectedEntries.Any(IsFolder) || !string.IsNullOrWhiteSpace(AddressbarPath)
        : IsSaveFilePickerMode
            ? !string.IsNullOrWhiteSpace(AddressbarPath) && IsValidSaveFileName(PickerEntryName)
        : SelectedEntries.Any(IsSelectableFile) || !string.IsNullOrWhiteSpace(PickerEntryName);
    public bool HasTransferProgress => IsTransferActive;
    public double TransferProgress => TransferTotalBytes > 0
        ? Math.Clamp(TransferBytesCompleted * 100d / TransferTotalBytes, 0, 100)
        : TransferItemTotal > 0 ? Math.Clamp(TransferItemCompleted * 100d / TransferItemTotal, 0, 100) : 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransferProgress))]
    private bool _isTransferActive;
    [ObservableProperty] private string _transferText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferProgress))]
    private long _transferBytesCompleted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferProgress))]
    private long _transferTotalBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferProgress))]
    private int _transferItemCompleted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferProgress))]
    private int _transferItemTotal;

    // ---- 加载根 ----

    public async Task LoadRootAsync()
    {
        IsBusy = true;
        StatusText = LocalizedText.Get("explorer.status.loading_navigation");
        try
        {
            // 并发加载特殊位置与盘符列表
            var specialTask = _client.GetSpecialLocationsAsync();
            var drivesTask = _client.GetDrivesAsync();
            await Task.WhenAll(specialTask, drivesTask);
            var specials = specialTask.Result;
            var drives = GetNavigationDrives(drivesTask.Result);

            Nodes.Clear();

            // (1) 主目录组节点：静态填充快捷入口（不含 dummy child，叶子节点点击直接导航，不挂 ExpandRequested）。
            // 与 Windows 11 File Explorer Home 节点行为一致：展开=精选快捷入口；点击组节点本身=导航到家目录（右侧网格列全部子项）。
            var homeEntry = specials.FirstOrDefault(s => s.Kind == SpecialFolderKind.Home);
            var homePath = homeEntry?.Path;
            var homeGroup = new TreeNodeModel(LocalizedText.Get("explorer.home"), homePath, iconKind: TreeNodeIconKind.Home);
            foreach (var s in specials.Where(s => s.Kind != SpecialFolderKind.Home))
            {
                var icon = s.Kind switch
                {
                    SpecialFolderKind.Desktop   => TreeNodeIconKind.Desktop,
                    SpecialFolderKind.Documents => TreeNodeIconKind.Documents,
                    SpecialFolderKind.Downloads => TreeNodeIconKind.Downloads,
                    SpecialFolderKind.Pictures  => TreeNodeIconKind.Pictures,
                    SpecialFolderKind.Music      => TreeNodeIconKind.Music,
                    SpecialFolderKind.Videos     => TreeNodeIconKind.Videos,
                    _ => TreeNodeIconKind.Folder
                };
                // 快捷入口叶子节点：不 AddDummyChild、不挂 ExpandRequested（点击直接导航）
                homeGroup.Children.Add(new TreeNodeModel(s.Name, s.Path, iconKind: icon));
            }
            Nodes.Add(homeGroup);

            // (2) 此电脑节点：保留盘符列表 + dummy child 懒加载（与原 Jaya 逻辑一致）
            var thisPc = new TreeNodeModel(LocalizedText.Get("explorer.computer"), null,
                iconKind: TreeNodeIconKind.Computer, isComputer: true);
            thisPc.ExpandRequested = OnNodeExpandRequested;
            foreach (var d in drives)
            {
                var node = new TreeNodeModel(d.Name, d.Path,
                    iconKind: TreeNodeIconKind.Drive, isDrive: true);
                node.AddDummyChild();
                node.ExpandRequested = OnNodeExpandRequested;
                thisPc.Children.Add(node);
            }
            Nodes.Add(thisPc);

            // (3) 网络占位节点（当前不实现浏览）
            Nodes.Add(new TreeNodeModel(LocalizedText.Get("explorer.network"), null, iconKind: TreeNodeIconKind.Network));

            homeGroup.IsExpanded = true;
            thisPc.IsExpanded = true;
            StatusText = LocalizedText.Format("explorer.status.root_ready", drives.Count, specials.Count);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.load_failed", ex.Message); }
        finally { IsBusy = false; }
    }

    private async Task OnNodeExpandRequested(TreeNodeModel node)
    {
        if (node.IsComputer || string.IsNullOrEmpty(node.Path)) { node.MarkChildrenLoaded(); return; }
        if (node.IsLoading) return;
        node.IsLoading = true;
        try
        {
            var dir = await _client.GetDirectoryAsync(node.Path);
            node.Children.Clear();
            foreach (var sub in dir.Directories)
            {
                var child = new TreeNodeModel(sub.Name, sub.Path, iconKind: TreeNodeIconKind.Folder);
                child.AddDummyChild();
                child.ExpandRequested = OnNodeExpandRequested;
                node.Children.Add(child);
            }
            node.MarkChildrenLoaded();
        }
        catch (Exception ex)
        {
            // 展开操作由 TreeNodeModel 以 fire-and-forget 方式发起，不能让异常丢失，
            // 否则权限、网络或服务端错误都会表现为一个无法展开的空节点。
            StatusText = LocalizedText.Format("explorer.status.path_load_failed", node.Path, ex.Message);
        }
        finally { node.IsLoading = false; }
    }

    partial void OnSelectedNodeChanged(TreeNodeModel? value)
    {
        // 同步设置 SelectedNode 时抑制反向导航（避免循环 + 重复历史入栈）
        if (_isSyncingTreeSelection) return;
        // Unloaded tree nodes contain a dummy child solely to show the expand glyph.
        // Its path is null, which used to be interpreted as the Computer root and
        // therefore replaced the current directory with the drive list.
        if (value is null || value.IsPlaceholder) return;
        if (value.IsNetwork)
        {
            // 网络占位：当前不实现浏览，仅状态栏提示，不导航
            StatusText = LocalizedText.Get("explorer.status.network_not_implemented");
            return;
        }
        _ = value.IsComputer ? NavigateToAsync(null) : NavigateToAsync(value.Path);
    }

    partial void OnAddressbarPathChanged(string? value)
    {
        // 注意：不在此同步树选中。TextBox.Text TwoWay 绑定默认 PropertyChanged，每个按键都触发本方法，
        // 路径还是半成品时去查找节点无意义且打断输入。同步在 NavigateToAsyncCore 末尾、路径被服务端确认后做。
        GoUpCommand.NotifyCanExecuteChanged();
        OpenTerminalCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        PasteFromHostCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoUp));
    }

    partial void OnSelectedEntryChanged(FileSystemEntryDto? value)
    {
        NotifySelectionCommands();
        OpenTerminalCommand.NotifyCanExecuteChanged();
        ConfirmPickerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
        if (IsPickerMode && !AllowMultipleFiles)
        {
            SelectedEntries.Clear();
            if (value is not null) SelectedEntries.Add(value);
            UpdatePickerEntryName();
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        PasteCommand.NotifyCanExecuteChanged();
        PasteFromHostCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFilterChanged(ExplorerFileFilter? value)
    {
        if (_pickerInitialized && IsFilePickerMode && !IsBusy)
            _ = RefreshAsync();
    }

    partial void OnPickerEntryNameChanged(string value)
    {
        if (_isUpdatingPickerText) return;
        SelectedEntries.Clear();
        ConfirmPickerCommand.NotifyCanExecuteChanged();
    }

    // ---- 导航 ----

    public async Task NavigateToAsync(string? path)
    {
        await NavigateToAsyncCore(path);
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(path);
        _historyIndex = _history.Count - 1;
        RefreshHistoryCommands();
    }

    private async Task NavigateToAsyncCore(string? path)
    {
        IsBusy = true;
        try
        {
            Entries.Clear();
            SelectedEntries.Clear();
            SelectedEntry = null;
            UpdatePickerEntryName();
            string? confirmedPath;
            if (path is null)
            {
                var drives = GetNavigationDrives(await _client.GetDrivesAsync());
                foreach (var d in drives)
                    Entries.Add(new FileSystemEntryDto(d.Path, d.Name, d.TotalSize,
                        FileSystemEntryType.Drive, null, null, null, false, false, null));
                confirmedPath = null;
                AddressbarPath = null;
                StatusText = LocalizedText.Format("explorer.status.drives_ready", drives.Count);
            }
            else
            {
                var dir = await _client.GetDirectoryAsync(path);
                foreach (var d in dir.Directories) Entries.Add(d);
                if (!IsFolderPickerMode)
                {
                    foreach (var f in dir.Files.Where(f => !IsFilePickerMode || MatchesSelectedFilter(f.Name)))
                        Entries.Add(new FileSystemEntryDto(f.Path, f.Name, f.Size, FileSystemEntryType.File,
                            f.Created, f.Modified, f.Accessed, f.IsHidden, f.IsSystem, f.MimeType));
                }
                confirmedPath = dir.Path;
                AddressbarPath = dir.Path;
                StatusText = LocalizedText.Format("explorer.status.directory_ready", dir.Directories.Count, dir.Files.Count);
            }
            // 路径已由服务端确认，反向同步树选中（防循环：被 _isSyncingTreeSelection 抑制）
            await SyncTreeSelectionAsync(confirmedPath);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.load_failed", ex.Message); }
        finally { IsBusy = false; }
    }

    // ---- 树选中同步（防循环） ----

    /// <summary>路径变化后反向同步树选中：找到对应节点，必要时逐级懒加载祖先，最终设 SelectedNode。
    /// 失败不抛（找不到时保持原选中，不阻塞右侧网格已展示内容）。</summary>
    private async Task SyncTreeSelectionAsync(string? path)
    {
        if (_isSyncingTreeSelection) return;
        // 早退：当前已选中节点的 path 与目标一致（如树点击触发的导航场景，避免冗余查找）
        if (SelectedNode is { } current && PathEquals(current.Path, path)) return;

        _isSyncingTreeSelection = true;
        try
        {
            var node = await FindAndExpandNodeAsync(path);
            if (node is not null && !ReferenceEquals(SelectedNode, node))
                SelectedNode = node;   // 触发 OnSelectedNodeChanged，但被 _isSyncingTreeSelection 抑制
        }
        finally { _isSyncingTreeSelection = false; }
    }

    /// <summary>查找路径对应的树节点，必要时逐级懒加载祖先。返回 null 表示未找到（不抛）。</summary>
    private async Task<TreeNodeModel?> FindAndExpandNodeAsync(string? path)
    {
        // null 路径 → 选 "此电脑" 节点（与 NavigateToAsync(null) 的盘符聚合视图对应）
        if (string.IsNullOrEmpty(path))
            return Nodes.FirstOrDefault(n => n.IsComputer);

        // 1) 先在顶层根节点与其快捷入口叶子里精确匹配（O(1) 命中主目录组节点 / 桌面 / 文档等）
        foreach (var root in Nodes)
        {
            if (!root.IsPlaceholder && PathEquals(root.Path, path))
                return root;
            foreach (var child in root.Children)
                if (!child.IsPlaceholder && PathEquals(child.Path, path))
                    return child;
        }

        // 2) 否则按路径分段从"此电脑"下的盘符节点下钻，逐级懒加载祖先
        var thisPc = Nodes.FirstOrDefault(n => n.IsComputer);
        if (thisPc is null) return null;
        var cmp = PathComparison;
        foreach (var drive in thisPc.Children)
        {
            if (drive.IsPlaceholder) continue;
            // 仅当下钻起点是目标路径的祖先时才进入（避免对每个盘符都展开）
            if (!IsAncestorOrEqual(drive.Path, path, cmp)) continue;
            var found = await DescendAsync(drive, path, cmp);
            if (found is not null) return found;
        }
        return null;

        async Task<TreeNodeModel?> DescendAsync(TreeNodeModel start, string target, StringComparison comparison)
        {
            var current = start;
            while (current is not null && !PathEquals(current.Path, target, comparison))
            {
                // 若子节点未懒加载：直接调 OnNodeExpandRequested 并 await（绕过 IsExpanded setter 的 fire-and-forget）
                if (!current.HasLoadedChildren && current.ExpandRequested is not null)
                {
                    await OnNodeExpandRequested(current);
                    current.IsExpanded = true;   // 加载已完成，setter 检测 _hasLoadedChildren 不再 Invoke
                }
                current = current.Children.FirstOrDefault(c =>
                    !c.IsPlaceholder && IsAncestorOrEqual(c.Path, target, comparison));
            }
            return PathEquals(current?.Path, target, comparison) ? current : null;
        }
    }

    // ---- 路径规范化辅助 ----

    /// <summary>路径比较策略：Linux 区分大小写（文件系统大小写敏感），Windows 不区分。</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>规范化路径：去尾部目录分隔符；Linux "/" 根特殊处理（不能 trim 成空串）；非法字符兜底返回原值。</summary>
    private static string? NormalizePath(string? p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        if (p == "/") return p;             // Linux 根特殊处理
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }                  // 非法字符 / 非法路径 fallback
    }

    private static bool PathEquals(string? a, string? b, StringComparison? comparison = null)
        => string.Equals(NormalizePath(a), NormalizePath(b),
            comparison ?? PathComparison);

    /// <summary>
    /// Linux 的 DriveInfo 会把每个挂载点（包括 /dev/shm 与 /dev/pts）都作为一个驱动器返回。
    /// 导航窗格应只有一个 POSIX 根；其余挂载点会在展开相应父目录时以正常目录层级呈现。
    /// 保留没有 POSIX 根的结果，以兼容 Windows 盘符列表和其他服务端实现。
    /// </summary>
    private static IReadOnlyList<DriveDto> GetNavigationDrives(IReadOnlyList<DriveDto> drives)
    {
        var readyDrives = drives.Where(d => d.IsReady).ToArray();
        var posixRoot = readyDrives.FirstOrDefault(d => string.Equals(d.Path, "/", StringComparison.Ordinal));
        return posixRoot is null ? readyDrives : [posixRoot];
    }

    /// <summary>ancestor 是否为 descendant 的祖先或相等（用于下钻时判断子节点是否包含目标路径）。</summary>
    private static bool IsAncestorOrEqual(string? ancestor, string descendant, StringComparison comparison)
    {
        var a = NormalizePath(ancestor);
        var d = NormalizePath(descendant);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(d)) return false;
        if (string.Equals(a, d, comparison)) return true;
        return d.StartsWith(a + '\\', comparison)
            || d.StartsWith(a + '/', comparison);
    }

    /// <summary>Whether a list entry can initiate a move drag.</summary>
    public bool CanDragEntry(FileSystemEntryDto entry)
        => !IsPickerMode && !IsBusy && entry.Type != FileSystemEntryType.Drive;

    /// <summary>Validates a move before advertising the drop target to Avalonia.</summary>
    public bool CanMoveEntryToDirectory(FileSystemEntryDto entry, string targetDirectory)
    {
        if (!CanDragEntry(entry) || string.IsNullOrWhiteSpace(targetDirectory)) return false;

        var destinationPath = CombineRemotePath(targetDirectory, entry.Name);
        if (PathEquals(entry.Path, destinationPath)) return false;

        // A directory cannot be moved into itself or into one of its descendants.
        return entry.Type != FileSystemEntryType.Directory ||
               !IsAncestorOrEqual(entry.Path, targetDirectory, PathComparison);
    }

    /// <summary>Moves a dragged entry into a directory and refreshes the current listing.</summary>
    public async Task MoveEntryToDirectoryAsync(FileSystemEntryDto entry, string targetDirectory)
    {
        if (!CanMoveEntryToDirectory(entry, targetDirectory)) return;

        var destinationPath = CombineRemotePath(targetDirectory, entry.Name);
        IsBusy = true;
        StatusText = LocalizedText.Format("explorer.status.moving", entry.Name);
        try
        {
            if (!await RetryWithOperationElevationAsync(
                    async () => { await _client.MoveAsync(entry.Path, destinationPath, overwrite: false); },
                    FileElevationCapability.Move, ParentDirectory(entry.Path), targetDirectory)) return;
            StatusText = LocalizedText.Format("explorer.status.moved", entry.Name, targetDirectory);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Format("explorer.status.move_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string CombineRemotePath(string directory, string name)
    {
        var separator = directory.Contains('\\') ? '\\' : '/';
        var trimmed = directory.TrimEnd('\\', '/');
        return trimmed.Length == 0
            ? separator + name
            : trimmed + separator + name;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        RefreshHistoryCommands();
        await NavigateToAsyncCore(_history[_historyIndex]);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        if (!CanGoForward) return;
        _historyIndex++;
        RefreshHistoryCommands();
        await NavigateToAsyncCore(_history[_historyIndex]);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(AddressbarPath)) return;
        var parent = Path.GetDirectoryName(AddressbarPath);
        await NavigateToAsync(string.IsNullOrEmpty(parent) ? null : parent);
    }

    [RelayCommand]
    private async Task RefreshAsync()
        => await NavigateToAsyncCore(_history.Count > 0 ? _history[_historyIndex] : AddressbarPath);

    /// <summary>地址栏回车跳转。</summary>
    public async Task AddressbarGoAsync(string? path)
        => await NavigateToAsync(string.IsNullOrWhiteSpace(path) ? null : path.Trim());

    private void RefreshHistoryCommands()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoUp));
    }

    // ---- 双击条目 ----

    public async Task InvokeEntryAsync(FileSystemEntryDto entry)
    {
        if (entry.Type == FileSystemEntryType.Directory || entry.Type == FileSystemEntryType.Drive)
            await NavigateToAsync(entry.Path);
        else if (IsFilePickerMode)
            await ConfirmPickerAsync();
        else
            await OpenEntryAsync(entry);
    }

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void UpdatePickerSelection(IEnumerable<object> selectedItems)
    {
        SelectedEntries.Clear();
        foreach (var entry in selectedItems.OfType<FileSystemEntryDto>())
            SelectedEntries.Add(entry);
        NotifySelectionCommands();
        OnPropertyChanged(nameof(HasSelection));
        if (IsPickerMode) UpdatePickerEntryName();
    }

    [RelayCommand]
    private void CancelPicker() => CancelAction?.Invoke();

    [RelayCommand(CanExecute = nameof(CanConfirmPicker))]
    private async Task ConfirmPickerAsync()
    {
        if (!IsPickerMode || _selectPaths is null) return;

        if (IsSaveFilePickerMode)
        {
            if (string.IsNullOrWhiteSpace(AddressbarPath))
            {
                StatusText = LocalizedText.Get("explorer.status.enter_target_directory_first");
                return;
            }
            if (!IsValidSaveFileName(PickerEntryName))
            {
                StatusText = LocalizedText.Get("explorer.status.file_name_invalid");
                return;
            }

            _selectPaths([CombineRemotePath(AddressbarPath, PickerEntryName.Trim())]);
            return;
        }

        var selected = IsFolderPickerMode
            ? SelectedEntries.Where(IsFolder).Select(entry => entry.Path).ToArray()
            : SelectedEntries.Where(IsSelectableFile).Select(entry => entry.Path).ToArray();

        if (selected.Length == 0 && IsFolderPickerMode && !string.IsNullOrWhiteSpace(AddressbarPath))
            selected = [AddressbarPath];

        if (selected.Length == 0 && IsFilePickerMode && !string.IsNullOrWhiteSpace(PickerEntryName))
        {
            var path = Path.IsPathRooted(PickerEntryName)
                ? PickerEntryName
                : string.IsNullOrWhiteSpace(AddressbarPath)
                    ? PickerEntryName
                    : Path.Combine(AddressbarPath, PickerEntryName);
            try
            {
                var entry = await _client.GetInfoAsync(path);
                if (entry is null || !IsSelectableFile(entry))
                {
                    StatusText = LocalizedText.Get("explorer.status.file_not_selectable");
                    return;
                }
                selected = [entry.Path];
            }
            catch (Exception ex)
            {
                StatusText = LocalizedText.Format("explorer.status.file_select_failed", ex.Message);
                return;
            }
        }

        if (selected.Length > 0)
            _selectPaths(selected);
    }

    private bool IsSelectableFile(FileSystemEntryDto entry)
        => entry.Type == FileSystemEntryType.File && (!IsFilePickerMode || MatchesSelectedFilter(entry.Name));

    private static bool IsValidSaveFileName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
            && trimmed is not "." and not ".."
            && !trimmed.Contains('/')
            && !trimmed.Contains('\\');
    }

    private static bool IsFolder(FileSystemEntryDto entry)
        => entry.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive;

    private bool MatchesSelectedFilter(string name)
        => SelectedFilter is { } filter
            && (filter.Patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name,
                    ignoreCase: !OperatingSystem.IsLinux()))
                || (filter.IncludeExtensionlessFiles && Path.GetExtension(name).Length == 0));

    private void UpdatePickerEntryName()
    {
        if (!IsPickerMode) return;
        _isUpdatingPickerText = true;
        PickerEntryName = IsFolderPickerMode
            ? SelectedEntries.FirstOrDefault(IsFolder)?.Name ?? string.Empty
            : IsSaveFilePickerMode
                ? SelectedEntries.FirstOrDefault(IsSelectableFile)?.Name ?? PickerEntryName
                : string.Join(" ", SelectedEntries.Where(IsSelectableFile).Select(entry => $"\"{entry.Name}\""));
        _isUpdatingPickerText = false;
        ConfirmPickerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenAsync()
    {
        if (SelectedEntry is { } entry && entry.Type == FileSystemEntryType.File)
            await OpenEntryAsync(entry);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenWithSelectedAsync()
    {
        if (SelectedEntry is { } entry && entry.Type == FileSystemEntryType.File)
            await (RequestOpenWithAsync?.Invoke(entry) ?? Task.CompletedTask);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PropertiesAsync()
    {
        if (SelectedEntry is not { } entry) return;
        try
        {
            var properties = await _client.GetPropertiesAsync(entry.Path);
            if (properties is null) { StatusText = LocalizedText.Get("explorer.status.item_missing"); return; }
            await (ShowPropertiesAsync?.Invoke(properties) ?? Task.CompletedTask);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.properties_read_failed", ex.Message); }
    }

    private async Task OpenEntryAsync(FileSystemEntryDto entry)
    {
        try
        {
            if (RequestFileElevationAsync is not null && !await RequestFileElevationAsync(entry.Path, FileElevationCapability.Read)) return;
            if (OpenFileAsync is null) { StatusText = LocalizedText.Get("explorer.status.no_file_opener"); return; }
            await OpenFileAsync(entry);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.file_open_failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
    private async Task OpenTerminalAsync()
    {
        var workingDirectory = SelectedEntry is { } entry && IsFolder(entry)
            ? entry.Path
            : AddressbarPath;
        if (string.IsNullOrWhiteSpace(workingDirectory)) return;

        try
        {
            if (OpenTerminalAtPathAsync is null)
            {
                StatusText = LocalizedText.Get("explorer.status.terminal_unavailable");
                return;
            }
            await OpenTerminalAtPathAsync(workingDirectory);
        }
        catch (Exception ex)
        {
            StatusText = LocalizedText.Format("explorer.status.terminal_start_failed", ex.Message);
        }
    }

    // ---- 文件操作 ----

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (string.IsNullOrEmpty(AddressbarPath))
        {
            StatusText = LocalizedText.Get("explorer.status.enter_directory_first");
            return;
        }
        var name = await (RequestTextInputAsync?.Invoke(LocalizedText.Get("explorer.new_folder"), LocalizedText.Get("explorer.new_folder_prompt"), LocalizedText.Get("explorer.new_folder"), LocalizedText.Get("common.create")) ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var target = Path.Combine(AddressbarPath, name);
            if (!await RetryWithOperationElevationAsync(
                    async () => { await _client.CreateDirectoryAsync(target); }, FileElevationCapability.CreateDirectory, AddressbarPath)) return;
            StatusText = LocalizedText.Format("explorer.status.folder_created", name);
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.create_failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var confirmed = await (RequestConfirmAsync?.Invoke(LocalizedText.Get("common.delete"),
            LocalizedText.Format(IsFolder(entry)
                ? "explorer.delete_confirmation"
                : "explorer.delete_file_confirmation", entry.Name),
            LocalizedText.Get("common.delete")) ?? Task.FromResult(false));
        if (!confirmed) return;
        try
        {
            if (!await RetryWithOperationElevationAsync(
                    async () => { await _client.DeleteAsync(entry.Path); }, FileElevationCapability.Delete, ParentDirectory(entry.Path))) return;
            StatusText = LocalizedText.Format("explorer.status.deleted", entry.Name);
            SelectedEntry = null;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.delete_failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var newName = await (RequestTextInputAsync?.Invoke(LocalizedText.Get("common.rename"), LocalizedText.Get("explorer.rename_prompt"), entry.Name, LocalizedText.Get("common.rename"))
            ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;
        try
        {
            if (!await RetryWithOperationElevationAsync(
                    async () => { await _client.RenameAsync(entry.Path, newName); }, FileElevationCapability.Rename, ParentDirectory(entry.Path))) return;
            StatusText = LocalizedText.Format("explorer.status.renamed", newName);
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.rename_failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        _fileClipboard.Set(GetSelectedEntries(), RemoteFileClipboardOperation.Copy);
        PasteCommand.NotifyCanExecuteChanged();
        StatusText = LocalizedText.Format("explorer.status.copied_to_clipboard", _fileClipboard.Entries.Count);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut()
    {
        _fileClipboard.Set(GetSelectedEntries(), RemoteFileClipboardOperation.Cut);
        PasteCommand.NotifyCanExecuteChanged();
        StatusText = LocalizedText.Format("explorer.status.cut_to_clipboard", _fileClipboard.Entries.Count);
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (string.IsNullOrWhiteSpace(AddressbarPath))
        {
            StatusText = LocalizedText.Get("explorer.status.enter_target_directory_first");
            return;
        }

        if (_fileClipboard.HasEntries)
        {
            await PasteRemoteClipboardAsync(AddressbarPath);
            return;
        }

        await PasteHostClipboardAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteFromHostAsync() => await PasteHostClipboardAsync();

    private bool CanPaste() => !IsBusy && !string.IsNullOrWhiteSpace(AddressbarPath);

    private async Task PasteRemoteClipboardAsync(string targetDirectory)
    {
        var entries = _fileClipboard.Entries
            .Where(entry => CanPlaceEntryInDirectory(entry, targetDirectory))
            .ToArray();
        if (entries.Length == 0)
        {
            StatusText = LocalizedText.Get("explorer.status.paste_no_valid_items");
            return;
        }

        IsBusy = true;
        BeginTransfer(LocalizedText.Format("explorer.status.pasting", 0, entries.Length), entries.Length, 0);
        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                TransferText = LocalizedText.Format("explorer.status.pasting_item", entry.Name, index + 1, entries.Length);
                var destination = CombineRemotePath(targetDirectory, entry.Name);
                var transferred = _fileClipboard.Operation == RemoteFileClipboardOperation.Cut
                    ? await RetryWithOperationElevationAsync(async () => { await _client.MoveAsync(entry.Path, destination, overwrite: false); }, FileElevationCapability.Move, ParentDirectory(entry.Path), targetDirectory)
                    : await RetryWithOperationElevationAsync(async () => { await _client.CopyAsync(entry.Path, destination, overwrite: false); }, FileElevationCapability.Copy, ParentDirectory(entry.Path), targetDirectory);
                if (!transferred) return;
                TransferItemCompleted = index + 1;
            }

            if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                _fileClipboard.Clear();
            StatusText = LocalizedText.Format("explorer.status.paste_completed", entries.Length);
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.paste_failed", ex.Message); }
        finally
        {
            IsTransferActive = false;
            IsBusy = false;
            PasteCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task PasteHostClipboardAsync()
    {
        if (string.IsNullOrWhiteSpace(AddressbarPath))
        {
            StatusText = LocalizedText.Get("explorer.status.enter_target_directory_first");
            return;
        }
        var sources = await (RequestClipboardUploadSourcesAsync?.Invoke() ?? Task.FromResult<IReadOnlyList<LocalUploadSource>>([]));
        if (sources.Count == 0)
        {
            StatusText = LocalizedText.Get("explorer.status.clipboard_no_files");
            return;
        }
        await UploadSourcesAsync(sources, LocalizedText.Get("explorer.status.pasting_host_files"));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveAsync()
    {
        if (SelectedEntry is not { } entry) return;
        var dest = await (RequestTextInputAsync?.Invoke(LocalizedText.Get("common.move"), LocalizedText.Get("explorer.destination_path_prompt"), entry.Path, LocalizedText.Get("common.move"))
            ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(dest) || dest == entry.Path) return;
        try
        {
            if (!await RetryWithOperationElevationAsync(
                    async () => { await _client.MoveAsync(entry.Path, dest, overwrite: false); },
                    FileElevationCapability.Move, ParentDirectory(entry.Path), ParentDirectory(dest))) return;
            StatusText = LocalizedText.Format("explorer.status.moved_to", dest);
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.move_failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DownloadAsync()
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.Type == FileSystemEntryType.Directory || entry.Type == FileSystemEntryType.Drive)
        {
            StatusText = LocalizedText.Get("explorer.status.folder_download_unsupported");
            return;
        }
        var localPath = await (RequestLocalSaveFileAsync?.Invoke(entry.Name) ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(localPath)) return;
        try
        {
            if (RequestFileElevationAsync is not null && !await RequestFileElevationAsync(entry.Path, FileElevationCapability.Read)) return;
            var r = await _client.DownloadAsync(entry.Path);
            if (r is not (var stream, _))
            {
                StatusText = LocalizedText.Get("explorer.status.download_file_missing");
                return;
            }
            using (stream)
            using (var fs = File.Create(localPath))
                await stream.CopyToAsync(fs);
            StatusText = LocalizedText.Format("explorer.status.downloaded", localPath);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.download_failed", ex.Message); }
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        var sources = await (RequestLocalUploadFilesAsync?.Invoke() ?? Task.FromResult<IReadOnlyList<LocalUploadSource>>([]));
        if (sources.Count > 0) await UploadSourcesAsync(sources, LocalizedText.Get("explorer.status.uploading_files"));
    }

    [RelayCommand]
    private async Task UploadFolderAsync()
    {
        var sources = await (RequestLocalUploadFoldersAsync?.Invoke() ?? Task.FromResult<IReadOnlyList<LocalUploadSource>>([]));
        if (sources.Count > 0) await UploadSourcesAsync(sources, LocalizedText.Get("explorer.status.uploading_folders"));
    }

    private async Task UploadSourcesAsync(IReadOnlyList<LocalUploadSource> sources, string operationName)
    {
        if (string.IsNullOrWhiteSpace(AddressbarPath))
        {
            StatusText = LocalizedText.Get("explorer.status.enter_target_directory_first");
            return;
        }

        UploadPlan plan;
        try { plan = BuildUploadPlan(sources); }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.upload_failed", ex.Message); return; }
        if (plan.Files.Count == 0 && plan.Directories.Count == 0)
        {
            StatusText = LocalizedText.Get("explorer.status.no_uploadable_files");
            return;
        }

        IsBusy = true;
        BeginTransfer(operationName, plan.Directories.Count + plan.Files.Count, plan.TotalBytes);
        try
        {
            for (var index = 0; index < plan.Directories.Count; index++)
            {
                var directory = plan.Directories[index];
                TransferText = LocalizedText.Format("explorer.status.creating_folder", directory, index + 1, TransferItemTotal);
                var remoteDirectory = CombineRemoteRelativePath(AddressbarPath, directory);
                if (!await RetryWithOperationElevationAsync(
                        async () => { await _client.CreateDirectoryAsync(remoteDirectory); }, FileElevationCapability.CreateDirectory, AddressbarPath)) return;
                TransferItemCompleted = index + 1;
            }

            long completedBytes = 0;
            for (var index = 0; index < plan.Files.Count; index++)
            {
                var file = plan.Files[index];
                var operationIndex = plan.Directories.Count + index + 1;
                TransferText = LocalizedText.Format("explorer.status.uploading_item", file.RelativePath, operationIndex, TransferItemTotal);
                var currentFileStart = completedBytes;
                var progress = new Progress<long>(uploaded =>
                {
                    TransferBytesCompleted = currentFileStart + uploaded;
                    TransferText = LocalizedText.Format("explorer.status.uploading_item", file.RelativePath, operationIndex, TransferItemTotal);
                });
                var destinationDirectory = GetRelativeDirectory(file.RelativePath);
                var targetDirectory = string.IsNullOrEmpty(destinationDirectory)
                    ? AddressbarPath
                    : CombineRemoteRelativePath(AddressbarPath, destinationDirectory);
                using var stream = File.OpenRead(file.SourcePath);
                if (!await RetryWithOperationElevationAsync(
                        async () => { await _client.UploadAsync(targetDirectory, GetRelativeFileName(file.RelativePath), stream, progress); }, FileElevationCapability.Upload, AddressbarPath)) return;
                completedBytes += file.Length;
                TransferBytesCompleted = completedBytes;
                TransferItemCompleted = operationIndex;
            }

            StatusText = plan.Directories.Count > 0
                ? LocalizedText.Format("explorer.status.upload_completed_with_folders", plan.Files.Count, plan.Directories.Count)
                : LocalizedText.Format("explorer.status.upload_completed", plan.Files.Count);
            await RefreshAsync();
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("explorer.status.upload_failed", ex.Message); }
        finally
        {
            IsTransferActive = false;
            IsBusy = false;
            PasteCommand.NotifyCanExecuteChanged();
        }
    }

    private IReadOnlyList<FileSystemEntryDto> GetSelectedEntries()
        => SelectedEntries.Count > 0 ? SelectedEntries.ToArray()
            : SelectedEntry is null ? [] : [SelectedEntry];

    private bool CanPlaceEntryInDirectory(FileSystemEntryDto entry, string targetDirectory)
    {
        var destination = CombineRemotePath(targetDirectory, entry.Name);
        if (PathEquals(entry.Path, destination)) return false;
        return entry.Type != FileSystemEntryType.Directory ||
               !IsAncestorOrEqual(entry.Path, targetDirectory, PathComparison);
    }

    private void NotifySelectionCommands()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        OpenWithSelectedCommand.NotifyCanExecuteChanged();
        OpenTerminalCommand.NotifyCanExecuteChanged();
        PropertiesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Leaves the first mutation to the host OS. Only its explicit elevation-required response
    /// opens the system authentication flow, then retries once with the JWT-scoped grant.
    /// </summary>
    private async Task<bool> RetryWithOperationElevationAsync(Func<Task> operation, FileElevationCapability capability, params string?[] directoryPaths)
    {
        try
        {
            await operation();
            return true;
        }
        catch (RemoteOsAuthException ex) when (ex.Type.EndsWith("/elevation-required", StringComparison.Ordinal))
        {
            var directories = directoryPaths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!).Distinct(PathStringComparer).ToArray();
            if (directories.Length == 0 || RequestFileOperationElevationAsync is null
                || !await RequestFileOperationElevationAsync(directories, capability))
            {
                StatusText = LocalizedText.Get("explorer.status.elevation_required");
                return false;
            }
            await operation();
            return true;
        }
    }

    private static string ParentDirectory(string path)
        => Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? path;

    private static StringComparer PathStringComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private void BeginTransfer(string text, int itemTotal, long totalBytes)
    {
        TransferText = text;
        TransferItemTotal = itemTotal;
        TransferItemCompleted = 0;
        TransferTotalBytes = totalBytes;
        TransferBytesCompleted = 0;
        IsTransferActive = true;
    }

    private static UploadPlan BuildUploadPlan(IEnumerable<LocalUploadSource> sources)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<UploadFile>();
        foreach (var source in sources.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(source))
            {
                var info = new FileInfo(source);
                files.Add(new UploadFile(source, info.Name, info.Length));
                continue;
            }
            if (!Directory.Exists(source)) continue;

            var root = Path.TrimEndingDirectorySeparator(source);
            if (string.Equals(root, Path.GetPathRoot(root), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                throw new ArgumentException("Selecting a filesystem root for upload is not supported.");
            var parent = Path.GetDirectoryName(root) ?? root;
            directories.Add(Path.GetRelativePath(parent, root));
            foreach (var directory in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
                directories.Add(Path.GetRelativePath(parent, directory));

            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                var info = new FileInfo(file);
                files.Add(new UploadFile(file, Path.GetRelativePath(parent, file), info.Length));
            }
        }

        return new UploadPlan(directories.OrderBy(path => path.Length).ToArray(), files, files.Sum(file => file.Length));
    }

    private sealed record UploadPlan(IReadOnlyList<string> Directories, IReadOnlyList<UploadFile> Files, long TotalBytes);
    private sealed record UploadFile(string SourcePath, string RelativePath, long Length);
    private static string CombineRemoteRelativePath(string directory, string relativePath)
    {
        var result = directory;
        foreach (var segment in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            result = CombineRemotePath(result, segment);
        return result;
    }

    private static string? GetRelativeDirectory(string relativePath)
    {
        var separator = Math.Max(relativePath.LastIndexOf('/'), relativePath.LastIndexOf('\\'));
        return separator < 0 ? null : relativePath[..separator];
    }

    private static string GetRelativeFileName(string relativePath)
    {
        var separator = Math.Max(relativePath.LastIndexOf('/'), relativePath.LastIndexOf('\\'));
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    [RelayCommand]
    private async Task AboutAsync()
    {
        await (ShowMessageAsync?.Invoke(LocalizedText.Get("explorer.about_title"),
            LocalizedText.Get("explorer.about_message"))
            ?? Task.CompletedTask);
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();

}
