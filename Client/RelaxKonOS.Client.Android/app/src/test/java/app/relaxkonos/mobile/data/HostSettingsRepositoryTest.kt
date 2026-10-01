package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class HostSettingsRepositoryTest {
    private class Storage : InstallationRequestStorage { var bytes:ByteArray?=null; override fun read()=bytes; override fun write(bytes:ByteArray){this.bytes=bytes.copyOf()} }
    private val gateway=FakeGateway(); private val session=AuthSession(gateway); private val storage=Storage();private val journal=HostSettingsJournal(storage)
    private val elevations=ElevationRepository(gateway,session,CredentialVault(InMemoryVaultStorage(),FakeVaultCrypto()))
    private val repository=HostSettingsRepository(gateway,session,elevations,journal)
    private val target=HostSettingsTarget("host/time","hostMachine",null)
    private val revision="A".repeat(64)
    private val plan=HostSettingsPlan("ca57b835-8242-47bb-8a2b-c3a5f4658a6b",target,revision,"2099-10-01T00:00:00Z",listOf(HostSettingsDifference("host.time.zone","Old","New")),"hostTimeChange","host/time","immediate","settings.time.affects_host_time_display")
    private val applied=HostSettingsOperation(plan.id,"host.time.zone",target,"applied","2026-10-01T00:00:00Z",revision,null,"immediate")
    private suspend fun login(name:String="alice"):SessionState.Active {
        gateway.onLogin={_,_,_->ApiResult.Success(loginSession().let{it.copy(userName=name,server=it.server.copy(privilegedOperations=true))})}
        session.login(ServerConnectionIdentityRules.direct("https://host.test"),name,"pw".toCharArray()){}
        return session.state.value as SessionState.Active
    }
    @Test fun `lost apply persists original plan no values and prevents new writes after reopening`()=runTest {
        val owner=login();var calls=0
        gateway.onHostApply={kind,id->calls++;assertEquals(HostSettingKind.Time,kind);assertEquals(plan.id,id);ApiResult.Transport(null)}
        assertTrue(repository.apply(owner,HostSettingKind.Time,plan,ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertTrue(HostSettingsJournal(storage).references(owner).single().unresolved)
        assertFalse(storage.bytes!!.decodeToString().contains("New"));assertFalse(storage.bytes!!.decodeToString().contains(revision))
        assertTrue(runCatching{repository.apply(owner,HostSettingKind.Time,plan,ElevationAnswerProvider.Declines)}.isFailure);assertEquals(1,calls)
    }
    @Test fun `original operation terminal receipt clears gate but wrong target stays unresolved`()=runTest {
        val owner=login();gateway.onHostApply={_,_->ApiResult.Success(applied.copy(target=target.copy(resourceId="other")))}
        assertTrue(repository.apply(owner,HostSettingKind.Time,plan,ElevationAnswerProvider.Declines) is ApiResult.Transport)
        val ref=repository.references(owner).single();assertTrue(ref.unresolved)
        gateway.onHostOperation={id->assertEquals(plan.id,id);ApiResult.Success(applied)}
        assertTrue(repository.operation(owner,ref) is ApiResult.Success);assertFalse(repository.references(owner).single().unresolved)
    }
    @Test fun `exact grant refusal may retry same plan once and clears administrator password`()=runTest {
        val owner=login();var calls=0;val password="admin-secret".toCharArray()
        gateway.onHostApply={_,id->assertEquals(plan.id,id);calls++;if(calls==1) ApiResult.Problem(428,"settings.elevation_required",null) else ApiResult.Success(applied)}
        gateway.onElevation={_,_,capability,resource,_,_->assertEquals("hostTimeChange",capability);assertEquals("host/time",resource);ApiResult.Success(ElevationGrant(true,null))}
        assertTrue(repository.apply(owner,HostSettingKind.Time,plan,ElevationAnswerProvider{_,_->ElevationAnswer("root",password)}) is ApiResult.Success)
        assertEquals(2,calls);assertTrue(password.all{it=='\u0000'})
    }
    @Test fun `cancellation after dispatch keeps recovery id without resubmission`()=runTest {
        val owner=login();val sent=CompletableDeferred<Unit>();var calls=0
        gateway.onHostApply={_,_->calls++;sent.complete(Unit);awaitCancellation()}
        val job=launch{repository.apply(owner,HostSettingKind.Time,plan,ElevationAnswerProvider.Declines)}
        sent.await();job.cancelAndJoin();assertEquals(1,calls);assertTrue(repository.references(owner).single().unresolved)
    }
    @Test fun `rollback compares original operation and never sends after changed facts`()=runTest {
        val owner=login();gateway.onHostApply={_,_->ApiResult.Success(applied)}
        repository.apply(owner,HostSettingKind.Time,plan,ElevationAnswerProvider.Declines)
        gateway.onHostOperation={_->ApiResult.Success(applied.copy(updatedAt="2026-10-01T00:00:01Z"))}
        val result=repository.rollback(owner,repository.references(owner).single(),applied,ElevationAnswerProvider.Declines)
        assertEquals("settings.revision_conflict",(result as ApiResult.Problem).code)
    }
    @Test fun `only terminal server facts clear the gate and owner swap cannot read another reference`()=runTest {
        val owner=login();val expired=plan.copy(expiresAt="2000-01-01T00:00:00Z");val ref=journal.record(owner,HostSettingKind.Time,expired)
        gateway.onHostOperation={_->ApiResult.Success(applied.copy(state="unknown",observedRevision=null))}
        repository.operation(owner,ref);assertTrue(repository.references(owner).single().unresolved)
        gateway.onHostOperation={_->ApiResult.Success(applied.copy(state="failed",observedRevision=null,problemCode="settings.plan_expired"))}
        repository.operation(owner,ref);assertFalse(repository.references(owner).single().unresolved)
        val other=login("bob");assertTrue(repository.references(other).isEmpty())
        assertTrue(runCatching{repository.operation(owner,ref)}.exceptionOrNull() is CancellationException)
    }
}
