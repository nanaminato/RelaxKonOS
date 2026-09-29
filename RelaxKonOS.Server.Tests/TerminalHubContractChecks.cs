using System.Reflection;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Hubs;

/// <summary>
/// Terminal Hub 的「方法名 ↔ 常量 ↔ 参数形状」静态契约校验。
/// </summary>
/// <remarks>
/// SignalR 按方法名与参数个数匹配 Hub 方法，两端任何一处漂移都只在运行时暴露，而客户端只会看到笼统的
/// invoke 失败（「新建会话」历史上正是这样坏的，见 `RelaxKonOS.Mobile.Progress.md`）。这里用反射把契约钉在
/// 编译产物上：不需要真实 Hub 连接、PTY 或网络，因此可以进默认套件。
/// </remarks>
internal static class TerminalHubContractChecks
{
    /// <summary>
    /// Hub 上真正可被 invoke 的方法。
    /// </summary>
    /// <remarks>
    /// <c>DeclaredOnly</c> 排除 <c>Hub</c> 基类成员；<c>IsVirtual</c> 排除
    /// <see cref="Microsoft.AspNetCore.SignalR.Hub.OnDisconnectedAsync"/> 这类生命周期重写——它是框架回调，
    /// 不是客户端可以 invoke 的方法。属性访问器由 <c>IsSpecialName</c> 排除。
    /// </remarks>
    private static List<MethodInfo> InvokableMethods() => typeof(TerminalHub)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName && !method.IsVirtual)
        .ToList();

    private static Dictionary<string, string> Constants(Type owner) => owner
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

    internal static void Run()
    {
        var methods = InvokableMethods();
        var methodNames = methods.Select(method => method.Name).ToHashSet(StringComparer.Ordinal);

        // client → server：每个常量都必须能落到一个 Hub 方法上。
        foreach (var (name, value) in Constants(typeof(TerminalHubMethods)))
        {
            TestAssert.Assert(name == value,
                $"TerminalHubMethods.{name} must keep method-name identity, but its value is '{value}'.");
            TestAssert.Assert(methodNames.Contains(value),
                $"TerminalHubMethods.{name} has no matching TerminalHub method; invoking '{value}' would fail at runtime.");
        }

        // 反向：Hub 上多出来的可 invoke 方法客户端无从调用，说明常量漏了一处。
        foreach (var method in methods)
            TestAssert.Assert(Constants(typeof(TerminalHubMethods)).ContainsValue(method.Name),
                $"TerminalHub.{method.Name} has no TerminalHubMethods constant, so no client can call it.");

        // server → client：事件名同理，必须与 ITerminalHubClient 的方法名一致。
        var clientMethods = typeof(ITerminalHubClient).GetMethods().Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in Constants(typeof(TerminalHubEvents)))
            TestAssert.Assert(clientMethods.Contains(value),
                $"TerminalHubEvents.{name} has no matching ITerminalHubClient method; 'On(\"{value}\")' would never fire.");

        MethodInfo Require(string name)
        {
            var method = methods.SingleOrDefault(candidate => candidate.Name == name);
            TestAssert.Assert(method is not null, $"TerminalHub.{name} is missing from the invoke contract.");
            return method!;
        }

        // 参数形状也是契约：SignalR 不应用 C# 默认值，`Start` 少传一个参数会让整次调用失败。
        var start = Require(TerminalHubMethods.Start).GetParameters();
        TestAssert.Assert(start.Length == 2
                && start[0].ParameterType == typeof(StartTerminalRequest)
                && start[1].ParameterType == typeof(string),
            "TerminalHub.Start must stay (StartTerminalRequest, string): SignalR matches by argument count and never applies a default value.");

        var attach = Require(TerminalHubMethods.AttachExisting).GetParameters();
        TestAssert.Assert(attach.Length == 1 && attach[0].ParameterType == typeof(string),
            "TerminalHub.AttachExisting must stay (string sessionId): it attaches an existing session or fails, never creates one.");

        // 关闭会话的 ID 来自客户端，服务端按 ID 删除，所以这里必须是「一个字符串」——多参数或非字符串
        // 会让归属校验的输入变得含糊。
        var close = Require(TerminalHubMethods.CloseSession).GetParameters();
        TestAssert.Assert(close.Length == 1 && close[0].ParameterType == typeof(string),
            "TerminalHub.CloseSession must take exactly one session id, the value the ownership check is performed against.");

        var resize = Require(TerminalHubMethods.Resize).GetParameters();
        TestAssert.Assert(resize.Length == 4 && resize.All(parameter => parameter.ParameterType == typeof(int)),
            "TerminalHub.Resize must stay (int columns, int rows, int widthPixels, int heightPixels).");

        Console.WriteLine("Terminal Hub contract checks passed.");
    }
}
