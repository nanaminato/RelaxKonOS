using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>关于页：展示可核验的项目链接，并在离线状态下提供随客户端打包的法律文本。</summary>
public sealed partial class AboutPageViewModel : SettingsPageViewModel
{
    private static readonly Uri OfficialWebsiteUri = new("https://relaxkonos.app/");
    private static readonly Uri SourceRepositoryUri = new("https://github.com/nanaminato/RelaxKonOS");

    public AboutPageViewModel(ShellSettings settings) : base(settings, save: null) { }

    public override string Route => "about";
    public override string DisplayNameKey => "settings.page.about";
    public override string DisplayName => "About RelaxKonOS";

    public string ProductName => "RelaxKonOS";
    public string AppVersion => "0.1";
    public string OfficialWebsite => OfficialWebsiteUri.AbsoluteUri;
    public string SourceRepository => SourceRepositoryUri.AbsoluteUri;
    public string LicenseName => "RelaxKonOS Non-Commercial Source-Available License";
    public string Copyright => "Copyright © 2026 RelaxKonOS. All rights reserved.";
    public LegalTextDocument ProjectLicense { get; } = new("settings.about_page.license", "Assets/Legal/LICENSE.txt");
    public LegalTextDocument ThirdPartyNotices { get; } = new("settings.about_page.third_party", "Assets/Legal/THIRD_PARTY_NOTICES.md");

    public Func<Uri, Task>? RequestOpenUriAsync { get; set; }
    public Func<string, Task>? RequestCopyTextAsync { get; set; }
    public Func<LegalTextDocument, Task>? RequestLegalDocumentAsync { get; set; }

    [RelayCommand] private Task OpenOfficialWebsiteAsync() => RequestOpenUriAsync?.Invoke(OfficialWebsiteUri) ?? Task.CompletedTask;
    [RelayCommand] private Task OpenSourceRepositoryAsync() => RequestOpenUriAsync?.Invoke(SourceRepositoryUri) ?? Task.CompletedTask;
    [RelayCommand] private Task CopyOfficialWebsiteAsync() => RequestCopyTextAsync?.Invoke(OfficialWebsite) ?? Task.CompletedTask;
    [RelayCommand] private Task CopySourceRepositoryAsync() => RequestCopyTextAsync?.Invoke(SourceRepository) ?? Task.CompletedTask;
    [RelayCommand] private Task ViewProjectLicenseAsync() => RequestLegalDocumentAsync?.Invoke(ProjectLicense) ?? Task.CompletedTask;
    [RelayCommand] private Task ViewThirdPartyNoticesAsync() => RequestLegalDocumentAsync?.Invoke(ThirdPartyNotices) ?? Task.CompletedTask;
}

/// <summary>Legal text embedded in the application so users can inspect it without a network connection.</summary>
public sealed class LegalTextDocument(string titleKey, string assetPath)
{
    public string LocalizedTitle => App.Services.GetRequiredService<LocalizationService>().Get(titleKey, titleKey);

    public async Task<string> LoadAsync()
    {
        await using var stream = AssetLoader.Open(new Uri($"avares://RelaxKonOS.Client/{assetPath}"));
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

public sealed class LegalTextDialogViewModel(string title, string text, Action close)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public IRelayCommand CloseCommand { get; } = new RelayCommand(close);
}
