using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>File operations required by an editor workspace and its document tabs.</summary>
public interface IEditorWorkspaceFiles
{
    /// <summary>列举目录内容（GET /files/list）。path 为空表示盘符根。</summary>
    Task<DirectoryDto> GetDirectoryAsync(string? path, CancellationToken ct = default);

    /// <summary>读取远程文件的原始字节；文件不存在时返回 null。</summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>以原始字节覆盖保存远程文件。</summary>
    Task<FileEntryDto> WriteFileAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>创建目录（POST /files/directory）。</summary>
    Task<FileSystemEntryDto> CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>删除文件或目录（DELETE /files）。</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>同目录重命名（POST /files/rename）。</summary>
    Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken ct = default);
}
