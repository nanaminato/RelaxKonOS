namespace RelaxKonOS.Server.Proxy;

public sealed class MihomoDirectPublicationExecutor : IMihomoDirectPublicationExecutor
{
    public async Task<string?> PublishProfileAsync(DirectProfileExecution operation, DirectPublicationProblems problems)
    {
        var reload = await operation.ReloadAsync();
        if (reload == problems.RecoveryRequired)
        {
            operation.RestoreUnknown();
            return reload;
        }
        if (string.IsNullOrEmpty(reload) && await operation.IsHealthyAsync())
            return await operation.ConfirmAsync() ? null : problems.RecoveryRequired;
        if (operation.HasPrevious())
        {
            operation.RestoreForCompensation();
            var rollback = await operation.ReloadAsync();
            var restored = string.IsNullOrEmpty(rollback) && await operation.IsHealthyAsync();
            if (restored) await operation.CompleteAsync();
            var outcome = restored ? problems.ApplyFailed : problems.RecoveryRequired;
            operation.ReportCompensation(outcome);
            return outcome;
        }
        // Without a previous file only a known rejection can close the checkpoint.
        if (string.IsNullOrEmpty(reload)) return problems.RecoveryRequired;
        operation.RemoveCandidate();
        await operation.CompleteAsync();
        return problems.ApplyFailed;
    }

    public async Task<string?> PublishSettingsAsync(DirectSettingsExecution operation, DirectPublicationProblems problems)
    {
        var available = await operation.IsControllerAvailableAsync();
        var reload = available ? await operation.ReloadAsync() : null;
        if (available && !string.IsNullOrEmpty(reload))
        {
            await operation.RestoreAsync();
            if (reload == problems.RecoveryRequired) return reload;
            if (!string.IsNullOrEmpty(await operation.ReloadAsync())) return problems.RecoveryRequired;
            await operation.CompleteAsync();
            return problems.ApplyFailed;
        }
        if (operation.TrackedHostPolicy)
        {
            if (available) return await operation.ConfirmTrackedAsync() ? null : problems.RecoveryRequired;
            await operation.RestoreAsync();
            await operation.CompleteAsync();
            return problems.ControllerUnavailable;
        }
        var host = await operation.ApplyHostPolicyAsync(false);
        if (available && host is null && operation.CanConfirmHostPolicy)
            return await operation.ConfirmAsync() ? null : problems.RecoveryRequired;
        if (operation.ProxyDisabled && host == problems.PrivilegedOperationUnavailable) host = null;
        if (host is not null)
        {
            operation.ReportHostPolicyFailure(host);
            await operation.RestoreAsync();
            if (available && !string.IsNullOrEmpty(await operation.ReloadAsync())) return problems.RecoveryRequired;
            if (host != problems.SystemProxyConflict) return problems.RecoveryRequired;
            await operation.CompleteAsync();
            return host;
        }
        if (!await operation.PersistAsync())
        {
            var rollback = await operation.ApplyHostPolicyAsync(true);
            await operation.RestoreAsync();
            if (available && !string.IsNullOrEmpty(await operation.ReloadAsync())) return problems.RecoveryRequired;
            if (rollback is null) await operation.CompleteAsync();
            return rollback is null ? problems.ApplyFailed : problems.RecoveryRequired;
        }
        await operation.CompleteAsync();
        return null;
    }
}
