using System.ComponentModel;
using Avalonia.Media;

namespace RelaxKonOS.App.Settings.Views;

public interface IPersonalizationOverview : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    ThemePalettePreview PalettePreview { get; }
}

public sealed record ThemePalettePreview(IBrush Background, IBrush Surface, IBrush Accent, IBrush Success, IBrush Danger, string AccentValue);
