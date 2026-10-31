using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.Client.Apps.Tailscale.Views;

/// <summary>Editor for the closed official preference set. The risk confirmation follows on apply.</summary>
public partial class TailscalePreferencesDialog : UserControl
{
    private readonly Action<TailscalePreferences?> _complete;
    public TailscalePreferencesDialog(TailscalePreferences preferences, IReadOnlyList<TailscaleExitNodeItem> exitNodes,
        string? platform, LocalizationService language, Action<TailscalePreferences?> complete)
    {
        InitializeComponent();
        _complete = complete;
        var model = new TailscalePreferencesEditor(preferences, exitNodes, platform, language);
        DataContext = model;
        AttachedToVisualTree += (_, _) => language.LanguageChanged += model.Changed;
        DetachedFromVisualTree += (_, _) => language.LanguageChanged -= model.Changed;
    }
    private void Cancel(object? sender, RoutedEventArgs e) => _complete(null);
    private void Apply(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TailscalePreferencesEditor editor) { _complete(null); return; }
        _complete(editor.Build());
    }
}

public sealed record TailscaleExitNodeChoice(string? Ip, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class TailscalePreferencesEditor : ObservableObject
{
    private readonly LocalizationService _language;
    private readonly IReadOnlyList<TailscaleExitNodeItem> _candidates;
    [ObservableProperty] private bool _allowIncoming;
    [ObservableProperty] private bool _acceptDns;
    [ObservableProperty] private bool _acceptRoutes;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _unattended;
    [ObservableProperty] private bool _advertiseExitNode;
    [ObservableProperty] private bool _lanAccess;
    [ObservableProperty] private TailscaleExitNodeChoice? _selectedExitNode;
    public IReadOnlyList<TailscaleExitNodeChoice> ExitNodeChoices { get; }
    public bool CanEditUnattended => _platform == "windows";
    public bool HasExitNode => SelectedExitNode?.Ip is { Length: > 0 };
    public bool NoCandidates => _candidates.Count == 0;
    private readonly string? _platform;

    public TailscalePreferencesEditor(TailscalePreferences preferences, IReadOnlyList<TailscaleExitNodeItem> candidates,
        string? platform, LocalizationService language)
    {
        _language = language;
        _platform = platform;
        _candidates = candidates;
        _allowIncoming = !preferences.ShieldsUp;
        _acceptDns = preferences.AcceptDns;
        _acceptRoutes = preferences.AcceptRoutes;
        _autoUpdate = preferences.AutoUpdate;
        _unattended = preferences.Unattended;
        _advertiseExitNode = preferences.AdvertiseExitNode;
        _lanAccess = preferences.ExitNodeAllowLanAccess;
        var choices = new List<TailscaleExitNodeChoice>
        {
            new(null, language.Get("tailscale.exit_node.none", "")),
            new("auto:any", language.Get("tailscale.exit_node.auto", "")),
        };
        foreach (var candidate in candidates)
            choices.Add(new(candidate.Ip, candidate.Label));
        if (preferences.ExitNode is { Length: > 0 } current && choices.All(choice => choice.Ip != current))
            choices.Add(new(current, current));
        ExitNodeChoices = choices;
        _selectedExitNode = choices.FirstOrDefault(choice => choice.Ip == (preferences.ExitNode is { Length: > 0 } exit ? exit : null))
            ?? choices[0];
    }

    partial void OnSelectedExitNodeChanged(TailscaleExitNodeChoice? value)
    {
        OnPropertyChanged(nameof(HasExitNode));
        if (!HasExitNode) { LanAccess = false; }
    }

    public TailscalePreferences Build() => new(AcceptDns, AcceptRoutes, !AllowIncoming,
        SelectedExitNode?.Ip is { Length: > 0 } ip ? ip : null, HasExitNode && LanAccess,
        CanEditUnattended && Unattended, AutoUpdate, AdvertiseExitNode);

    internal void Changed(object? sender, SystemLanguageChangedEventArgs e)
    {
        // The choice labels are proper nouns; only the "none" entry is localized.
        OnPropertyChanged(nameof(ExitNodeChoices));
        OnPropertyChanged(nameof(CanEditUnattended));
        OnPropertyChanged(nameof(NoCandidates));
    }
}
