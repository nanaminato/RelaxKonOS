using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

public enum PreferencesSaveState { Idle, Saving, Accepted, Saved, Failed, Conflict, Offline }

/// <summary>Owns preference drafts and debounced writes beyond any Settings window lifetime.</summary>
public sealed class WorkspacePreferencesEditor : ObservableObject, IDisposable
{
    private readonly IWorkspaceSettingsService _service;
    private readonly IAuthSession _session;
    private readonly DefaultAppRegistry _registry;
    private CancellationTokenSource? _pending;
    private Draft? _draft;
    private PreferencesSaveState _state;
    public PreferencesSaveState State { get => _state; private set => SetProperty(ref _state, value); }
    public bool HasDraft => _draft is not null;

    public WorkspacePreferencesEditor(IWorkspaceSettingsService service, IAuthSession session, DefaultAppRegistry registry)
    {
        _service = service;
        _session = session;
        _registry = registry;
        session.StateChanged += OnSessionChanged;
    }

    public void Schedule(WorkspacePreferencesDto preferences)
    {
        _pending?.Cancel();
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
        {
            State = PreferencesSaveState.Offline;
            return;
        }
        // Shell owns mutable lists: freeze the edit and its target before the debounce delay.
        var frozen = JsonSerializer.Deserialize<WorkspacePreferencesDto>(
            JsonSerializer.Serialize(preferences, RelaxKonOSJsonOptions.Default), RelaxKonOSJsonOptions.Default)!;
        _draft = new(url, _session.CurrentSession?.Id, workspace.Id, frozen);
        OnPropertyChanged(nameof(HasDraft));
        _registry.SetMappings(frozen.DefaultApps);
        BeginSave(_draft, debounce: true);
    }

    public void ObserveExternalRevision(long? revision)
    {
        if (_draft is { } draft && IsCurrent(draft) && State != PreferencesSaveState.Saving
            && revision != draft.Value.Revision)
            State = PreferencesSaveState.Conflict;
    }

    /// <summary>Explicit user choice. Fetch first so a failed reload retains the original draft.</summary>
    public async Task<WorkspacePreferencesDto?> DiscardAndReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_draft is not { } draft || !IsCurrent(draft) || State == PreferencesSaveState.Saving) return null;
        try
        {
            var snapshot = await _service.GetAsync(draft.Url, _session.Tokens!.AccessToken, draft.WorkspaceId, cancellationToken);
            if (!ReferenceEquals(_draft, draft) || !IsCurrent(draft)) return null;
            _pending?.Cancel();
            _draft = null;
            State = PreferencesSaveState.Idle;
            OnPropertyChanged(nameof(HasDraft));
            _registry.SetMappings(snapshot.DefaultApps);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch
        {
            if (ReferenceEquals(_draft, draft) && IsCurrent(draft)) State = PreferencesSaveState.Failed;
            return null;
        }
    }

    public void Retry()
    {
        // A conflict requires an explicit reload/merge. Never attach a new revision to an old draft.
        if (_draft is { } draft && State != PreferencesSaveState.Conflict && IsCurrent(draft)) BeginSave(draft, false);
    }

    private void BeginSave(Draft draft, bool debounce)
    {
        _pending?.Cancel();
        var pending = new CancellationTokenSource();
        _pending = pending;
        State = PreferencesSaveState.Saving;
        _ = SaveAsync(draft, pending, debounce);
    }

    private async Task SaveAsync(Draft draft, CancellationTokenSource pending, bool debounce)
    {
        try
        {
            if (debounce) await Task.Delay(300, pending.Token);
            if (!IsCurrent(draft)) return;
            var saved = await _service.SaveAsync(draft.Url, _session.Tokens!.AccessToken, draft.WorkspaceId, draft.Value, pending.Token);
            if (!ReferenceEquals(_draft, draft) || !IsCurrent(draft)) return;
            _draft = null;
            OnPropertyChanged(nameof(HasDraft));
            State = saved.PersistedRevision == saved.Revision ? PreferencesSaveState.Saved : PreferencesSaveState.Accepted;
            // Observe persistence without replaying the write. Stop when a new edit or session replaces this one.
            for (var attempt = 0; attempt < 12 && State == PreferencesSaveState.Accepted; attempt++)
            {
                await Task.Delay(1000, pending.Token);
                if (!IsCurrent(draft) || !ReferenceEquals(_pending, pending)) return;
                var observed = await _service.GetAsync(draft.Url, _session.Tokens!.AccessToken, draft.WorkspaceId, pending.Token);
                if (!IsCurrent(draft) || !ReferenceEquals(_pending, pending)) return;
                if (observed.Revision != saved.Revision) return;
                if (observed.PersistedRevision == saved.Revision) State = PreferencesSaveState.Saved;
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (RelaxKonOSAuthException ex)
        {
            if (ReferenceEquals(_draft, draft) && IsCurrent(draft))
                State = ex.Status == 409 ? PreferencesSaveState.Conflict : PreferencesSaveState.Failed;
        }
        catch
        {
            if (ReferenceEquals(_draft, draft) && IsCurrent(draft)) State = PreferencesSaveState.Failed;
        }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
            pending.Dispose();
        }
    }

    private bool IsCurrent(Draft draft) => _session.State == AuthSessionState.Authenticated
        && _session.ServerUrl == draft.Url && _session.CurrentWorkspace?.Id == draft.WorkspaceId
        && _session.CurrentSession?.Id == draft.SessionId;

    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
    {
        if (_draft is null)
        {
            if (_session.State != AuthSessionState.Authenticated)
            {
                _pending?.Cancel();
                State = PreferencesSaveState.Idle;
            }
            return;
        }
        if (IsCurrent(_draft)) return;
        _pending?.Cancel();
        _draft = null;
        OnPropertyChanged(nameof(HasDraft));
        State = PreferencesSaveState.Idle;
    }

    public void Dispose()
    {
        _session.StateChanged -= OnSessionChanged;
        _pending?.Cancel();
    }

    private sealed record Draft(string Url, Guid? SessionId, Guid WorkspaceId, WorkspacePreferencesDto Value);
}
