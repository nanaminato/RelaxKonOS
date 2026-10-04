using Microsoft.AspNetCore.Http;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Files;

internal static class FileReadElevationChecks
{
    internal static void Run(string root)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "reader"), new Claim("jti", "read-session"), new Claim("name", "testuser")], "test"));
        var host = new HostElevationSessionStore(new TestHostAccountPrivilegeService(),
            new UploadSessionChecks.SystemMode(), new HostElevationSessionState());
        var grants = new FileElevationSessionStore(host);
        var files = DispatchProxy.Create<IFileService, ProtectedMetadata>();
        var metadata = (ProtectedMetadata)(object)files;
        metadata.CanRead = path => grants.IsElevated(principal, FileElevationCapability.Read, path);
        var directory = Path.Combine(root, "protected");
        var image = Path.Combine(directory, "nested", "image.png");
        var http = new DefaultHttpContext { User = principal };
        var grant = typeof(FileEndpoints).GetMethod("GrantElevation", BindingFlags.NonPublic | BindingFlags.Static)!;
        metadata.EntryType = FileSystemEntryType.Directory;
        grant.Invoke(null, [new FileElevationRequest(directory, "password", Capability: FileElevationCapability.Read),
            http, new Administrator(), grants, files]);
        TestAssert.Assert(grants.IsElevated(principal, FileElevationCapability.Read, image),
            "Reading a protected folder must authorize opening nested images without another password.");
        TestAssert.Assert(!grants.IsElevated(principal, FileElevationCapability.Write, image)
            && !grants.IsElevated(principal, FileElevationCapability.Read, directory + "-other/image.png"),
            "Folder read elevation must not authorize writes or adjacent folders.");
        var exact = Path.Combine(root, "single.png");
        metadata.EntryType = FileSystemEntryType.File;
        grant.Invoke(null, [new FileElevationRequest(exact, "password", Capability: FileElevationCapability.Read),
            http, new Administrator(), grants, files]);
        TestAssert.Assert(grants.IsElevated(principal, FileElevationCapability.Read, exact)
            && !grants.IsElevated(principal, FileElevationCapability.Read, Path.Combine(exact, "child")),
            "A file read grant must remain exact-path.");
        host.Revoke(principal);
        TestAssert.Assert(!grants.IsElevated(principal, FileElevationCapability.Read, image),
            "Revoking the session must revoke subtree read access.");
        var denied = grant.Invoke(null, [new FileElevationRequest(directory, "wrong", Capability: FileElevationCapability.Read),
            http, new Administrator(), grants, files]);
        TestAssert.Assert(denied is IStatusCodeHttpResult { StatusCode: 403 }
            && !grants.IsElevated(principal, FileElevationCapability.Read, directory, image),
            "Failed administrator authentication must not grant folder or file access.");
    }

    public class ProtectedMetadata : DispatchProxy
    {
        public Func<string, bool> CanRead { get; set; } = _ => false;
        public FileSystemEntryType EntryType { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IFileService.GetInfo)) throw new NotSupportedException();
            var path = (string)args![0]!;
            if (!CanRead(path)) throw new UnauthorizedAccessException("Protected metadata requires a grant.");
            return new FileSystemEntryDto(path, Path.GetFileName(path), null, EntryType, null, null, null, false, false, null);
        }
    }

    private sealed class Administrator : IHostAdministratorAuthenticator
    {
        public HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password)
            => new(password == "password", "elevation-password-invalid", "system");
    }
}
