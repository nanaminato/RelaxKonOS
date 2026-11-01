using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed record ServerModuleConsentKindChoice(string Kind, string Action, string LabelKey)
{
    public LocalizedStatus Label => LocalizedStatus.Key(LabelKey);
}

public sealed partial class ServerModulesViewModel
{
    public IReadOnlyList<ServerModuleConsentKindChoice> ConsentKinds { get; } =
    [
        new(ModuleCapabilityCatalog.ServiceManage, "start", "server_modules.capability.service_manage.start"),
        new(ModuleCapabilityCatalog.ServiceManage, "stop", "server_modules.capability.service_manage.stop"),
        new(ModuleCapabilityCatalog.ServiceManage, "restart", "server_modules.capability.service_manage.restart"),
        new(ModuleCapabilityCatalog.PackageManage, "install", "server_modules.capability.package_manage.install"),
        new(ModuleCapabilityCatalog.PackageManage, "update", "server_modules.capability.package_manage.update"),
        new(ModuleCapabilityCatalog.PackageManage, "repair", "server_modules.capability.package_manage.repair"),
        new(ModuleCapabilityCatalog.FileManage, "write", "server_modules.capability.file_manage.write"),
        new(ModuleCapabilityCatalog.DataManage, "preserve", "server_modules.capability.data_manage.preserve"),
        new(ModuleCapabilityCatalog.ApplicationManage, "query", "server_modules.capability.app_manage.query"),
    ];
    [ObservableProperty] private ServerModuleConsentKindChoice consentKind =
        new(ModuleCapabilityCatalog.ServiceManage, "restart", "server_modules.capability.service_manage.restart");
    [ObservableProperty] private string consentTarget = "";
    [ObservableProperty] private string consentReason = "";
    [ObservableProperty] private string consentContentSha256 = "";
    [ObservableProperty] private decimal consentHours = 1;
    [ObservableProperty] private string consentText = "";
    [ObservableProperty] private IReadOnlyList<ModulePrivilegeConsent> consents = [];
    [ObservableProperty] private ModulePrivilegeConsent? selectedConsent;
    [ObservableProperty] private ModulePrivilegeConsent? consentPlan;
    public bool ShowConsentContentSha256 => ConsentKind.Kind == ModuleCapabilityCatalog.FileManage;
    partial void OnConsentKindChanged(ServerModuleConsentKindChoice value)
    {
        if (value.Kind != ModuleCapabilityCatalog.FileManage) ConsentContentSha256 = "";
        ClearConsentReview();
        OnPropertyChanged(nameof(ShowConsentContentSha256));
    }
    partial void OnConsentTargetChanged(string value) => ClearConsentReview();
    partial void OnConsentReasonChanged(string value) => ClearConsentReview();
    partial void OnConsentContentSha256Changed(string value) => ClearConsentReview();
    partial void OnConsentHoursChanged(decimal value) => ClearConsentReview();
    partial void OnSelectedConsentChanged(ModulePrivilegeConsent? value)
    {
        if (value?.ConsentId != ConsentPlan?.ConsentId) ConsentPlan = null;
        ConsentText = value is null ? "" : DescribeConsent(value);
        OnPropertyChanged(nameof(CanRevokeConsent));
        RevokeConsentCommand.NotifyCanExecuteChanged();
    }
    partial void OnConsentPlanChanged(ModulePrivilegeConsent? value)
    {
        OnPropertyChanged(nameof(CanApproveConsent));
        ApproveConsentCommand.NotifyCanExecuteChanged();
    }
    private void ClearConsentReview() { ConsentPlan = null; ConsentText = ""; }
    private void InvalidateConsents() { ClearConsentReview(); Consents = []; SelectedConsent = null; ConsentTarget = ""; ConsentReason = ""; ConsentContentSha256 = ""; }
    private static string DescribeConsent(ModulePrivilegeConsent consent) =>
        $"{consent.ModuleId} · {consent.Version} · {consent.PublisherId}\n{consent.ArtifactSha256}\n"
        + $"{consent.Kind} · {consent.Action} · {consent.Target}\n{consent.Reason}\n"
        + (consent.ContentSha256 is { Length: 64 } digest ? $"content sha256: {digest}\n" : "")
        + $"{consent.State} · {consent.ExpiresAt:g}\n{consent.ConsentId}";

    [RelayCommand(CanExecute = nameof(CanPrepareConsent))] private Task PrepareConsentAsync() => Run(async () =>
    {
        if (Selected is not { } module || ConsentHours is <= 0 or > 720 || string.IsNullOrWhiteSpace(ConsentTarget)
            || string.IsNullOrWhiteSpace(ConsentReason)) throw new InvalidOperationException("module.privilege_consent_invalid");
        var contentSha256 = ConsentKind.Kind == ModuleCapabilityCatalog.FileManage
            ? ConsentContentSha256.Trim() : null;
        if (contentSha256 is not null && !Digest(contentSha256))
            throw new InvalidOperationException("module.privilege_consent_invalid");
        var prepared = await client.PrepareConsentAsync(new(module.Id, ConsentKind.Kind, ConsentKind.Action, ConsentTarget.Trim(),
            ConsentReason.Trim(), DateTimeOffset.UtcNow.AddHours((double)ConsentHours), Guid.NewGuid().ToString("N"),
            contentSha256));
        var review = await client.ReadConsentAsync(prepared.ConsentId);
        ConsentPlan = review;
        Consents = Consents.Where(c => c.ConsentId != review.ConsentId).Append(review).ToArray();
        SelectedConsent = review;
    });
    [RelayCommand(CanExecute = nameof(CanApproveConsent))] private Task ApproveConsentAsync() => Run(async () =>
    {
        if (ConsentPlan is not { State: "AwaitingConfirmation" } plan || plan.ExpiresAt <= DateTimeOffset.UtcNow
            || plan.CreatedAt.AddMinutes(5) <= DateTimeOffset.UtcNow) throw new InvalidOperationException("module.privilege_plan_expired");
        try
        {
            SelectedConsent = await client.ApproveConsentAsync(plan.ConsentId,
                new(plan.PlanHash, plan.ConfirmationToken, PrivilegeAdministrator, PrivilegePassword));
            ConsentPlan = null;
            Consents = await client.ReadConsentsAsync();
        }
        finally { PrivilegePassword = ""; }
    });
    [RelayCommand] private Task RefreshConsentsAsync() => Run(async () =>
    {
        ClearConsentReview();
        Consents = await client.ReadConsentsAsync();
        SelectedConsent = null;
    });
    private static bool Digest(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    [RelayCommand(CanExecute = nameof(CanRevokeConsent))] private Task RevokeConsentAsync() => Run(async () =>
    {
        if (SelectedConsent is not { } consent) return;
        SelectedConsent = await client.RevokeConsentAsync(consent.ConsentId);
        ConsentPlan = null; PrivilegePassword = "";
        Consents = await client.ReadConsentsAsync();
    });
}
