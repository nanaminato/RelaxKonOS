package app.relaxkonos.mobile.data
import app.relaxkonos.mobile.core.net.ApiResult
internal fun testDockerGate(control: DockerControlJournal = DockerControlJournal(object : InstallationRequestStorage {
    override fun read(): ByteArray? = null
    override fun write(bytes: ByteArray) = Unit
}), resources: DockerResourceJournal = DockerResourceJournal(object : InstallationRequestStorage {
    override fun read(): ByteArray? = null
    override fun write(bytes: ByteArray) = Unit
})) = DockerMutationGate(control, resources) { ApiResult.Success(Unit) }
