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
    ];
    [ObservableProperty] private ServerModuleConsentKindChoice consentKind =
        new(ModuleCapabilityCatalog.ServiceManage, "restart", "server_modules.capability.service_manage.restart");
    [ObservableProperty] private string consentTarget = "";
    [ObservableProperty] private string consentReason = "";
    [ObservableProperty] private decimal consentHours = 1;
    [ObservableProperty] private string consentText = "";
    [ObservableProperty] private IReadOnlyList<ModulePrivilegeConsent> consents = [];
    [ObservableProperty] private ModulePrivilegeConsent? selectedConsent;
    [ObservableProperty] private ModulePrivilegeConsent? consentPlan;
    partial void OnConsentKindChanged(ServerModuleConsentKindChoice value) => ClearConsentReview();
    partial void OnConsentTargetChanged(string value) => ClearConsentReview();
    partial void OnConsentReasonChanged(string value) => ClearConsentReview();
    partial void OnConsentHoursChanged(decimal value) => ClearConsentReview();
    partial void OnSelectedConsentChanged(ModulePrivilegeConsent? value)
    {
        if (value?.ConsentId != ConsentPlan?.ConsentId) ConsentPlan = null;
        ConsentText = value is null ? "" : DescribeConsent(value);
    }
    private void ClearConsentReview() { ConsentPlan = null; ConsentText = ""; }
    private void InvalidateConsents() { ClearConsentReview(); Consents = []; SelectedConsent = null; ConsentTarget = ""; ConsentReason = ""; }
    private static string DescribeConsent(ModulePrivilegeConsent consent) =>
        $"{consent.ModuleId} · {consent.Version} · {consent.PublisherId}\n{consent.ArtifactSha256}\n"
        + $"{consent.Kind} · {consent.Action} · {consent.Target}\n{consent.Reason}\n{consent.State} · {consent.ExpiresAt:g}\n{consent.ConsentId}";

    [RelayCommand] private Task PrepareConsentAsync() => Run(async () =>
    {
        if (Selected is not { } module || ConsentHours is <= 0 or > 720 || string.IsNullOrWhiteSpace(ConsentTarget)
            || string.IsNullOrWhiteSpace(ConsentReason)) throw new InvalidOperationException("module.privilege_consent_invalid");
        var prepared = await client.PrepareConsentAsync(new(module.Id, ConsentKind.Kind, ConsentKind.Action, ConsentTarget.Trim(),
            ConsentReason.Trim(), DateTimeOffset.UtcNow.AddHours((double)ConsentHours), Guid.NewGuid().ToString("N")));
        var review = await client.ReadConsentAsync(prepared.ConsentId);
        ConsentPlan = review;
        Consents = Consents.Where(c => c.ConsentId != review.ConsentId).Append(review).ToArray();
        SelectedConsent = review;
    });
    [RelayCommand] private Task ApproveConsentAsync() => Run(async () =>
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
    [RelayCommand] private Task RevokeConsentAsync() => Run(async () =>
    {
        if (SelectedConsent is not { } consent) return;
        SelectedConsent = await client.RevokeConsentAsync(consent.ConsentId);
        ConsentPlan = null; PrivilegePassword = "";
        Consents = await client.ReadConsentsAsync();
    });
}
