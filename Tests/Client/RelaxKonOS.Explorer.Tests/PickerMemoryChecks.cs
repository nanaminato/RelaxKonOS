using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Services;

internal static class PickerMemoryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var client = DispatchProxy.Create<IExplorerClient, ExplorerFake>();
        var fake = (ExplorerFake)(object)client;
        var memory = new PickerMemory();
        memory.Values["open:OpenFile"] = "/remembered";
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(), _ => { }, usageMemory: memory, memoryPurpose: "open"))
        {
            await picker.LoadRootAsync();
            check(picker.AddressbarPath == "/remembered", "Remote picker restores its remembered directory.");
            await picker.NavigateToAsync("/another");
            picker.CancelPickerCommand.Execute(null);
            check(memory.Values["open:OpenFile"] == "/remembered", "Browsing and cancelling do not overwrite the remembered directory.");
        }
        fake.ProtectedDirectory = "/remembered";
        var prompts = 0;
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(), _ => { }, usageMemory: memory, memoryPurpose: "open"))
        {
            picker.RequestFileElevationAsync = (_, _) => { prompts++; return Task.FromResult(false); };
            await picker.LoadRootAsync();
            check(picker.AddressbarPath != "/remembered" && prompts == 0, "A protected remembered directory falls back without prompting for elevation.");
        }
        fake.ProtectedDirectory = null;
        fake.Denied = "/remembered";
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(), _ => { }, usageMemory: memory, memoryPurpose: "open"))
        {
            await picker.LoadRootAsync();
            check(picker.AddressbarPath != "/remembered", "An unavailable remote directory falls back to an accessible location.");
        }
        fake.Denied = null;
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(), _ => { }, usageMemory: memory, memoryPurpose: "open", initialDirectory: "/explicit"))
        {
            await picker.LoadRootAsync();
            check(picker.AddressbarPath == "/explicit", "An explicit remote start directory overrides memory.");
        }
        string? selected = null;
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(ExplorerPickerMode.SaveFile), paths => selected = paths[0], usageMemory: memory, memoryPurpose: "save"))
        {
            await picker.LoadRootAsync();
            await picker.NavigateToAsync("/destination");
            picker.PickerEntryName = "new.txt";
            await picker.ConfirmPickerCommand.ExecuteAsync(null);
            check(selected == "/destination/new.txt" && memory.Values["save:SaveFile"] == "/destination", "Confirmed remote save remembers its parent directory.");
        }
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder), paths => selected = paths[0], usageMemory: memory, memoryPurpose: "folder"))
        {
            await picker.LoadRootAsync();
            await picker.NavigateToAsync("/chosen-folder");
            await picker.ConfirmPickerCommand.ExecuteAsync(null);
            check(memory.Values["folder:SelectFolder"] == selected, "Confirmed remote folder selection remembers the selected folder.");
        }
        using (var picker = new ExplorerViewModel(client, new ExplorerPickerOptions(ExplorerPickerMode.SaveFile), _ => throw new Exception("Expired selection returned"), usageMemory: memory, memoryPurpose: "expired"))
        {
            await picker.NavigateToAsync("/destination");
            picker.PickerEntryName = "new.txt";
            memory.IsCurrent = false;
            await picker.ConfirmPickerCommand.ExecuteAsync(null);
            check(!memory.Values.ContainsKey("expired:SaveFile"), "An expired remote selection cannot update memory or return a path.");
        }
    }
    private sealed class PickerMemory : IUsageMemoryScope
    {
        public bool IsCurrent { get; set; } = true;
        public Dictionary<string, string> Values = new();
        public string? Directory(string purpose, bool remote) => Values.GetValueOrDefault(purpose);
        public void RememberDirectory(string purpose, bool remote, string? directory)
        {
            if (IsCurrent && directory is not null) Values[purpose] = directory;
        }
    }
}
