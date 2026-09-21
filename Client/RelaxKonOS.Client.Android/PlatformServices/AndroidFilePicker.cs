namespace RelaxKonOS.Client.Android;

/// <summary>M0 marker for the Activity Result / content-URI boundary. File features must consume streams, never assumed local paths.</summary>
public interface IAndroidFilePicker
{
    Task<Stream?> PickReadStreamAsync(CancellationToken cancellationToken = default);
}
