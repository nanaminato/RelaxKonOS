using System.Reflection;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Protocol.Docker;

internal static class DockerSubmissionChecks
{
    public static void Run()
    {
        var client = DispatchProxy.Create<IRemoteDockerClient, DockerSubmissionStub>();
        var stub = (DockerSubmissionStub)(object)client;
        var vm = new DockerManagerViewModel(client) { IsDockerAvailable = true, StackName = "draft", ComposeYaml = "services: {}" };
        var submitting = vm.TryDeployStackAsync();
        Check(!submitting.IsCompleted && vm.IsLoading, "Editor waits for a durable submission receipt.");
        Check(!vm.TryDeployStackAsync().GetAwaiter().GetResult() && stub.Submissions == 1, "Duplicate submission is blocked.");
        Check(stub.Request!.Definition.Name == "draft" && stub.Request.Definition.ComposeYaml == "services: {}", "Submission uses the frozen draft and preview version.");
        stub.Pending.SetException(new DockerStackRequestException("docker.test_refused"));
        Check(!submitting.GetAwaiter().GetResult() && !vm.IsLoading, "Rejected submission keeps the editor open and unlocks recovery.");
        Check(vm.StackName == "draft" && vm.ComposeYaml == "services: {}", "Rejection preserves the draft.");
        stub.Pending = new();
        submitting = vm.TryDeployStackAsync();
        stub.Pending.SetResult(new(Guid.NewGuid(), "draft", DockerStackOperationKind.Deploy,
            DockerStackOperationState.Succeeded, default, null, null, "test", DateTimeOffset.UtcNow,
            null, DateTimeOffset.UtcNow, false, []));
        Check(submitting.GetAwaiter().GetResult(), "A receipt permits closing the editor.");
        Console.WriteLine("Docker submission checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class DockerSubmissionStub : DispatchProxy
{
    public TaskCompletionSource<DockerStackOperationDto> Pending = new();
    public DockerStackDeployRequest? Request;
    public int Submissions;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "PreviewStackAsync")
        {
            var definition = (DockerStackDefinitionDto)args![0]!;
            return Task.FromResult(new DockerStackPreviewDto(definition.Name, new string('a', 64), [], [], []));
        }
        if (method.Name == "DeployStackAsync")
        {
            Request = (DockerStackDeployRequest)args![0]!;
            Submissions++;
            return Pending.Task;
        }
        if (method.Name == "GetStatusAsync") return Task.FromResult(new DockerStatusDto(true, "", "test", "test", "test"));
        var type = method.ReturnType.GetGenericArguments().Single();
        object? value = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? Array.CreateInstance(type.GetGenericArguments()[0], 0) : null;
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type).Invoke(null, [value]);
    }
}
