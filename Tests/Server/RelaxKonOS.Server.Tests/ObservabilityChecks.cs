using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Server.Observability;

internal static class ObservabilityChecks
{
    public static void VerifyProtocolAndSanitization()
    {
        TestAssert.Equal(ObservabilityEventCatalog.All.Count, ObservabilityEventCatalog.All.Select(entry => entry.Id).Distinct().Count(), "observability event IDs must be unique");
        TestAssert.Equal(ObservabilityEventCatalog.All.Count, ObservabilityEventCatalog.All.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count(), "observability event names must be unique");
        var sanitizer = new ObservabilitySanitizer(new ObservabilityOptions { AuditHmacKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var unsafeText = "password=hunter2 Authorization: Bearer abc.def.ghi https://alice:secret@example.test/a";
        var safe = sanitizer.SanitizeSummary(unsafeText);
        TestAssert.True(!safe.Contains("hunter2", StringComparison.Ordinal) && !safe.Contains("abc.def.ghi", StringComparison.Ordinal) && !safe.Contains("alice:secret", StringComparison.Ordinal), "sanitizer must not retain supplied secrets");
        TestAssert.Equal(sanitizer.ToReference("/private/example"), sanitizer.ToReference("/private/example"), "references must be stable within one installation");
        TestAssert.True(!new CorrelationContext(Guid.Empty).IsValid(), "empty correlation IDs must be rejected");
        TestAssert.True(CorrelationContext.Create(action: "privileged.operation").IsValid(), "catalog action must be accepted");
    }
}
