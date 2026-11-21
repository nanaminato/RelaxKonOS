using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Tunnels;

public abstract class TunnelObservableObject(Func<string, string> localize) : ObservableObject
{
    protected Func<string, string> Localize { get; } = localize;

    protected string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Localize(key), arguments);

    protected TunnelMessage Message(string key, params object?[] arguments) =>
        new(() => arguments.Length == 0 ? Localize(key) : string.Format(CultureInfo.CurrentCulture, Localize(key), arguments));

    protected TunnelMessage Problem(Exception failure) => Problem((failure as TunnelRequestException)?.ProblemCode);

    protected TunnelMessage Problem(string? code) => new(() =>
    {
        var key = $"tunnels.problem.{code}";
        var text = Localize(key);
        return text == key ? Localize("tunnels.status.failed") : text;
    });

    public void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) =>
        OnPropertyChanged(string.Empty);
}

public readonly struct TunnelMessage(Func<string>? resolve)
{
    public string Resolve() => resolve?.Invoke() ?? string.Empty;

    public static implicit operator string(TunnelMessage message) => message.Resolve();

    public override string ToString() => Resolve();
}
