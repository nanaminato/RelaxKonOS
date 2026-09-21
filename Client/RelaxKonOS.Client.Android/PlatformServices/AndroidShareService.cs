namespace RelaxKonOS.Client.Android;

/// <summary>Future download/open handoff boundary. The Mobile layer never directly invokes Android intents.</summary>
public interface IAndroidShareService
{
    Task ShareAsync(Stream content, string displayName, string mediaType, CancellationToken cancellationToken = default);
}
