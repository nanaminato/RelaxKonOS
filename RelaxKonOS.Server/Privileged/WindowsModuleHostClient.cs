using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Dedicated launch transport; no executable, path, credentials or machine secret is exposed to the module.</summary>
internal sealed class WindowsModuleHostClient(PrivilegedHelperOptions options, Action<string>? log) : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private Task? _reader;
    private byte[] _secret = [];
    private string _nonce = "";
    private long _sequence;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Channel<string> _stdout = Channel.CreateBounded<string>(8);

    internal async Task StartAsync(ModuleHostLaunchRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new ModulePackageException("module-host-isolation-unsupported");
        try { _secret = Convert.FromBase64String(options.SharedSecret); }
        catch (FormatException) { throw new ModulePackageException("module-host-helper-unconfigured"); }
        if (_secret.Length < 32 || string.IsNullOrWhiteSpace(options.PipeName))
            throw new ModulePackageException("module-host-helper-unconfigured");
        _pipe = new NamedPipeClientStream(".", ModuleHostPipeProtocol.PipeName(options.PipeName),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Impersonation);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setup.CancelAfter(TimeSpan.FromSeconds(20));
        await _pipe.ConnectAsync(setup.Token);
        var hello = await ModuleHostPipeProtocol.ReadAsync(_pipe, _secret, "", ModuleHostPipeProtocol.HelperDirection, 0, setup.Token);
        if (hello.Type != "hello" || hello.Payload is not { Length: 64 } || !hello.Payload.All(char.IsAsciiHexDigitLower))
            throw new ModulePackageException("module-host-response-invalid");
        _nonce = hello.Payload;
        await ModuleHostPipeProtocol.WriteAsync(_pipe, new(0, "launch", JsonSerializer.Serialize(request, ModuleHostPipeProtocol.Json)),
            _secret, _nonce, ModuleHostPipeProtocol.ClientDirection, setup.Token);
        _sequence = 1;
        _reader = ReadFramesAsync();
    }
    private async Task ReadFramesAsync()
    {
        Exception? failure = null;
        try
        {
            long sequence = 1;
            while (!_stopping.IsCancellationRequested)
            {
                var frame = await ModuleHostPipeProtocol.ReadAsync(_pipe!, _secret, _nonce,
                    ModuleHostPipeProtocol.HelperDirection, sequence++, _stopping.Token);
                switch (frame.Type)
                {
                    case "stdout":
                        if (frame.Payload.Length > 16384 || !_stdout.Writer.TryWrite(frame.Payload))
                            throw new InvalidDataException("module-host-output-overflow");
                        break;
                    case "stderr":
                        if (frame.Payload.Length > 2048) throw new InvalidDataException("module-host-output-overflow");
                        log?.Invoke(frame.Payload);
                        break;
                    case "exit": return;
                    case "faulted":
                        _stdout.Writer.TryWrite(JsonSerializer.Serialize(new ModuleHostFaulted("faulted", "module-host-isolation-failed"), ModuleHostProtocol.Json));
                        return;
                    default: throw new ModulePackageException("module-host-isolation-failed");
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            _stdout.Writer.TryComplete(failure);
            // Any protocol, authentication or output violation closes the Helper channel and its job.
            if (failure is not null) _pipe?.Dispose();
        }
    }
    internal async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        try { return await _stdout.Reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException) { throw new ModulePackageException("module-host-stopped"); }
    }
    internal async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            await ModuleHostPipeProtocol.WriteAsync(_pipe!, new(_sequence++, "command", command), _secret,
                _nonce, ModuleHostPipeProtocol.ClientDirection, cancellationToken);
        }
        finally { _write.Release(); }
    }
    internal async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_pipe is null) return;
        try
        {
            await SendAsync(JsonSerializer.Serialize(new ModuleHostCommand(ModuleHostProtocol.Shutdown), ModuleHostProtocol.Json), cancellationToken);
            if (_reader is not null) await _reader.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        finally { _pipe.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _pipe?.Dispose();
        if (_reader is not null) try { await _reader; } catch (Exception) { }
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_secret);
        _stopping.Dispose();
        _write.Dispose();
    }
}
