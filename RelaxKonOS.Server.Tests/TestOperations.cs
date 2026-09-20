internal static class TestOperations
{
internal static async Task<CertificateOperationDto> WaitForCertificateOperationAsync(CertificateOperationStore operations, Guid id)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var operation = await operations.GetAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("Certificate operation disappeared.");
        if (operation.State is CertificateOperationState.Succeeded or CertificateOperationState.Failed or CertificateOperationState.Cancelled) return operation;
        await Task.Delay(10);
    }
    throw new TimeoutException("Certificate operation did not complete.");
}

internal static async Task<WebServerOperationDto> WaitForWebOperationAsync(WebServerOperationStore operations, Guid id)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var operation = await operations.GetAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("WebServer operation disappeared.");
        if (operation.State is WebServerOperationState.Succeeded or WebServerOperationState.Failed or WebServerOperationState.Cancelled) return operation;
        await Task.Delay(10);
    }
    throw new TimeoutException("WebServer operation did not complete.");
}

}
