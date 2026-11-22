using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Protocol.TaskOrchestration;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.ComponentCenter;

public sealed class TaskPlanConfirmationView : UserControl
{
    public TaskPlanConfirmationView(ModalDialog<bool> dialog, TaskPlan plan,
        Func<string, string> label, Func<string, object?[], string> format,
        Func<TaskStep, (string Title, string Effects)> describeStep)
    {
        Classes.Add("builtin-app");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = label("component_center.task_confirm_hint"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new SelectableTextBlock { Text = format("component_center.task_host", [plan.Semantics.HostId]), TextWrapping = TextWrapping.Wrap });
        foreach (var step in plan.Semantics.Steps)
        {
            var row = describeStep(step);
            content.Children.Add(new TextBlock { Text = (step.RequiredCapabilities.IsEmpty ? "" : "sing-box · ") + row.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = row.Effects, TextWrapping = TextWrapping.Wrap });
            if (!step.RequiredCapabilities.IsEmpty)
            {
                var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs);
                if (frozen.Release is { } release)
                    content.Children.Add(new SelectableTextBlock { Text = format("component_center.task_release", [release.SourceId,
                        release.OriginalVersion, release.Artifact.Sha256]), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = format("component_center.task_capability",
                    [string.Join(", ", step.RequiredCapabilities.Select(item => item.Capability + " / " + item.Target))]), TextWrapping = TextWrapping.Wrap });
                if (frozen.Impact.MayRestartService || frozen.Impact.StopsService)
                    content.Children.Add(new TextBlock { Text = label("component_center.task_service_impact"), TextWrapping = TextWrapping.Wrap });
            }
        }
        content.Children.Add(new SelectableTextBlock { Text = format("component_center.task_digest", [plan.PlanDigest]), TextWrapping = TextWrapping.Wrap });
        var cancel = new Button { Content = label("common.cancel") }; cancel.Click += (_, _) => dialog.Cancel();
        var confirm = new Button { Content = label("component_center.task_approve") }; confirm.Classes.Add("primary"); confirm.Click += (_, _) => dialog.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
        grid.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons); Content = grid;
    }
}
