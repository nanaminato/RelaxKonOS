package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ManagedRuntimeCatalogTest {
    private val payload = """{"source":{"runtimeId":"mihomo","baseUri":"https://release.invalid/","confirmationFingerprint":"fingerprint","identity":{"sourceId":"official","publisher":"Publisher","enabled":true,"trust":"confirmed","trustRevision":5}},"fresh":true,"problemCode":null,"catalog":{"sourceId":"official","revision":4,"trustRevision":5,"signatureVerified":true,"releases":[]}}"""
    @Test fun catalogCannotBorrowAnotherSourceIdentity() {
        val value = JSONObject(payload)
        value.getJSONObject("catalog").put("sourceId", "other")
        assertThrows(IllegalArgumentException::class.java) { ManagedRuntimeWire.catalog(value.toString()) }
    }
    @Test fun releasesCannotBorrowAnotherSourceIdentity() {
        val value = JSONObject(payload)
        value.getJSONObject("catalog").getJSONArray("releases").put(JSONObject("""{"sourceId":"other","runtimeId":"mihomo","releaseId":"release-b","originalVersion":"1.19.30","trust":"confirmed","configurationSupported":true,"channel":"stable","target":{"os":"linux","architecture":"amd64","serviceHost":"systemd"},"evidence":"nativeConfiguration"}"""))
        assertThrows(IllegalArgumentException::class.java) { ManagedRuntimeWire.catalog(value.toString()) }
    }
}
