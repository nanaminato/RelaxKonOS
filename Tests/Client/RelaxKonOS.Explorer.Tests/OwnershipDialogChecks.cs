using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Protocol.Files;

public static class OwnershipDialogChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var before = new FilePropertiesDto("/project", "project", FileSystemEntryType.Directory,
            null, null, null, null, "0755", "Directory", 0x1ed, 0, 0);
        var sends = 0;
        var cancel = false;
        var fail = false;
        var vm = new FilePropertiesDialogViewModel(before, null, () => { }, (owner, group, recursive) =>
        {
            sends++;
            check(owner == "nanami" && group == "1000" && recursive, "Ownership dialog sends trimmed owner, group and selected recursion");
            if (cancel) throw new OperationCanceledException();
            if (fail) throw new IOException("denied");
            return Task.FromResult(before with { UnixOwnerId = 1000, UnixGroupId = 1000 });
        });
        check(vm.OwnershipOwner == "0" && vm.OwnershipGroup == "0" && vm.CanEditOwnership,
            "Ownership dialog displays the actual current UID and GID");
        vm.OwnershipOwner = " ";
        await vm.SaveOwnershipCommand.ExecuteAsync(null);
        check(sends == 0 && vm.OwnershipStatus == "explorer.ownership.required", "Missing ownership input does not send a mutation");
        vm.OwnershipOwner = " nanami "; vm.OwnershipGroup = "1000"; vm.RecursiveOwnership = true;
        await vm.SaveOwnershipCommand.ExecuteAsync(null);
        check(vm.Properties.UnixOwnerId == 1000 && vm.OwnershipOwner == "1000" && !vm.IsSavingOwnership,
            "Successful ownership save refreshes metadata from the server");
        vm.Properties = before; vm.OwnershipOwner = "nanami"; vm.OwnershipGroup = "1000"; cancel = true;
        await vm.SaveOwnershipCommand.ExecuteAsync(null);
        check(vm.Properties == before && vm.OwnershipStatus != "explorer.ownership.saved" && !vm.IsSavingOwnership,
            "Canceled authorization preserves metadata and never reports success");
        cancel = false; fail = true;
        await vm.SaveOwnershipCommand.ExecuteAsync(null);
        check(vm.Properties == before && vm.OwnershipStatus.Contains("save_failed") && !vm.IsSavingOwnership,
            "Failed ownership save preserves displayed metadata and releases the busy state");
        var readOnly = new FilePropertiesDialogViewModel(before, null, () => { }, null);
        check(readOnly.HasUnixOwnership && !readOnly.CanEditOwnership, "User Mode can show ownership without exposing a writable control");
    }
}
