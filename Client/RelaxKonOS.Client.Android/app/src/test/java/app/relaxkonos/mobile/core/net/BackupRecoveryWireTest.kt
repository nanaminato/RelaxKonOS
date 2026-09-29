package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BackupRecoveryWireTest {
    private val manifest = """{"backupId":"b-1","operationId":"o-1","applicationId":"a-1","state":"verified","keyId":"operator-v1","objects":[{"kind":"applicationDefinition","reference":"a-1","consistencyMethod":"catalog-transaction","length":42,"sha256":"${"a".repeat(64)}"}],"problemCode":null}"""

    @Test fun `parses manifest list and preflight blockers`() {
        val parsed = BackupRecoveryWire.manifests("[$manifest]").single()
        assertEquals("b-1", parsed.backupId)
        assertEquals("catalog-transaction", parsed.objects.single().consistencyMethod)
        val preflight = BackupRecoveryWire.preflight("""{"backupId":"b-1","sourceApplicationId":"a-1","suggestedApplicationName":"app-restored","createsNewInstance":true,"canRestore":false,"blockers":["backup-recovery.secret_rebind_required"]}""")
        assertFalse(preflight.canRestore)
        assertTrue(preflight.createsNewInstance)
        assertEquals(listOf("backup-recovery.secret_rebind_required"), preflight.blockers)
    }

    @Test fun `encodes backup route identifiers`() {
        assertEquals("/api/v1.0/backup-recovery/backups/a%2Fb", BackupRecoveryRoutes.backup("a/b"))
        assertEquals("/api/v1.0/backup-recovery/applications/a%2Fb/backups", BackupRecoveryRoutes.backups("a/b"))
        assertEquals("/api/v1.0/backup-recovery/applications/a%2Fb/definition-backups", BackupRecoveryRoutes.definitionBackup("a/b"))
        assertEquals("/api/v1.0/backup-recovery/applications/a%2Fb/definition-backup-request", BackupRecoveryRoutes.definitionBackupRequest("a/b"))
    }
}
