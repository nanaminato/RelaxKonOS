using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IServerModuleConsentReview : IServerModuleReview
{
    bool CanManage { get; }
    bool CanPrepareConsent { get; }
    bool ShowConsentContentSha256 { get; }
    string ConsentContentSha256 { get; set; }
    string ConsentCountText { get; }
    decimal ConsentHours { get; set; }
    ServerModuleConsentKindChoice ConsentKind { get; set; }
    IReadOnlyList<ServerModuleConsentKindChoice> ConsentKinds { get; }
    string ConsentModuleText { get; }
    string ConsentReason { get; set; }
    IReadOnlyList<ServerModuleConsentRow> ConsentRows { get; }
    ServerModuleConsentRow? SelectedConsentRow { get; set; }
    string ConsentTarget { get; set; }
    string ConsentText { get; }
    ICommand PrepareConsentCommand { get; }
    ICommand ApproveConsentCommand { get; }
    ICommand RefreshConsentsCommand { get; }
    ICommand RevokeConsentCommand { get; }
}

public sealed record ServerModuleConsentKindChoice(string Kind, string Action, string LabelKey, Func<string, string> Localize)
{
    public string Label => Localize(LabelKey);
}

public sealed record ServerModuleConsentRow(Guid ConsentId, string ModuleId, string Target, string State);
