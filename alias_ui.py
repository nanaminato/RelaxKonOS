exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
edit('Client/RelaxKonOS.Client/Services/Bootstrapper.cs','        services.AddSingleton<IAuthSession, AuthSession>();','''        services.AddSingleton<IAuthSession, AuthSession>();
        services.AddHttpClient<AccountSecurityClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });''')
edit('Client/RelaxKonOS.Client/Apps/Settings/ViewModels/SettingsViewModel.cs','            new SystemPageViewModel(settings, session, save),','''            new SystemPageViewModel(settings, session, save),
            new AccountSecurityPageViewModel(settings, App.Services.GetRequiredService<AccountSecurityClient>(), session,
                App.Services.GetRequiredService<IRememberedSessionStore>()),''')
edit('Client/RelaxKonOS.Client/Apps/Settings/ViewModels/SettingsViewModel.cs','        _ = RefreshCatalogAsync();','        _ = RefreshCatalogAsync();\n        _ = Pages.OfType<AccountSecurityPageViewModel>().Single().LoadAsync();')
edit('Client/RelaxKonOS.Client/Apps/Settings/ViewModels/SettingsViewModel.Navigation.cs','            ("host.environment",','            ("account.alias", "account-security", "settings.account.title", SettingsScope.HostUser, "account security alias login 账号 安全 登录别名 アカウント セキュリティ ログイン エイリアス"),\n            ("host.environment",')
edit('Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml','    <UserControl.DataTemplates>','''    <UserControl.DataTemplates>
        <DataTemplate DataType="vm:AccountSecurityPageViewModel">
            <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" Padding="0,0,0,32">
                <pages:AccountSecurityPageView VerticalAlignment="Top" />
            </ScrollViewer>
        </DataTemplate>''')
edit('Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs','"system", "environment",','"system", "account-security", "environment",')
edit('Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs','        _window = window;','''        _window = window;
        viewModel.Pages.OfType<AccountSecurityPageViewModel>().Single().RequestOperationAsync = async (operation, configuration, cancellationToken) =>
        {
            AliasOperationDialogViewModel? editor = null;
            CancellationTokenRegistration registration = default;
            try
            {
                return await context.ShowDialogAsync<object?>(window, LocalizedText.Get("settings.account.title"), dialog =>
                {
                    editor = new AliasOperationDialogViewModel(operation, configuration, dialog.Close);
                    registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => { editor.Clear(); dialog.Close(null); }));
                    return new AliasOperationDialogView { DataContext = editor };
                }, new Size(540, 620));
            }
            finally { registration.Dispose(); editor?.Clear(); }
        };''')
# Existing Settings disposal already enumerates IDisposable pages.
import json
translations={
'title':['Account & Security','账号与安全','アカウントとセキュリティ'],
'description':['The login alias belongs to your existing system account and workspace. This setting does not affect SSH, file sharing, or the OS account.','登录别名使用现有系统账号与工作区。此设置不影响 SSH、文件共享或系统账号状态。','ログインエイリアスは既存のシステムアカウントとワークスペースに紐づきます。SSH、ファイル共有、OS アカウントには影響しません。'],
'system':['System account','系统账号','システムアカウント'], 'method':['Current login method','当前登录方式','現在のログイン方法'],
'alias':['Login alias','登录别名','ログインエイリアス'], 'system_login':['Allow direct system login','允许系统账号直接登录','システムアカウントで直接ログイン'],
'on':['On','开启','有効'], 'off':['Off','关闭','無効'], 'not_set':['Not configured','未设置','未設定'],
'create':['Create alias','创建别名','作成'], 'rename':['Rename alias','更改别名','名前を変更'], 'password':['Change password','更改密码','パスワードを変更'],
'delete':['Delete alias','删除别名','削除'], 'toggle':['Change system login policy','更改系统登录策略','直接ログイン設定を変更'], 'reload':['Reload','重新读取','再読み込み'],
'new_password':['New alias password (15–128 characters)','新别名密码（15–128 个字符）','新しいエイリアスパスワード（15～128 文字）'],
'confirm_password':['Confirm new password','确认新密码','新しいパスワードを確認'], 'system_password':['Current OS password','当前系统密码','現在の OS パスワード'],
'alias_password':['Current alias password','当前别名密码','現在のエイリアスパスワード'], 'current_password':['Current password for the selected method','所选方式的当前密码','選択した方法の現在のパスワード'],
'use_system_password':['Verify with my OS password','使用本人系统密码复验','自分の OS パスワードで確認'],
'alias_hint':['3–32 lowercase ASCII characters, starting with a letter','3–32 个小写 ASCII 字符，以字母开头','小文字 ASCII 3～32 文字、先頭は英字'],
'password_mismatch':['Enter your current password and matching new passwords.','请填写当前密码，并确保两次新密码一致。','現在のパスワードを入力し、新しいパスワードを一致させてください。'],
'confirm.create':['Verify your OS password to create the alias. System login will remain enabled.','复验本人系统密码后创建别名。创建后系统登录仍开启。','OS パスワードを確認して作成します。直接ログインは有効のままです。'],
'confirm.rename':['Verify your current password to rename the alias. Existing sessions remain connected.','复验当前密码以更改别名。现有会话保持连接。','現在のパスワードを確認して名前を変更します。既存の接続は維持されます。'],
'confirm.password':['Changing the alias password signs out every session, including this one. Sign in again afterward.','更改别名密码将退出所有会话（包括当前会话），完成后需重新登录。','パスワード変更後、すべてのセッションからログアウトします。再ログインが必要です。'],
'confirm.delete':['Verify your OS password. Deleting the alias restores direct system login and signs out every session.','验证本人系统密码。删除别名将恢复系统直接登录，并退出所有会话。','OS パスワードを確認します。削除すると直接ログインが有効になり、全セッションからログアウトします。'],
'confirm.toggle':['Disabling requires the current alias password; enabling requires the current OS password. SSH and OS access are unaffected.','关闭时验证当前别名密码；开启时验证当前系统密码。不会更改 SSH 或 OS 访问。','無効化には現在のエイリアスパスワード、有効化には OS パスワードが必要です。SSH と OS アクセスには影響しません。'],
'saved':['Account security updated.','账号安全设置已更新。','アカウント設定を更新しました。'],
'failed':['The operation was not confirmed. Review the refreshed configuration before trying again.','操作结果未确认。请核对重新读取的配置后再操作。','操作結果を確認できません。再読み込みした設定を確認してから再実行してください。'],
'load_failed':['Could not read account security. Reconnect or reload.','无法读取账号安全配置，请重新连接或读取。','設定を読み込めません。再接続または再読み込みしてください。'],
'unavailable':['Account eligibility is unavailable. Contact the server operator.','无法确认账号资格，请联系服务器运维人员。','アカウントの利用資格を確認できません。管理者に連絡してください。'],
'unavailable.linux-pam-account-check-unverified':['Alias support awaits verification of this Linux PAM stack. Use system login.','此 Linux PAM 配置尚未完成资格检查验证，请使用系统登录。','この Linux PAM 構成は未検証です。システムログインを使用してください。'],
'unavailable.windows-domain-account-check-unverified':['Domain alias support is not verified. Use system login.','域账号别名资格检查尚未验证，请使用系统登录。','ドメインのエイリアスは未検証です。システムログインを使用してください。'],
'unavailable.persistent-storage-required':['Alias management requires SQLite storage.','别名管理需要 SQLite 持久化存储。','エイリアス管理には SQLite ストレージが必要です。'],
'error.reauthentication-failed':['Current password verification failed.','当前密码复验失败。','現在のパスワードを確認できません。'],
'error.alias-revision-conflict':['Settings changed elsewhere. Review the refreshed state.','配置已在其他位置更改，请核对最新状态。','別の場所で設定が変更されました。最新の状態を確認してください。'],
'error.alias-unavailable':['This alias or account cannot be used.','该别名或账号暂不可用。','このエイリアスまたはアカウントは利用できません。'],
'error.invalid-input':['Check the alias format and password length (15–128 characters).','请检查别名格式和密码长度（15–128 个字符）。','エイリアス形式とパスワードの長さ（15～128 文字）を確認してください。'],
'error.login-rate-limited':['Too many attempts. Wait before trying again.','尝试过于频繁，请稍后再试。','試行回数が多すぎます。しばらくお待ちください。'],
'saved_cleanup_failed':['Update the saved login for this server manually.','请手动更新此服务器的已保存登录条目。','このサーバーの保存済みログインを手動で更新してください。']}
for i,lang in enumerate(['en-US','zh-CN','ja-JP']):
 p=Path('Client/RelaxKonOS.Client/Localization')/lang/'settings.json'; d=json.loads(p.read_text()); d.update({'settings.account.'+k:v[i] for k,v in translations.items()}); p.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 p=p.with_name('login.json'); d=json.loads(p.read_text()); d['login.username']=['System username or login alias:','系统用户名或登录别名：','システムユーザー名またはエイリアス：'][i]; d['login.username_placeholder']=['System username, or lowercase alias','系统用户名，或小写登录别名','システムユーザー名、または小文字のエイリアス'][i]; p.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
