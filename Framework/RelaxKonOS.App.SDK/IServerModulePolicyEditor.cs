namespace RelaxKonOS.AppSDK;

public interface IServerModulePolicyEditor : IServerModuleReview
{
    bool CanEditPolicy { get; }
    bool CanSetPolicy { get; }
    decimal IdleGraceSeconds { get; set; }
    IReadOnlyList<ServerModulePolicyChoice> PolicyChoices { get; }
    ServerModulePolicyChoice PolicyChoice { get; set; }
    string ActionTargetText { get; }
    string PolicyHint { get; }
    string SelectedDesiredPolicyText { get; }
    string SelectedEffectivePolicyText { get; }
    void ResetPolicyFromSelection();
}

public sealed record ServerModulePolicyChoice(string Value, Func<string, string, string> Localize)
{
    public string Label => Localize("server_modules.strategy." + Value, Value);
}
