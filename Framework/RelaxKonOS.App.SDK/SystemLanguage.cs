namespace RelaxKonOS.AppSDK;

/// <summary>
/// Read-only system language capability exposed to package applications.
/// </summary>
public interface ISystemLanguage
{
    /// <summary>BCP-47 culture name selected for the current RelaxKonOS workspace.</summary>
    string CurrentLanguage { get; }

    /// <summary>Raised after the workspace language has changed.</summary>
    event EventHandler<SystemLanguageChangedEventArgs>? LanguageChanged;
}

/// <summary>Describes a system language change.</summary>
public sealed class SystemLanguageChangedEventArgs(string previousLanguage, string currentLanguage) : EventArgs
{
    public string PreviousLanguage { get; } = previousLanguage;
    public string CurrentLanguage { get; } = currentLanguage;
}
