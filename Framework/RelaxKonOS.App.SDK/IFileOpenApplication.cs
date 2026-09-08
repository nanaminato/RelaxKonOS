namespace RelaxKonOS.AppSDK;

/// <summary>Implemented by applications that can receive a file path from the file explorer.</summary>
public interface IFileOpenApplication
{
    /// <summary>Open a remote-host file in a new application window.</summary>
    void OpenFile(AppContext context, string path);
}
