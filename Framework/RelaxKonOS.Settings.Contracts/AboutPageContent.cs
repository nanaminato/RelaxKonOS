using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings;

public sealed partial class AboutPageContent(Func<string, string> localize) : ObservableObject
{
    private static readonly Uri OfficialWebsiteUri = new("https://relaxkonos.com/");
    private static readonly Uri AllProductsWebsiteUri = new("https://relaxkon.com/");
    private static readonly Uri SourceRepositoryUri = new("https://github.com/nanaminato/RelaxKonOS");


    public Func<string, string> Labels => localize;

    public void RefreshLanguage() => OnPropertyChanged(string.Empty);

    public string ProductName => "RelaxKonOS";
    public string AppVersion => "0.1";
    public string OfficialWebsite => OfficialWebsiteUri.AbsoluteUri;
    public string AllProductsWebsite => AllProductsWebsiteUri.AbsoluteUri;
    public string SourceRepository => SourceRepositoryUri.AbsoluteUri;
    public string LicenseName => "RelaxKonOS Non-Commercial Source-Available License";
    public string Copyright => "Copyright © 2026 RelaxKonOS. All rights reserved.";
    public LegalTextDocument ProjectLicense { get; } = new("settings.about_page.license", "Assets/Legal/LICENSE.txt");
    public LegalTextDocument ThirdPartyNotices { get; } = new("settings.about_page.third_party", "Assets/Legal/THIRD_PARTY_NOTICES.md");

    public Func<Uri, Task>? RequestOpenUriAsync { get; set; }
    public Func<string, Task>? RequestCopyTextAsync { get; set; }
    public Func<LegalTextDocument, Task>? RequestLegalDocumentAsync { get; set; }

    [RelayCommand] private Task OpenOfficialWebsiteAsync() => RequestOpenUriAsync?.Invoke(OfficialWebsiteUri) ?? Task.CompletedTask;
    [RelayCommand] private Task OpenAllProductsWebsiteAsync() => RequestOpenUriAsync?.Invoke(AllProductsWebsiteUri) ?? Task.CompletedTask;
    [RelayCommand] private Task OpenSourceRepositoryAsync() => RequestOpenUriAsync?.Invoke(SourceRepositoryUri) ?? Task.CompletedTask;
    [RelayCommand] private Task CopyOfficialWebsiteAsync() => RequestCopyTextAsync?.Invoke(OfficialWebsite) ?? Task.CompletedTask;
    [RelayCommand] private Task CopyAllProductsWebsiteAsync() => RequestCopyTextAsync?.Invoke(AllProductsWebsite) ?? Task.CompletedTask;
    [RelayCommand] private Task CopySourceRepositoryAsync() => RequestCopyTextAsync?.Invoke(SourceRepository) ?? Task.CompletedTask;
    [RelayCommand] private Task ViewProjectLicenseAsync() => RequestLegalDocumentAsync?.Invoke(ProjectLicense) ?? Task.CompletedTask;
    [RelayCommand] private Task ViewThirdPartyNoticesAsync() => RequestLegalDocumentAsync?.Invoke(ThirdPartyNotices) ?? Task.CompletedTask;
}
