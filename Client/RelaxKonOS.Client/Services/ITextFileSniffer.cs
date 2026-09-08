using RelaxKonOS.Client.Apps.Explorer;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// 嗅探远程文件是否为文本。优先使用服务端列目录时已经写入 <c>MimeType</c> 字段（text/* 直接判真），
/// 退化路径再读前 N KB 做内容判断。由桌面/资源管理器在没有任何应用显式声明支持该扩展名时按需调用，
/// 作为把 <see cref="RelaxKonOS.Core.Applications.ApplicationManifest.SupportsTextFiles"/>
/// 应用加入"打开方式"候选的前置条件。
/// </summary>
public interface ITextFileSniffer
{
    /// <summary>根据服务端返回的 MIME 类型做 O(1) 判断：text/* 直接视为文本；application/octet-stream 无法确定；其余非文本返回 false。</summary>
    bool IsTextByMimeType(string? mimeType);

    /// <summary>返回 <c>true</c> 当远程文件被判定为文本（可被文本编辑器安全打开）。</summary>
    Task<bool> IsTextFileAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// 默认实现：
/// <list type="bullet">
/// <item><see cref="IsTextByMimeType"/>: 按 MIME 前缀 O(1) 判断。</item>
/// <item><see cref="IsTextFileAsync"/>: 通过 <see cref="IExplorerClient.DownloadAsync"/> 读文件首 8KB，
/// 应用经典 binary-detection 算法：
/// BOM → 文本；含 NULL 字节（无 BOM）→ 二进制；可打印 ASCII + 常见空白占比 &lt; 70% → 二进制；其余文本。</item>
/// </list>
/// </summary>
public sealed class TextFileSniffer(IExplorerClient files) : ITextFileSniffer
{
    private const int SniffByteCount = 8 * 1024;

    public bool IsTextByMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return false;
        var span = mimeType.AsSpan().Trim();
        if (span.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        // 常见"本质是文本"但不挂在 text/ 前缀下的 JSON/XML/YAML/TOML/Markdown 类 MIME：
        if (span.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/jsonc", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/json5", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/yaml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/x-yaml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/toml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/x-sh", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/typescript", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/rss+xml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/sql", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/x-sql", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/x-hocon", StringComparison.OrdinalIgnoreCase)
            || span.Equals("application/x-m4b", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<bool> IsTextFileAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        Stream? stream = null;
        try
        {
            var download = await files.DownloadAsync(path, ct);
            if (download is null) return false;
            stream = download.Value.Stream;
            var buffer = new byte[Math.Min(SniffByteCount, 4096)];
            var total = 0;
            int read;
            while (total < SniffByteCount && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total == buffer.Length && total < SniffByteCount)
                    Array.Resize(ref buffer, Math.Min(SniffByteCount, buffer.Length * 2));
            }
            if (total == 0) return true; // 空文件视为空文本。
            return IsTextContent(buffer, total);
        }
        catch
        {
            // 任何 IO/网络错误都视为"无法确认是文本"，保守返回 false 以避免误打开二进制。
            return false;
        }
        finally
        {
            await (stream?.DisposeAsync() ?? ValueTask.CompletedTask);
        }
    }

    private static bool IsTextContent(byte[] buffer, int length)
    {
        // BOM 判定。
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return true;       // UTF-8
        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return true;                              // UTF-16 LE
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return true;                              // UTF-16 BE
        if (length >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF) return true; // UTF-32 BE
        if (length >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00) return true; // UTF-32 LE

        int printable = 0;
        bool hasNull = false;
        for (var i = 0; i < length; i++)
        {
            var b = buffer[i];
            if (b == 0x00) { hasNull = true; break; }
            // ASCII 可打印 (0x20–0x7E) + 常见空白与控制字符。
            if ((b >= 0x20 && b <= 0x7E) || b == '\t' || b == '\n' || b == '\r' || b == '\f' || b == 0x0B) printable++;
            // UTF-8 多字节序列整体宽松计为"可能是文本"：
            // 起始字节 (>=0xC0) 与续字节 (0x80–0xBF) 都计入，避免中文 3 字节 2/3 比例被误杀。
            else if (b >= 0x80) printable++;
        }
        if (hasNull) return false;
        return printable >= (length * 7 / 10); // ≥ 70% 视为文本。
    }
}
