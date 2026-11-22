using System.Windows.Input;
using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ServerModuleSecurityView : UserControl
{
    public ServerModuleSecurityView() => InitializeComponent();
}

public interface IServerModuleSecurity : IServerModuleReview
{
    bool CanManage { get; }
    bool HealthToneOk { get; }
    bool HealthToneBad { get; }
    bool HasHealthReport { get; }
    string HealthSummaryText { get; }
    string HealthCheckedText { get; }
    IEnumerable<IServerModuleHealthRow> HealthRows { get; }
    string PrivilegeStateText { get; }
    string ConsentCountText { get; }
    ICommand CheckHealthCommand { get; }
    ICommand OpenPrivilegeReviewCommand { get; }
    ICommand OpenConsentEditorCommand { get; }
}

public interface IServerModuleHealthRow
{
    string ModuleId { get; }
    string ProblemText { get; }
    bool HasProblem { get; }
    string VersionText { get; }
    bool ToneOk { get; }
    bool ToneBad { get; }
    string StatusText { get; }
}
