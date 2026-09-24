namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>
/// Validates the closed shape of a user-execution request. Both the Server-side direct adapter
/// and every privileged Helper dispatcher use this policy so newly added fields cannot be
/// silently accepted by one transport and ignored by another.
/// </summary>
public static class UserExecutionRequestPolicy
{
    public static bool IsValid(UserExecutionRequest request, bool terminal)
    {
        if (request.Identity is null || request.Version != UserExecutionProtocol.Version
            || request.OperationId is not { } operationId || operationId == Guid.Empty
            || !Enum.IsDefined(request.Operation))
            return false;

        var noDestination = request.DestinationPath is null;
        var noName = request.NewName is null && request.FileName is null;
        var noContent = request.ContentBase64 is null;
        var noMode = request.UnixMode is null;
        var noGit = request.GitArguments is null;
        var noTerminal = request.TerminalShell is null && request.TerminalColumns is null
            && request.TerminalRows is null && request.TerminalWidthPixels is null
            && request.TerminalHeightPixels is null;

        return request.Operation switch
        {
            UserExecutionOperationKind.FileGetSpecialLocations => !terminal && request.Path is null
                && noDestination && noName && noContent && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileListDirectory or UserExecutionOperationKind.FileGetInfo
                or UserExecutionOperationKind.FileRead or UserExecutionOperationKind.FileDelete
                or UserExecutionOperationKind.FileCreateDirectory or UserExecutionOperationKind.FileGetProperties
                => !terminal && request.Path is not null && noDestination && noName && noContent
                    && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileWrite => !terminal && request.Path is not null
                && noDestination && noName && request.ContentBase64 is not null && noMode
                && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileRename => !terminal && request.Path is not null
                && noDestination && request.NewName is not null && request.FileName is null
                && noContent && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileMove or UserExecutionOperationKind.FileCopy => !terminal
                && request.Path is not null && request.DestinationPath is not null && noName
                && noContent && noMode && noGit && noTerminal,
            UserExecutionOperationKind.FileUpload => !terminal && request.Path is not null
                && noDestination && request.NewName is null && request.FileName is not null
                && request.ContentBase64 is not null && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileSetUnixPermissions => !terminal && request.Path is not null
                && noDestination && noName && noContent && request.UnixMode is not null
                && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.GitExecute => !terminal && request.Path is not null
                && noDestination && noName && noContent && noMode && request.GitArguments is not null
                && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.TerminalStart => terminal && request.Path is not null
                && noDestination && noName && noContent && noMode && noGit
                && request.TerminalColumns is not null && request.TerminalRows is not null
                && request.TerminalWidthPixels is not null && request.TerminalHeightPixels is not null
                && !request.Overwrite,
            _ => false,
        };
    }
}
