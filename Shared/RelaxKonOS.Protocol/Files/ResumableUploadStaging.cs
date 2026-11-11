namespace RelaxKonOS.Protocol.Files;

/// <summary>Appends a confirmed chunk and restores the last durable offset after an interrupted write.</summary>
public static class ResumableUploadStaging
{
    public static async Task<long> AppendAsync(string stagingPath, long offset, long expectedBytes,
        Stream content, CancellationToken cancellationToken = default, bool requireEndOfBody = false)
    {
        if (expectedBytes < 0) throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        await using var file = new FileStream(stagingPath, FileMode.Open, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // A process can stop after writing some bytes but before the session index is advanced. Those
        // unconfirmed bytes must be discarded so a restarted client can resend from the indexed offset.
        if (file.Length > offset)
        {
            file.SetLength(offset);
            await file.FlushAsync(cancellationToken);
        }
        if (file.Length != offset)
            throw new IOException($"Staging file length {file.Length} does not match the declared offset {offset}.");
        file.Position = offset;
        try
        {
            var buffer = new byte[81920];
            long written = 0;
            while (written < expectedBytes)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expectedBytes - written)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("The request body is shorter than its declared length.");
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
            }
            if (requireEndOfBody && await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new InvalidDataException("The request body exceeds its declared chunk length.");
            await file.FlushAsync(cancellationToken);
            // Only a flush that returned without error may be reported as confirmed: the caller advances the
            // session offset on the strength of this value alone.
            return file.Length;
        }
        catch
        {
            // Never leave a half-written chunk behind: the next attempt must start from the offset the
            // session still reports, not from the bytes an interrupted attempt happened to leave.
            try { file.SetLength(offset); await file.FlushAsync(CancellationToken.None); }
            catch (IOException) { }
            throw;
        }
    }
}
