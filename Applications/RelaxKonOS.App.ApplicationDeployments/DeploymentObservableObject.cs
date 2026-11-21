using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ApplicationDeployments;

public abstract class DeploymentObservableObject(Func<string, string> localize) : ObservableObject
{
    protected Func<string, string> Localize { get; } = localize;

    protected string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Localize(key), arguments);

    protected DeploymentMessage Message(string key, params object?[] arguments) =>
        new(() => arguments.Length == 0 ? Localize(key) : Format(key, arguments));

    public void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) =>
        OnPropertyChanged(string.Empty);
}

public readonly struct DeploymentMessage(Func<string>? resolve)
{
    public bool IsEmpty => resolve is null;

    public static DeploymentMessage Literal(string? value) =>
        string.IsNullOrEmpty(value) ? default : new(() => value);

    public static DeploymentMessage Join(string separator, DeploymentMessage[] parts) =>
        new(() => string.Join(separator, parts.Select(part => part.Resolve())));

    public string Resolve() => resolve?.Invoke() ?? string.Empty;

    public static implicit operator string(DeploymentMessage message) => message.Resolve();

    public override string ToString() => Resolve();
}
