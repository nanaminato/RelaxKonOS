using System.Net.Http.Headers;
using System.Net.Http.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Client.Apps.Databases;

public sealed partial class MySqlViewModel(HttpClient http, IAuthSession session) : ObservableObject, IDisposable
{
    private readonly object? login = session.CurrentSession;
    private readonly CancellationTokenSource lifetime = new();
    private bool invalid;
    private bool reading;
    private MySqlRequest? submission;
    public InstallationTaskViewModel Installation { get; set; } = null!;
    public bool CanManage { get; init; }
    public Func<Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] private MySqlState? state;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private LocalizedStatus notice = LocalizedStatus.Literal("");

    public LocalizedStatus Status => LocalizedStatus.Key(State switch
    {
        null => "database.state_unavailable",
        { ProblemCode: { } problem } => problem,
        { Installed: false } => "database.not_installed",
        { InstanceId: null } => "database.not_initialized",
        { Running: true, Verified: true } => "database.connection_verified",
        { Running: true } => "database.connection_unverified",
        _ => "database.stopped"
    });
    private bool Writable => !invalid && !Busy && CanManage && !Installation.IsActive && submission is null
        && State is { Supported: true, PendingOperationId: null, ProblemCode: null };
    public bool CanInstall => Writable && State is { Installed: false };
    public bool CanInitialize => Writable && State is { Installed: true, InstanceId: null };
    public bool CanStart => Writable && State is { InstanceId: not null, Running: false };
    public bool CanStop => Writable && State is { Running: true };
    public bool CanRecover => !invalid && !Busy && CanManage && !Installation.IsActive && submission is null && State?.PendingOperationId is not null;
    public bool CanResume => !invalid && !Busy && CanManage && !Installation.IsActive && submission is not null;

    partial void OnStateChanged(MySqlState? value) => Notify();
    partial void OnBusyChanged(bool value) => Notify();
    public void Notify()
    {
        foreach (var property in new[] { nameof(Status), nameof(CanInstall), nameof(CanInitialize), nameof(CanStart),
            nameof(CanStop), nameof(CanRecover), nameof(CanResume) }) OnPropertyChanged(property);
    }

    [RelayCommand] public async Task RefreshAsync()
    {
        if (invalid || reading) return;
        reading = true;
        try
        {
            var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: lifetime.Token);
            if (invalid || !ReferenceEquals(login, session.CurrentSession)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(session.EffectiveBaseUrl!), MySqlRoutes.State.TrimStart('/')));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, lifetime.Token);
            response.EnsureSuccessStatusCode();
            var current = await response.Content.ReadFromJsonAsync<MySqlState>(RelaxKonOSJsonOptions.Default, lifetime.Token);
            if (!invalid && ReferenceEquals(login, session.CurrentSession))
            {
                State = current;
                Notice = LocalizedStatus.Literal("");
            }
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            State = null;
            Notice = LocalizedStatus.Key("database.state_unavailable");
        }
        catch (OperationCanceledException) { }
        finally { reading = false; }
    }

    [RelayCommand] private Task InstallAsync() => SubmitAsync(MySqlAction.Install, CanInstall);
    [RelayCommand] private Task InitializeAsync() => SubmitAsync(MySqlAction.Initialize, CanInitialize);
    [RelayCommand] private Task StartAsync() => SubmitAsync(MySqlAction.Start, CanStart);
    [RelayCommand] private Task StopAsync() => SubmitAsync(MySqlAction.Stop, CanStop);
    [RelayCommand] private Task VerifyAsync() => SubmitAsync(MySqlAction.Verify, CanStop);
    [RelayCommand] private Task RecoverAsync() => SubmitAsync(null, CanRecover);
    [RelayCommand] private async Task ResumeAsync()
    {
        if (!CanResume) return;
        Busy = true;
        try { await SendAsync(); }
        finally { Busy = false; }
    }
    private async Task SubmitAsync(MySqlAction? action, bool allowed)
    {
        if (!allowed || State is not { } snapshot) return;
        Busy = true;
        try
        {
            if (ConfirmAsync is null || !await ConfirmAsync() || invalid || !ReferenceEquals(login, session.CurrentSession)) return;
            submission = action is null ? new(true, RecoverOperationId: snapshot.PendingOperationId)
                : new(true, action, snapshot.Revision);
            await SendAsync();
        }
        finally { Busy = false; }
    }

    private async Task SendAsync()
    {
        var kind = submission!.RecoverOperationId is not null ? InstallationOperationKind.Repair
            : submission.Action == MySqlAction.Install ? InstallationOperationKind.Install : InstallationOperationKind.Configure;
        var previous = Installation.Operation?.OperationId;
        await Installation.SubmitAsync(kind, submission);
        if (!Installation.HasPendingSubmission || Installation.Operation?.OperationId is { } accepted && accepted != previous)
            submission = null;
        Notify();
    }

    public void Invalidate()
    {
        invalid = true;
        lifetime.Cancel();
        State = null;
        submission = null;
    }

    public void Dispose()
    {
        Invalidate();
        Installation.Dispose();
        lifetime.Dispose();
    }
}
