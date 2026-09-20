namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>
/// Stable problem codes. Clients localize these; raw Docker command text is never a product message.
/// </summary>
public static class ApplicationDeploymentProblemCodes
{
    // Ledger and input validation.
    public const string StoreUnavailable = "application-deployment.store_unavailable";
    public const string InvalidRequest = "application-deployment.invalid_request";
    public const string ApplicationNotFound = "application-deployment.application_not_found";
    public const string OperationNotFound = "application-deployment.operation_not_found";
    public const string RevisionNotFound = "application-deployment.revision_not_found";
    public const string PermissionDenied = "application-deployment.permission_denied";
    public const string IdempotencyRequired = "application-deployment.idempotency_required";
    public const string IdempotencyConflict = "application-deployment.idempotency_conflict";
    public const string ResourceConflict = "application-deployment.resource_conflict";
    public const string NameConflict = "application-deployment.name_conflict";
    public const string ConfirmationRequired = "application-deployment.confirmation_required";
    public const string NotCancellable = "application-deployment.not_cancellable";
    public const string AlreadyActive = "application-deployment.already_active";

    // Preflight.
    public const string EngineUnavailable = "application-deployment.engine_unavailable";
    public const string EngineNotInstalled = "application-deployment.engine_not_installed";
    public const string PlatformUnsupported = "application-deployment.platform_unsupported";
    public const string PortUnavailable = "application-deployment.port_unavailable";
    public const string PortConflict = "application-deployment.port_conflict";
    public const string DiskFull = "application-deployment.disk_full";
    public const string ElevationRequired = "application-deployment.elevation_required";

    // Input handling.
    public const string FileReferenceUnavailable = "application-deployment.file_reference_unavailable";
    public const string ArchiveUnavailable = "application-deployment.archive_unavailable";
    public const string ArchiveTooLarge = "application-deployment.archive_too_large";
    public const string ArchiveUnsafeEntry = "application-deployment.archive_unsafe_entry";
    public const string ArchiveTooManyEntries = "application-deployment.archive_too_many_entries";
    public const string ArchiveExpandedTooLarge = "application-deployment.archive_expanded_too_large";
    public const string ArchiveContentInvalid = "application-deployment.archive_content_invalid";
    public const string ImageReferenceInvalid = "application-deployment.image_reference_invalid";
    public const string ImageNotFound = "application-deployment.image_not_found";
    public const string ImagePlatformMismatch = "application-deployment.image_platform_mismatch";
    public const string RegistryAuthenticationFailed = "application-deployment.registry_authentication_failed";
    public const string RegistryUnreachable = "application-deployment.registry_unreachable";
    public const string EntryPointInvalid = "application-deployment.entry_point_invalid";
    public const string RuntimeMismatch = "application-deployment.runtime_mismatch";
    public const string DependencyInstallFailed = "application-deployment.dependency_install_failed";
    public const string BuildFailed = "application-deployment.build_failed";
    public const string SecretVersionMissing = "application-deployment.secret_version_missing";

    // Runtime.
    public const string ContainerCreateFailed = "application-deployment.container_create_failed";
    public const string ContainerStartFailed = "application-deployment.container_start_failed";
    public const string ContainerStopFailed = "application-deployment.container_stop_failed";
    public const string ContainerRemoveFailed = "application-deployment.container_remove_failed";
    public const string HealthCheckFailed = "application-deployment.health_check_failed";
    public const string HealthCheckTimeout = "application-deployment.health_check_timeout";
    public const string ActivationFailed = "application-deployment.activation_failed";
    public const string ProxyValidationFailed = "application-deployment.proxy_validation_failed";
    public const string ProxyUnavailable = "application-deployment.proxy_unavailable";
    public const string VolumeCreateFailed = "application-deployment.volume_create_failed";
    public const string VolumeRemoveFailed = "application-deployment.volume_remove_failed";
    public const string StagingCleanupFailed = "application-deployment.staging_cleanup_failed";

    // Operation outcomes.
    public const string Failed = "application-deployment.failed";
    public const string Cancelled = "application-deployment.cancelled";
    public const string Interrupted = "application-deployment.interrupted";
    public const string NotSupported = "application-deployment.not_supported";

    // Recovery: reported separately from the original failure.
    public const string RecoveryFailed = "application-deployment.recovery_failed";
    public const string RecoveryUnknown = "application-deployment.recovery_unknown";
    public const string PreviousInstanceRestored = "application-deployment.previous_instance_restored";
    public const string OrphanedResources = "application-deployment.orphaned_resources";

    // Reconciliation and drift.
    public const string DriftContainerMissing = "application-deployment.drift_container_missing";
    public const string DriftContainerExternallyModified = "application-deployment.drift_container_externally_modified";
    public const string DriftImageMissing = "application-deployment.drift_image_missing";
    public const string DriftVolumeMissing = "application-deployment.drift_volume_missing";
    public const string DriftUnownedResource = "application-deployment.drift_unowned_resource";
}
