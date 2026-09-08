using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

public enum PreferencesSaveState { Idle, Saving, Accepted, Failed, Conflict, Offline }

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
            JsonSerializer.Serialize(preferences, RemoteOsJsonOptions.Default), RemoteOsJsonOptions.Default)!;
        _draft = new(url, tokens.AccessToken, workspace.Id, frozen);
        OnPropertyChanged(nameof(HasDraft));
        _registry.SetMappings(frozen.DefaultApps);
        BeginSave(_draft, debounce: true);
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
            await _service.SaveAsync(draft.Url, draft.Token, draft.WorkspaceId, draft.Value, pending.Token);
            if (!ReferenceEquals(_draft, draft) || !IsCurrent(draft)) return;
            _draft = null;
            OnPropertyChanged(nameof(HasDraft));
            State = PreferencesSaveState.Accepted;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (RemoteOsAuthException ex)
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
        && _session.Tokens?.AccessToken == draft.Token;

    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
    {
        if (_draft is null || IsCurrent(_draft)) return;
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

    private sealed record Draft(string Url, string Token, Guid WorkspaceId, WorkspacePreferencesDto Value);
}
