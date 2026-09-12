using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed partial class AliasOperationDialogViewModel(string operation, AliasConfigurationDto configuration, Action<object?> close) : ObservableObject
{
    [ObservableProperty] private string alias = configuration.Alias ?? "";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string confirmation = "";
    [ObservableProperty] private string currentPassword = "";
    [ObservableProperty] private bool useSystemPassword;
    [ObservableProperty] private string error = "";
    public bool ShowAlias => operation is "create" or "rename";
    public bool ShowNewPassword => operation is "create" or "password";
    public bool CanChooseMethod => operation is "rename" or "password" && configuration.SystemLoginEnabled;
    public string Explanation => LocalizedText.Get("settings.account.confirm." + operation);
    public string CurrentPasswordLabel => LocalizedText.Get(operation is "create" or "delete" || operation == "toggle" && !configuration.SystemLoginEnabled
        ? "settings.account.system_password" : operation == "toggle" ? "settings.account.alias_password" : "settings.account.current_password");
    [RelayCommand]
    private void Submit()
    {
        if (string.IsNullOrEmpty(CurrentPassword) || ShowNewPassword && NewPassword != Confirmation)
        { Error = LocalizedText.Get("settings.account.password_mismatch"); return; }
        var proof = new AliasReauthentication(UseSystemPassword && CanChooseMethod ? "system" : "alias", CurrentPassword);
        object request = operation switch
        {
            "create" => new CreateAliasRequest(Alias, NewPassword, CurrentPassword, configuration.Revision),
            "rename" => new RenameAliasRequest(Alias, proof, configuration.Revision),
            "password" => new ChangeAliasPasswordRequest(NewPassword, proof, configuration.Revision),
            "delete" => new DeleteAliasRequest(CurrentPassword, configuration.Revision),
            "toggle" => new SetSystemLoginRequest(!configuration.SystemLoginEnabled, CurrentPassword, configuration.Revision),
            _ => throw new InvalidOperationException()
        };
        Clear(); close(request);
    }
    [RelayCommand] private void Cancel() { Clear(); close(null); }
    public void Clear() { NewPassword = ""; Confirmation = ""; CurrentPassword = ""; Error = ""; }
}
