using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;

namespace RelaxKonOS.App.Settings.Views;

public interface IPersonalizationBackgroundPage
{
    IPersonalizationBackground Editor { get; }
}

public interface IPersonalizationBackground : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IReadOnlyList<WallpaperPreview> Wallpapers { get; }
    int WallpaperIndex { get; set; }
    ICommand ChooseImageCommand { get; }
    ICommand ResetBackgroundCommand { get; }
}

public sealed record WallpaperPreview(string Name, IBrush Brush);
