using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>
/// The log toolbar above the commit list is a single row and never scrolls sideways. When the
/// middle pane narrows, filters leave the strip in priority order and reappear in the overflow
/// flyout, mirroring how the JetBrains log toolbar collapses its own filter controls. The search
/// box is the last item to go: it shrinks to a readable minimum before it joins the flyout.
/// </summary>
internal partial class GitLogView
{
    private const double PreferredSearchWidth = 210;
    // A search box this wide still reads comfortably; filters only start folding below it.
    private const double ComfortableSearchWidth = 180;
    private const double MinimumSearchWidth = 120;
    private const double BranchFilterWidth = 150;
    private const double AuthorFilterWidth = 100;
    private const double DateFilterWidth = 90;
    // The popup has vertical room, so give the same combos enough width to show their labels.
    private const double OverflowFilterWidth = 180;
    // Two pixels keep rounding from producing a one-pixel horizontal clip at the strip edge.
    private const double FitTolerance = 2;

    private Control[] _filterGroups = [];
    private bool _filterBarReady;
    private bool _filterBarLayoutPending;
    private LocalizationService? _localization;

    private void InitializeFilterBar()
    {
        if (_filterGroups.Length == 0)
            _filterGroups = [SearchFilterGroup, BranchFilterGroup, AuthorFilterGroup, DateFilterGroup, PathFilterGroup];
        FilterOverflowPanel.DataContext = DataContext;
        if (!_filterBarReady)
        {
            _filterBarReady = true;
            FilterStripHost.SizeChanged += FilterStripHost_SizeChanged;
        }
        if (_localization is null)
        {
            _localization = App.Services.GetRequiredService<LocalizationService>();
            _localization.LanguageChanged += Localization_LanguageChanged;
        }
        QueueFilterBarLayout();
    }

    private void DetachFilterBar()
    {
        if (FilterOverflowButton.Flyout is { IsOpen: true } open) open.Hide();
        if (_localization is null) return;
        _localization.LanguageChanged -= Localization_LanguageChanged;
        _localization = null;
    }

    private void Localization_LanguageChanged(object? sender, SystemLanguageChangedEventArgs e) => QueueFilterBarLayout();

    private void FilterStripHost_SizeChanged(object? sender, SizeChangedEventArgs e) => QueueFilterBarLayout();

    private void QueueFilterBarLayout()
    {
        if (_filterBarLayoutPending) return;
        _filterBarLayoutPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _filterBarLayoutPending = false;
            if (_attached) ApplyFilterBarLayout();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Chooses which filter groups fit and moves the rest, keeping their real controls (and thus
    /// their bindings and flyouts) into the overflow flyout. Groups are deliberately moved rather
    /// than duplicated so the toolbar and the popup cannot drift out of sync.
    /// </summary>
    private void ApplyFilterBarLayout()
    {
        if (!_filterBarReady || _filterGroups.Length == 0) return;
        var available = FilterStripHost.Bounds.Width;
        if (available <= 0) available = 0;

        // Measure against the toolbar widths, not the wider popup widths a folded group may
        // currently have, so the same group folds and returns at the same window width.
        LogBranchFilter.Width = BranchFilterWidth;
        LogAuthorFilter.Width = AuthorFilterWidth;
        LogDateFilter.Width = DateFilterWidth;

        var preferred = new double[_filterGroups.Length];
        for (var index = 0; index < _filterGroups.Length; index++)
        {
            var group = _filterGroups[index];
            if (ReferenceEquals(group, SearchFilterGroup))
            {
                preferred[index] = PreferredSearchWidth;
                continue;
            }
            group.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            preferred[index] = group.DesiredSize.Width;
        }

        // The overflow entry takes its width from the strip. When every filter would fit in that
        // recovered width, restore them in the same pass; otherwise widening the window would keep
        // one filter folded behind a 27 DIP hysteresis.
        if (FilterOverflowButton.IsVisible && FilterOverflowButton.Bounds.Width > 0)
        {
            var withoutOverflow = available + FilterOverflowButton.Bounds.Width;
            var all = new bool[_filterGroups.Length];
            Array.Fill(all, true);
            if (ContentWidth(preferred, all, ComfortableSearchWidth) <= withoutOverflow - FitTolerance)
                available = withoutOverflow;
        }

        // Hide in increasing importance: date, author, branch, path. Branch carries a label and a
        // 150 DIP combo, so folding it frees enough room for the compact path control; the search
        // box is handled separately so it can shrink instead of disappearing as soon as a label grows.
        var visible = new bool[_filterGroups.Length];
        Array.Fill(visible, true);
        var collapseOrder = new[] { 3, 2, 1, 4 }; // Date, Author, Branch, Path
        // Keep the search box comfortable while collapsing filters; it only shrinks once it is
        // the last inline control, rather than squeezing every filter into a tiny strip.
        while (visible.Count(value => value) > 1
            && ContentWidth(preferred, visible, ComfortableSearchWidth) > available - FitTolerance)
        {
            var collapsed = false;
            foreach (var index in collapseOrder)
            {
                if (!visible[index]) continue;
                visible[index] = false;
                collapsed = true;
                break;
            }
            if (!collapsed) break;
        }

        var searchWidth = PreferredSearchWidth;
        if (visible[0])
        {
            var other = 0d;
            var count = 0;
            for (var index = 1; index < preferred.Length; index++)
            {
                if (!visible[index]) continue;
                other += preferred[index];
                count++;
            }
            var gaps = FilterStrip.Spacing * count;
            if (count == 0 && available - FitTolerance < MinimumSearchWidth) visible[0] = false;
            else searchWidth = Math.Clamp(available - FitTolerance - other - gaps, MinimumSearchWidth, PreferredSearchWidth);
        }

        for (var index = 0; index < _filterGroups.Length; index++)
        {
            var group = _filterGroups[index];
            var target = visible[index] ? (Panel)FilterStrip : FilterOverflowPanel;
            if (!ReferenceEquals(group.Parent, target))
            {
                if (group.Parent is Panel source) source.Children.Remove(group);
                if (ReferenceEquals(group, SearchFilterGroup)) group.Width = visible[index] ? searchWidth : PreferredSearchWidth;
                InsertFilterGroup(target, index, group);
            }
            else if (ReferenceEquals(group, SearchFilterGroup))
            {
                group.Width = visible[index] ? searchWidth : PreferredSearchWidth;
            }
        }

        LogBranchFilter.Width = visible[1] ? BranchFilterWidth : OverflowFilterWidth;
        LogAuthorFilter.Width = visible[2] ? AuthorFilterWidth : OverflowFilterWidth;
        LogDateFilter.Width = visible[3] ? DateFilterWidth : OverflowFilterWidth;

        var hasOverflow = Array.IndexOf(visible, false) >= 0;
        if (!hasOverflow && FilterOverflowButton.Flyout is { IsOpen: true } open) open.Hide();
        FilterOverflowButton.IsVisible = hasOverflow;
        UpdateFilterOverflowIndicator();
    }

    private double ContentWidth(IReadOnlyList<double> preferred, IReadOnlyList<bool> visible, double searchWidth)
    {
        var width = 0d;
        var count = 0;
        for (var index = 0; index < preferred.Count; index++)
        {
            if (!visible[index]) continue;
            width += index == 0 ? searchWidth : preferred[index];
            count++;
        }
        return width + FilterStrip.Spacing * Math.Max(0, count - 1);
    }

    private void InsertFilterGroup(Panel target, int canonicalIndex, Control group)
    {
        var insertion = 0;
        for (var index = 0; index < canonicalIndex; index++)
            if (ReferenceEquals(_filterGroups[index].Parent, target)) insertion++;
        target.Children.Insert(insertion, group);
    }

    private void UpdateFilterOverflowIndicator()
    {
        if (!_filterBarReady) return;
        FilterOverflowDot.IsVisible = _filterGroups.Any(group =>
            ReferenceEquals(group.Parent, FilterOverflowPanel) && IsFilterActive(group));
    }

    private bool IsFilterActive(Control group)
    {
        if (ReferenceEquals(group, SearchFilterGroup)) return !string.IsNullOrWhiteSpace(_vm?.CommitSearchText);
        if (ReferenceEquals(group, BranchFilterGroup)) return !string.IsNullOrEmpty(_vm?.SelectedLogBranch?.Value);
        if (ReferenceEquals(group, AuthorFilterGroup)) return !string.IsNullOrEmpty(_vm?.SelectedLogAuthor?.Value);
        if (ReferenceEquals(group, DateFilterGroup))
            return _vm?.SelectedLogDate?.Value is { Length: > 0 } value
                && !string.Equals(value, "all", StringComparison.OrdinalIgnoreCase);
        if (ReferenceEquals(group, PathFilterGroup)) return !string.IsNullOrWhiteSpace(_vm?.LogPathFilter);
        return false;
    }

    private static bool IsFilterProperty(string? propertyName) => propertyName is
        nameof(GitClientViewModel.CommitSearchText) or
        nameof(GitClientViewModel.SelectedLogBranch) or
        nameof(GitClientViewModel.SelectedLogAuthor) or
        nameof(GitClientViewModel.SelectedLogDate) or
        nameof(GitClientViewModel.LogPathFilter);
}
