namespace RelaxKonOS.Core.VirtualSystemDrive;

/// <summary>Stable, non-sensitive diagnostics emitted while validating Virtual System Drive data.</summary>
public static class VirtualSystemDriveProblemCode
{
    public const string PathInvalid = "vsd.path.invalid";
    public const string PathEscape = "vsd.path.escape";
    public const string SchemaUnsupported = "vsd.schema.unsupported";
    public const string JsonInvalid = "vsd.json.invalid";
    public const string DocumentTooLarge = "vsd.document.too-large";
    public const string AppIdInvalid = "vsd.app-id.invalid";
    public const string DuplicateAppId = "vsd.app-id.duplicate";
    public const string BuiltInMismatch = "vsd.builtin.mismatch";
    public const string PackageLayoutInvalid = "vsd.package.layout.invalid";
    public const string ShortcutInvalid = "vsd.shortcut.invalid";
    public const string ScriptInvalid = "vsd.script.invalid";
}
