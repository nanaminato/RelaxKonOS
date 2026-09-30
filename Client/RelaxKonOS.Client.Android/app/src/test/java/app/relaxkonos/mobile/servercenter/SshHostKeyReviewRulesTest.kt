package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 「还差哪一步」的判定契约。
 *
 * 重点在于：指纹变更与首次见面都必须要求用户显式确认，但只有变更需要并排展示旧指纹；
 * 而认证失败不能被当成一次指纹核对请求。
 */
class SshHostKeyReviewRulesTest {

    private val oldBlob = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
    private val newBlob = byteArrayOf(9, 9, 9, 9, 9, 9, 9, 9)

    private fun observation(blob: ByteArray = newBlob) = ServerCenterHostKeyObservation(
        host = "host.example.com",
        port = 22,
        algorithm = "ssh-ed25519",
        publicKeyBlob = blob,
    )

    private fun pinned(blob: ByteArray = oldBlob) = ServerHostKeyRecord(
        host = "host.example.com",
        port = 22,
        algorithm = "ssh-ed25519",
        publicKeyBase64 = Base64Codec.encode(blob),
        fingerprint = ServerHostTrustRules.fingerprint(blob),
        confirmedAtEpochMillis = 1_700_000_000_000L,
    )

    @Test
    fun `first contact needs a review without a previous pin`() {
        val review = planSshHostKeyReview(ServerCenterSshVerification.NeedsTrust(observation()))
        assertEquals(observation().fingerprint, review?.observation?.fingerprint)
        assertNull(review?.previous)
        assertFalse(review!!.replacesPinnedKey)
    }

    @Test
    fun `changed key carries the superseded pin so both fingerprints can be compared`() {
        val previous = pinned()
        val review = planSshHostKeyReview(
            ServerCenterSshVerification.KeyChanged(observation(), previous),
        )
        assertTrue(review!!.replacesPinnedKey)
        assertEquals(previous, review.previous)
        assertEquals(1_700_000_000_000L, review.previous?.confirmedAtEpochMillis)
        assertEquals(ServerHostTrustRules.fingerprint(oldBlob), review.previous?.fingerprint)
        assertEquals(ServerHostTrustRules.fingerprint(newBlob), review.observation.fingerprint)
    }

    @Test
    fun `a trusted or failed handshake never asks for a fingerprint review`() {
        // 把认证失败弹成指纹核对，就是让用户对一个与密钥无关的问题做信任决定。
        assertNull(planSshHostKeyReview(ServerCenterSshVerification.Trusted("SHA256:whatever")))
        assertNull(planSshHostKeyReview(ServerCenterSshVerification.Failed(SshFailureReason.AuthenticationRejected)))
        assertNull(planSshHostKeyReview(null))
    }

    @Test
    fun `both review kinds replace the pin through the same single record rule`() {
        // 确认动作对两种情形是同一个：写入固定时同端点同算法只保留一条。
        val replaced = ServerHostTrustRules.replace(listOf(pinned()), pinned(newBlob))
        assertEquals(1, replaced.size)
        assertEquals(
            ServerHostKeyTrust.Trusted,
            ServerHostTrustRules.evaluate(replaced, "host.example.com", 22, "ssh-ed25519", newBlob),
        )
    }
}
