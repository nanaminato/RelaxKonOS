using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelaxKonOS.ManagedRuntimes.PostgreSql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private static string PgRemoteRoot(PgLedger l) => PgRoot + "/remote-" + l.Instance!.Value.ToString("N");
    private static string PgRemoteTable(PgLedger l) => "rkpg_" + l.Instance!.Value.ToString("N");
    private static string PgRemoteGuard(PgLedger l) => "[Unit]\nAfter=nftables.service ufw.service firewalld.service\n[Service]\nExecStartPre=+/usr/sbin/nft -f " + PgRemoteRoot(l) + "/guard.nft\n";
    private static string PgRemoteDropIn(PgLedger l) => "/etc/systemd/system/" + PostgreSqlInitializationPlanner.Create(l.Instance!.Value,l.Port).ServiceName + ".d";
    private static bool PgRemoteOwnDropIn(PgLedger l)
    {
        var path=PgRemoteDropIn(l); PgNoLinks(path); PgNoLinks(path+"/50-rk-database.conf");
        return Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 1
            && File.Exists(path+"/50-rk-database.conf") && File.ReadAllText(path+"/50-rk-database.conf") == PgRemoteGuard(l);
    }
    private static void PgRemoteCertificate(PostgreSqlRemoteCommand c, PostgreSqlTlsMaterial? material)
    {
        if(material is null || material.CertificatePem is not { Length: > 0 and <= 131072 } || material.PrivateKeyPem is not { Length: > 0 and <= 16384 }) throw new PgFailure("database.tls_invalid");
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
            if(certificate.GetCertHashString(HashAlgorithmName.SHA256) != c.CertificateFingerprint?.ToUpperInvariant()
                || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(1)
                || !certificate.MatchesHostname(c.ServerName!, allowWildcards:false, allowCommonName:false)) throw new PgFailure("database.tls_invalid");
            var usage=certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if(usage is not null && !usage.EnhancedKeyUsages.Cast<Oid>().Any(x=>x.Value=="1.3.6.1.5.5.7.3.1")) throw new PgFailure("database.tls_invalid");
        }
        catch(CryptographicException) { throw new PgFailure("database.tls_invalid"); }
    }
    private static async Task PgRemotePreflight(PgLedger ledger, PostgreSqlRemoteCommand c, PostgreSqlTlsMaterial? tls)
    {
        if(!PostgreSqlRemoteValidation.Valid(c) || ledger.Instance!=c.InstanceId || ledger.Revision!=c.ExpectedRevision
            || c.Enabled == (ledger.RemoteAccess is not null)) throw new PgFailure("database.remote_request_invalid");
        await PgVerifyPackage(ledger); await PgVerifyConfiguration(ledger);
        if(!File.Exists("/usr/sbin/nft")) throw new PgFailure("database.firewall_unavailable");
        if(c.Enabled)
        {
            if(!NetworkInterface.GetAllNetworkInterfaces().SelectMany(x=>x.GetIPProperties().UnicastAddresses).Any(x=>x.Address.ToString()==c.ListenAddress)) throw new PgFailure("database.remote_request_invalid");
            var account=ledger.Accounts.SingleOrDefault(x=>x.Database==c.Database && x.Name==c.Account) ?? throw new PgFailure("database.resource_unmanaged");
            if(account.Permission==PostgreSqlPermission.Migration) throw new PgFailure("database.remote_request_invalid");
            await PgVerifyConnection(ledger); await PgVerifyAccount(ledger,account); PgRemoteCertificate(c,tls);
        }
        var table=await PgRun("/usr/sbin/nft",["-s","list","table","inet",PgRemoteTable(ledger)],allowFailure:true);
        var proof=PgRemoteRoot(ledger)+"/firewall.hash";
        if(table.Code==0 && (!File.Exists(proof)|| c.Enabled && File.ReadAllText(proof)!=PgHash(Encoding.UTF8.GetBytes(table.Output)))) throw new PgFailure("database.firewall_drift");
    }
    private static string PgRemoteRules(PgLedger ledger, PostgreSqlRemoteCommand? c)
    {
        var table=PgRemoteTable(ledger);var chain="inet "+table+" ingress";
        var text=$"add table inet {table}\nflush table inet {table}\nadd chain {chain} {{ type filter hook input priority -10; policy accept; }}\n";
        text += $"add rule {chain} iifname lo accept\n";
        if(c is not null) foreach(var source in c.Sources!) text += $"add rule {chain} {(source.Contains(':')?"ip6":"ip")} saddr {source} tcp dport {ledger.Port} accept\n";
        return text+$"add rule {chain} tcp dport {ledger.Port} counter drop\n";
    }
    private static async Task PgRemoteVerify(PgLedger ledger)
    {
        if (ledger.RemoteAccess is not { } policy) return;
        var plan = PostgreSqlInitializationPlanner.Create(ledger.Instance!.Value, ledger.Port);
        if(File.GetUnixFileMode(plan.ConfigurationDirectory+"/rk-server.key") != (UnixFileMode.UserRead|UnixFileMode.UserWrite)) throw new PgFailure("database.tls_invalid");
        PgRemoteCertificate(policy, new(File.ReadAllText(plan.ConfigurationDirectory+"/rk-server.pem"), File.ReadAllText(plan.ConfigurationDirectory+"/rk-server.key")));
        if (!PgRemoteOwnDropIn(ledger)) throw new PgFailure("database.firewall_drift");
        var table = await PgRun("/usr/sbin/nft", ["-s", "list", "table", "inet", PgRemoteTable(ledger)]);
        if (File.ReadAllText(PgRemoteRoot(ledger)+"/firewall.hash") != PgHash(Encoding.UTF8.GetBytes(table.Output))) throw new PgFailure("database.firewall_drift");
    }
    private static async Task PgRemoteFirewall(PgLedger ledger, PostgreSqlRemoteCommand? policy)
    {
        var root=PgRemoteRoot(ledger);PgNoLinks(root);Directory.CreateDirectory(root);
        PgAtomic(root+"/guard.nft",Encoding.UTF8.GetBytes(PgRemoteRules(ledger,policy)));
        await PgRun("/usr/sbin/nft",["-f",root+"/guard.nft"]);
        var result=await PgRun("/usr/sbin/nft",["-s","list","table","inet",PgRemoteTable(ledger)]);
        PgAtomic(root+"/firewall.hash",Encoding.UTF8.GetBytes(PgHash(Encoding.UTF8.GetBytes(result.Output))));
    }
    private static async Task<PgLedger> PgContinueRemote(PgLedger ledger, PostgreSqlTlsMaterial? tls)
    {
        var command=ledger.Pending!.Request.RemoteAccess!;var plan=PostgreSqlInitializationPlanner.Create(ledger.Instance!.Value,ledger.Port);
        var root=PgRemoteRoot(ledger);PgNoLinks(root);Directory.CreateDirectory(root);
        await PgVerifyPackage(ledger);
        // Persist drop-all before touching the listener. Guard runs before every normal systemd start.
        try { await PgRemoteFirewall(ledger,null); }
        catch { await PgRun("/usr/bin/systemctl",["stop",plan.ServiceName]); throw; }
        var dropin=PgRemoteDropIn(ledger);PgNoLinks(dropin);Directory.CreateDirectory(dropin);
        PgAtomic(dropin+"/50-rk-database.conf",Encoding.UTF8.GetBytes(PgRemoteGuard(ledger)));
        await PgRun("/usr/bin/systemctl",["daemon-reload"]);
        await PgRun("/usr/bin/systemctl",["stop",plan.ServiceName]);
        if(File.Exists(plan.DataDirectory+"/postmaster.pid")) throw new PgFailure("database.stop_unconfirmed");
        try
        {
            if(ledger.Pending.Phase=="Prepared")
            {
                if(command.Enabled)
                {
                    PgAtomic(root+"/original.conf",File.ReadAllBytes(plan.ConfigurationDirectory+"/postgresql.conf"));
                    PgAtomic(root+"/original.hba",File.ReadAllBytes(plan.ConfigurationDirectory+"/pg_hba.conf"));
                    PgAtomic(root+"/original.hash",Encoding.UTF8.GetBytes(PgFileHash(root+"/original.conf")+PgFileHash(root+"/original.hba")));
                }
                ledger=PgPhase(ledger,"RemoteIsolated");
            }
            if (File.ReadAllText(root+"/original.hash") != PgFileHash(root+"/original.conf")+PgFileHash(root+"/original.hba")) throw new PgFailure("database.configuration_drift");
            var config=Encoding.UTF8.GetString(File.ReadAllBytes(root+"/original.conf"));
            var hba=Encoding.UTF8.GetString(File.ReadAllBytes(root+"/original.hba"));
            if(command.Enabled)
            {
                PgRemoteCertificate(command,tls);
                var cert=plan.ConfigurationDirectory+"/rk-server.pem";var key=plan.ConfigurationDirectory+"/rk-server.key";
                PgAtomic(cert,Encoding.UTF8.GetBytes(tls!.CertificatePem));PgAtomic(key,Encoding.UTF8.GetBytes(tls.PrivateKeyPem));
                await PgRun("/usr/bin/chown",["postgres:postgres",cert,key]);
                config+=$"\n# RelaxKonOS remote access\nlisten_addresses='127.0.0.1,::1,{command.ListenAddress}'\nssl=on\nssl_min_protocol_version='TLSv1.2'\nssl_cert_file='{cert}'\nssl_key_file='{key}'\n";
                var remote=string.Join("",command.Sources!.Select(x=>$"hostssl {command.Database} {command.Account} {x} scram-sha-256\n"));
                hba=hba.Replace("host all all 0.0.0.0/0 reject",remote+"host all all 0.0.0.0/0 reject",StringComparison.Ordinal);
            }
            PgAtomic(plan.ConfigurationDirectory+"/postgresql.conf",Encoding.UTF8.GetBytes(config));
            PgAtomic(plan.ConfigurationDirectory+"/pg_hba.conf",Encoding.UTF8.GetBytes(hba));
            await PgRun("/usr/bin/chown",["postgres:postgres",plan.ConfigurationDirectory+"/postgresql.conf",plan.ConfigurationDirectory+"/pg_hba.conf"]);
            ledger=ledger with { RemoteAccess=command.Enabled?command:null,ConfigurationHash=PgConfigurationHash(plan) };
            ledger=PgPhase(ledger,"RemoteConfigured");
            if(command.Enabled)
            {
                await PgRun("/usr/bin/systemctl",["start",plan.ServiceName]);
                await PgVerifyConnection(ledger);
                if(await PgScalar(ledger,"postgres","SELECT current_setting('ssl')||'|'||current_setting('ssl_min_protocol_version');")!="on|TLSv1.2") throw new PgFailure("database.tls_invalid");
                await PgRemoteFirewall(ledger,command);
            }
            else
            {
                // Closing must remain possible even when the administrator can no longer authenticate.
                // Keep the verified stopped instance local-only; an explicit normal start follows separately.
                await PgVerifyConfiguration(ledger);
                foreach(var file in new[]{"rk-server.pem","rk-server.key"}) { PgNoLinks(plan.ConfigurationDirectory+"/"+file); if(File.Exists(plan.ConfigurationDirectory+"/"+file)) File.Delete(plan.ConfigurationDirectory+"/"+file); }
                LinuxDirectorySync.Flush(plan.ConfigurationDirectory);
                ledger=ledger with { ConfigurationHash=PgConfigurationHash(plan) };
            }
            return PgPhase(ledger,"Verified");
        }
        catch
        {
            try { await PgRemoteFirewall(ledger,null); }
            finally { await PgRun("/usr/bin/systemctl",["stop",plan.ServiceName]); }
            throw;
        }
    }
}
