using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal sealed record GitGraphEdge(int From, int To, bool Incoming, bool Through, bool Cut = false);
internal sealed record GitGraphRow(int Lane, IReadOnlyList<GitGraphEdge> Edges, int LaneCount, bool IsHead);
internal sealed record GitLogReference(string Name, IBrush Foreground);
internal sealed class GitLogRow(GitCommitDto commit, GitGraphRow graph, IReadOnlyList<GitLogReference> references)
{
    public GitCommitDto Commit { get; } = commit;
    public GitGraphRow Graph { get; } = graph;
    public IReadOnlyList<GitLogReference> References { get; } = references;
    public bool IsMerge { get; init; }
    public string Subject => Commit.Subject;
    public string Author => Commit.Author;
    public string AuthorDate => Commit.AuthorDate;
    public string ToolTip => Commit.Sha + "\n" + Commit.Subject + "\n" + string.Join(", ", References.Select(reference => reference.Name));
}
internal static class GitCommitGraph
{
    public static IReadOnlyList<GitLogRow> Build(IReadOnlyList<GitCommitDto> commits, IReadOnlyList<GitGraphCommitDto> graph,
        IReadOnlyList<GitBranchDto> branches, IReadOnlyList<GitTagDto> tags)
    {
        var parents = graph.ToDictionary(item => item.Sha, item => item.Parents, StringComparer.Ordinal);
        var heads = graph.Where(item => item.IsHead).Select(item => item.Sha).ToHashSet(StringComparer.Ordinal);
        var visible = commits.Select(commit => commit.Sha).ToHashSet(StringComparer.Ordinal);
        var lanes = new List<string?>();
        var rows = new List<GitLogRow>();
        foreach (var commit in commits)
        {
            var lane = lanes.IndexOf(commit.Sha);
            var expected = lane >= 0;
            if (lane < 0) { lane = lanes.IndexOf(null); if (lane < 0) { lane = lanes.Count; lanes.Add(commit.Sha); } else lanes[lane] = commit.Sha; }
            var before = lanes.ToArray();
            lanes[lane] = null;
            var edges = new List<GitGraphEdge>();
            var ancestry = parents.GetValueOrDefault(commit.Sha) ?? [];
            var first = true;
            foreach (var parent in ancestry)
            {
                if (!visible.Contains(parent)) { edges.Add(new(lane, lane, false, false, true)); continue; }
                var target = lanes.IndexOf(parent);
                if (target < 0)
                {
                    target = first && lanes[lane] is null ? lane : lanes.IndexOf(null);
                    if (target < 0) { target = lanes.Count; lanes.Add(parent); } else lanes[target] = parent;
                }
                edges.Add(new(lane, target, false, false)); first = false;
            }
            for (var index = 0; index < before.Length; index++)
            {
                if (before[index] is null) continue;
                if (index == lane) { if (expected) edges.Add(new(lane, lane, true, false)); }
                else edges.Add(new(index, index, false, true));
            }
            var refs = branches.Where(branch => branch.Sha == commit.Sha).Select(branch => new GitLogReference(branch.Name,
                new SolidColorBrush(Color.Parse(branch.IsCurrent ? "#B78924" : branch.IsRemote ? "#A275BE" : "#58A344"))))
                .Concat(tags.Where(tag => tag.Sha == commit.Sha).Select(tag => new GitLogReference(tag.Name, new SolidColorBrush(Color.Parse("#D09B40"))))).ToArray();
            rows.Add(new(commit, new(lane, edges, Math.Max(before.Length, lanes.Count), heads.Contains(commit.Sha)), refs) { IsMerge = ancestry.Count > 1 });
            while (lanes.Count > 0 && lanes[^1] is null) lanes.RemoveAt(lanes.Count - 1);
        }
        return rows;
    }
}
internal sealed class GitCommitGraphCell : Control
{
    public static readonly StyledProperty<GitGraphRow?> RowProperty = AvaloniaProperty.Register<GitCommitGraphCell, GitGraphRow?>(nameof(Row));
    public GitGraphRow? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    static GitCommitGraphCell() => AffectsRender<GitCommitGraphCell>(RowProperty);
    private static readonly IBrush[] Colors = [new SolidColorBrush(Color.Parse("#6DB33F")), new SolidColorBrush(Color.Parse("#3EB09C")), new SolidColorBrush(Color.Parse("#9B70C7")), new SolidColorBrush(Color.Parse("#529BD3")), new SolidColorBrush(Color.Parse("#D1A34E"))];
    public override void Render(DrawingContext context)
    {
        base.Render(context); if (Row is not { } row) return;
        double X(int lane) => 10 + lane * 13;
        var middle = Bounds.Height / 2;
        foreach (var edge in row.Edges)
        {
            var pen = new Pen(Colors[edge.From % Colors.Length], 1.2, edge.Cut ? DashStyle.Dash : null);
            var from = new Point(X(edge.From), edge.Incoming || edge.Through ? 0 : middle);
            var to = new Point(X(edge.To), edge.Incoming ? middle : edge.Cut ? middle + 7 : Bounds.Height);
            context.DrawLine(pen, from, to);
        }
        context.DrawEllipse(Colors[row.Lane % Colors.Length], null, new Point(X(row.Lane), middle), row.IsHead ? 4.7 : 3.6, row.IsHead ? 4.7 : 3.6);
        if (row.IsHead) context.DrawEllipse(Brushes.White, null, new Point(X(row.Lane), middle), 2.2, 2.2);
    }
}
