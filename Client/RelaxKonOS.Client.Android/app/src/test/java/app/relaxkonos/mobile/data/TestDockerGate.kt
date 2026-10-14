package app.relaxkonos.mobile.data
import app.relaxkonos.mobile.core.net.ApiResult
internal fun testDockerGate(control: DockerControlJournal = DockerControlJournal(object : InstallationRequestStorage {
    override fun read(): ByteArray? = null
    override fun write(bytes: ByteArray) = Unit
}), resources: DockerResourceJournal = DockerResourceJournal(object : InstallationRequestStorage {
    override fun read(): ByteArray? = null
    override fun write(bytes: ByteArray) = Unit
}), compose: DockerComposeJournal = testComposeJournal()) = DockerMutationGate(control, resources, compose) { ApiResult.Success(Unit) }

internal fun testComposeJournal() = DockerComposeJournal(object : InstallationRequestStorage {
    private var bytes: ByteArray? = null
    override fun read() = bytes
    override fun write(bytes: ByteArray) { this.bytes = bytes }
})
