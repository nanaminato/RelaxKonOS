namespace RelaxKonOS.App.Tailscale;

public sealed record TailscaleExitNodeItem(string Ip, string HostName, string Addresses)
{
    public string Label => HostName.Length > 0 ? HostName + " · " + Addresses : Ip;
    public override string ToString() => Label;
}
