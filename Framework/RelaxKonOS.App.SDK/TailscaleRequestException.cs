namespace RelaxKonOS.AppSDK;

public sealed class TailscaleRequestException(string code, int status)
    : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
