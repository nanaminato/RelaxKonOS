using System.Windows.Input;
using Avalonia.Controls;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentHostObservationView : UserControl
{
    public ComponentHostObservationView() => InitializeComponent();
}

public interface IComponentHostObservation
{
    ICommand ObserveHostCommand { get; }
    bool CanObserveHost { get; }
    bool IsObservingHost { get; }
    bool HasHostObservation { get; }
    string HostObservationPlatformText { get; }
    string HostObservationManagementText { get; }
    string HostObservationVersionText { get; }
    string HostObservationStateText { get; }
    string HostObservationNoteText { get; }
    string HostObservationNotice { get; }
}
