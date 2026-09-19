using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.UI.Themes.SystemStyle;

namespace RelaxKonOS.Client.Views.Shell;

/// <summary>
/// The host's window overview / task switcher surface. It renders whatever
/// <see cref="SystemUiCoordinator"/> projects and never talks to a window, an application or the
/// window manager itself - every action goes back through the coordinator's commands.
/// </summary>
/// <remarks>
/// The three task-switcher recipes (<c>windows-grid</c>, <c>macos-strip</c>, <c>gnome-overview</c>)
/// are selected by mirroring the <c>SystemStyle.TaskSwitcher</c> resource into a class on the root
/// border, so the layout differences live in this file's styles rather than in three forked views.
/// </remarks>
public partial class WindowOverviewView : UserControl
{
    /// <summary>
    /// The active <c>TaskSwitcherRecipe</c>, mirrored from the host's style resource. As with the
    /// window chrome, this is a recipe *selector*: a style can choose a reviewed variant, never
    /// contribute markup, a colour or a size of its own.
    /// </summary>
    private static readonly StyledProperty<object?> RecipeProperty =
        AvaloniaProperty.Register<WindowOverviewView, object?>("Recipe");

    /// <summary>Enter motion, both driven by <c>OverviewEnterDuration</c>. See <see cref="PlayEnterTransition"/>.</summary>
    private readonly DoubleTransition _panelFade = new() { Property = OpacityProperty };
    private readonly TransformOperationsTransition _panelScale = new() { Property = RenderTransformProperty };
    private readonly DoubleTransition _scrimFade = new() { Property = OpacityProperty };

    private static readonly (string Class, string? Recipe)[] RecipeClasses =
    [
        ("recipe-windows-grid", TaskSwitcherRecipes.WindowsGrid),
        ("recipe-macos-strip", TaskSwitcherRecipes.MacOsStrip),
        ("recipe-gnome-overview", TaskSwitcherRecipes.GnomeOverview),
    ];

    static WindowOverviewView()
    {
        RecipeProperty.Changed.AddClassHandler<WindowOverviewView>((view, _) => view.ApplyRecipe());
    }

    public WindowOverviewView()
    {
        InitializeComponent();
        PART_Cards.Transitions = new Transitions { _panelFade, _panelScale };
        PART_Root.Transitions = new Transitions { _scrimFade };
    }

    /// <summary>The coordinator this overview drives; set by the host window when the view is created.</summary>
    public SystemUiCoordinator? Coordinator => DataContext as SystemUiCoordinator;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A resource observable, rather than a one-off lookup, means an overview opened after a
        // style switch already renders the new recipe without being recreated.
        Bind(RecipeProperty, this.GetResourceObservable(SystemStyleRecipeKeys.TaskSwitcher));
        ApplyRecipe();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;

        if (IsVisible)
        {
            PlayEnterTransition();
            // The overview owns the keyboard while it is open. Without this, focus would stay on
            // whichever application control had it and that control would see the keys first.
            Dispatcher.UIThread.Post(OnOverviewShown, DispatcherPriority.Input);
        }
    }

    private void OnOverviewShown()
    {
        if (IsVisible) Focus();
    }

    /// <summary>
    /// Mirrors the recipe into the root's classes; an unknown or missing recipe leaves the default
    /// Windows-like variant in place, so a broken manifest can only ever degrade to the built-in
    /// template.
    /// </summary>
    private void ApplyRecipe()
    {
        var recipe = GetValue(RecipeProperty) as string;
        var known = RecipeClasses.Any(entry => entry.Recipe == recipe);

        foreach (var (className, expected) in RecipeClasses)
            PART_Root.Classes.Set(className, known ? recipe == expected : expected == TaskSwitcherRecipes.WindowsGrid);
    }

    /// <summary>
    /// A single opacity + scale entrance whose duration is the style's <c>OverviewEnterDuration</c>.
    /// The host already collapses every duration token to <c>ReducedMotionDuration</c> when the user
    /// asks for reduced motion, so this animation respects that setting without a second switch here.
    /// </summary>
    private void PlayEnterTransition()
    {
        var duration = this.TryFindResource("OverviewEnterDuration", out var value) && value is TimeSpan span
            ? span
            : TimeSpan.Zero;

        _panelFade.Duration = duration;
        _panelScale.Duration = duration;
        _scrimFade.Duration = duration;

        PART_Root.Opacity = 0;
        PART_Cards.Opacity = 0;
        PART_Cards.RenderTransform = new ScaleTransform(0.985, 0.985);

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            PART_Root.Opacity = 1;
            PART_Cards.Opacity = 1;
            PART_Cards.RenderTransform = new ScaleTransform(1, 1);
        }, DispatcherPriority.Render);
    }

    private void CardOpen_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Coordinator is not { } coordinator) return;
        if ((sender as Control)?.DataContext is not WindowOverviewCard card) return;
        coordinator.ActivateWindow(card.WindowId);
    }

    private void CardClose_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Coordinator is not { } coordinator) return;
        if ((sender as Control)?.DataContext is not WindowOverviewCard card) return;
        coordinator.CloseWindow(card.WindowId);
    }

    /// <summary>
    /// Clicking the dimmed area behind the cards dismisses the overview. Only a press that landed
    /// on the backdrop itself counts: a press inside a card reaches the backdrop through bubbling
    /// and must not close the view the user is working in.
    /// </summary>
    private void Backdrop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PART_Root)) return;
        Coordinator?.HideOverview();
    }
}
