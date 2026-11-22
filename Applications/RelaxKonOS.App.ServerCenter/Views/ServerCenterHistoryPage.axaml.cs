using Avalonia.Controls;
using Avalonia.Data;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ServerCenter.Views;

public partial class ServerCenterHistoryPage : UserControl
{
    public ServerCenterHistoryPage(IServerCenterHistoryPresentation presentation)
    {
        InitializeComponent();
        DataContext = presentation;
        BindHeader(0, nameof(IServerCenterHistoryPresentation.HistoryTimeHeader));
        BindHeader(1, nameof(IServerCenterHistoryPresentation.HistoryKindHeader));
        BindHeader(2, nameof(IServerCenterHistoryPresentation.HistoryStateHeader));
        BindHeader(3, nameof(IServerCenterHistoryPresentation.HistoryMessageHeader));

        void BindHeader(int index, string property) =>
            HistoryGrid.Columns[index].Bind(DataGridColumn.HeaderProperty, new Binding(property) { Source = presentation });
    }
}
