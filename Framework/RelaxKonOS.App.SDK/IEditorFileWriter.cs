namespace RelaxKonOS.AppSDK;

/// <summary>Conditional editor saves. A null version means exclusive creation, never overwrite.</summary>
public interface IEditorFileWriter
{
    Task<string> WriteEditorFileAsync(string path, byte[] content, string? expectedVersion, CancellationToken ct = default);
}

public sealed class EditorWriteConflictException(string message) : IOException(message);
