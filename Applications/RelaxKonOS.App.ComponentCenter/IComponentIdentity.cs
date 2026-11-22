namespace RelaxKonOS.App.ComponentCenter;

public interface IComponentIdentity : IComponentApplicationLink
{
    string SelectedComponentTitle { get; }
    string SelectedStatusText { get; }
    bool SelectedToneOk { get; }
    bool SelectedToneWarning { get; }
    bool SelectedToneDanger { get; }
    bool SelectedToneIdle { get; }
    bool SelectedHasUpdate { get; }
    bool HasSelectedVersion { get; }
    string SelectedVersionText { get; }
    string SelectedComponentDescription { get; }
    string SelectedCatalogProvenance { get; }
    bool ShowHeaderOpenApplication { get; }
    string SelectedCategoryText { get; }
    string SelectedIntegrationText { get; }
    string SelectedApplicationText { get; }
    bool HasHostPlatformSources { get; }
    bool ShowOtherPlatformSources { get; }
    IReadOnlyList<IComponentSourceChip> SelectedHostSources { get; }
    IReadOnlyList<IComponentSourceChip> SelectedOtherSources { get; }
}

public interface IComponentSourceChip
{
    string Text { get; }
    string Platform { get; }
    bool Ready { get; }
}

public interface IComponentApplicationNotice : IComponentApplicationLink
{
    string NotIntegratedMessage { get; }
    bool CanOpenSelectedApplication { get; }
}

public interface IComponentApplicationLink
{
    string SelectedApplicationOpenText { get; }
}
