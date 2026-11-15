using RelaxKonOS.Core.VirtualSystemDrive;

var valid = new[] { "abc", "com.example.app", "relaxkonos.terminal", "a-b.c", new string('a', 128) };
foreach (var id in valid)
    if (!ApplicationDescriptorValidator.IsValidAppId(id)) throw new Exception($"Valid app ID rejected: {id}");
string?[] invalid = [null, "", "ab", new string('a', 129), "Com.example", "com.example\n", "com.example\r\n", "com.example\t", "com.example\0", " com.example", "com.example ", "com/example", "com.example\nother"];
foreach (var id in invalid)
    if (ApplicationDescriptorValidator.IsValidAppId(id)) throw new Exception($"Invalid app ID accepted: {System.Text.Json.JsonSerializer.Serialize(id)}");
var descriptor = new ApplicationDescriptor(1, "com.example\n", ApplicationDescriptorKind.Package, "Example", "1.0", new(EntryAssembly: "lib/App.dll", EntryType: "App"));
if (ApplicationDescriptorValidator.Validate(descriptor).ProblemCode != VirtualSystemDriveProblemCode.AppIdInvalid)
    throw new Exception("Descriptor must reject a trailing newline with AppIdInvalid.");
Console.WriteLine($"PASS {valid.Length + invalid.Length + 1} app ID validation cases.");
