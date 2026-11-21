namespace RelaxKonOS.ModuleHost.Protocol;

public sealed record CloudBaiduDeviceGrant(string DeviceCode, string UserCode, Uri VerificationUri,
    Uri? QrCodeUri, int ExpiresIn, int Interval)
{
    public override string ToString() => "Baidu device grant";
}

public enum CloudBaiduPollStatus { Pending, SlowDown, Denied, Expired, Failed, Authorized }

public sealed record CloudBaiduTokenGrant(string AccessToken, string RefreshToken, int ExpiresIn)
{
    public override string ToString() => "Baidu token grant";
}

public sealed record CloudBaiduPollResult(CloudBaiduPollStatus Status, CloudBaiduTokenGrant? Tokens = null);

public sealed record CloudBaiduRefreshGrant(string Access, string Refresh, DateTimeOffset Expires)
{
    public override string ToString() => "Baidu refreshed token grant";
}
