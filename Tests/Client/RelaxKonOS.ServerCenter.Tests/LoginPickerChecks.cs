using System.Reflection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 登录窗口 SSH 分支的已保存记录检查。核心约定：可编辑的“计算机”下拉框用文本发布自己的选择，
/// 因此模式切换里恢复的上一次 SSH 用户名不得覆盖选中记录的用户名。
/// </summary>
static class LoginPickerChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync()
    {
        const string host = "localhost";
        const int port = 22;
        const string user = "codexdev";
        const string secret = "secret-pw";

        var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-login-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var targets = new HostTargetStore(directory);
            await targets.UpsertAsync(ServerHostTargetRules.Create(host, port, user, null, DateTimeOffset.UtcNow));
            var credentials = new SshCredentialStore(directory);
            // Linux 上没有 Secret Service 时凭据不会被保存；此时只覆盖宿主记录提供的用户名。
            var credentialSaved = await credentials.SaveAsync(SshCredentialRecord.From(
                ServerCenterSshEndpoint.Create(host, port, user),
                new ServerCenterSshCredential.Password(secret), DateTimeOffset.UtcNow)) == SshCredentialSaveResult.Saved;

            var viewModel = new LoginViewModel(
                DispatchProxy.Create<IAuthSession, SavedProfileSessionStub>(),
                new LoginLocalizationService(new LocalLanguageStore()),
                new ServerEndpointResolver(new HttpClient()),
                new SshDesktopSession(null!),
                targets,
                new SshHostKeyTrustStore(directory),
                credentials);

            // 可编辑下拉框把地址文本回写成选择结果：文本命中一条记录即选中它，否则清空选择，
            // 并且 Avalonia 的双向绑定立刻把这个选择发布给视图模型。
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(LoginViewModel.ServerUrl)) return;
                var match = viewModel.SavedSshHosts
                    .FirstOrDefault(profile => profile.MatchesAddress(viewModel.ServerUrl));
                if (!ReferenceEquals(viewModel.SelectedSshHost, match)) viewModel.SelectedSshHost = match;
            };

            await viewModel.LoadSavedProfilesAsync();
            Check(viewModel.SavedSshHosts.Count == 1 && viewModel.SavedSshHosts[0].UserName == user,
                "登录窗口把宿主记录与安全凭据合并为 用户名@主机:端口 记录");

            // 登录窗口以 RelaxKonOS Server 启动，SSH 记录只有在用户切换登录方式后才被使用。
            viewModel.UseSshLogin = true;
            Check(viewModel.Identifier == user, "切换到 SSH 后用户名来自选中记录而不是上一次 SSH 会话的缓存");
            Check(viewModel.ServerUrl == $"{host}:{port}", "地址框只显示 主机:端口");
            if (credentialSaved)
            {
                await WaitForAsync(() => viewModel.Password.Length > 0);
                Check(viewModel.Password == secret, "选中记录会填入安全保存的 SSH 密码");
            }

            viewModel.UseSshLogin = false;
            viewModel.UseSshLogin = true;
            Check(viewModel.Identifier == user, "反复切换登录方式后用户名仍然来自选中记录");

            // 没有记录认领该地址时，用户自己输入的地址与用户名必须跨模式切换保留。
            viewModel.ServerUrl = "example.internal:2222";
            viewModel.Identifier = "operator";
            viewModel.UseSshLogin = false;
            viewModel.UseSshLogin = true;
            Check(viewModel.ServerUrl == "example.internal:2222" && viewModel.Identifier == "operator",
                "没有匹配记录时保留用户输入的地址与用户名");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20);
        Check(condition(), "火后即忘的选中记录填充已完成");
    }
}

/// <summary>登录窗口只向会话查询已记住的连接，其余成员在本检查中不可达。</summary>
class SavedProfileSessionStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetSavedProfilesAsync" => Task.FromResult<IReadOnlyList<SavedLoginProfile>>([]),
        _ => throw new NotSupportedException(method?.Name)
    };
}
