package app.relaxkonos.mobile.data

import android.content.Context

/**
 * 一条「本机能力所致、影响不大」的提醒的稳定身份。
 *
 * 只有**由本机能力决定的结论**才可以被静音：同一台设备上它不会因为再试一次而变得不同，反复
 * 弹窗只是噪音。凡是「本次操作的结果」——用户取消、指纹锁定、密钥失效、网络失败——都不在这里，
 * 因为那正是用户需要看到、并且往往需要采取行动的东西（`Shell.Design.md` §3.4）。
 *
 * [storageKey] 是落盘标识。它已经存在于用户设备上，**一旦发布不得再改**：改名等于让所有被用户
 * 关掉的提醒重新弹出来，而用户以为自己已经关掉了。枚举常量名可以随时重排。
 */
enum class ReminderKind(val storageKey: String) {
    /** 登录成功，但本机结构上无法保存密码：没有可用保险箱。 */
    LoginCredentialNotSavedDevice("login.credential-not-saved.device"),

    /** 本机当前无法用指纹/锁屏解封已保存的密码。登录、服务器中心与提权对话框共用同一条。 */
    SavedPasswordUnavailable("vault.unlock-unavailable"),

    /** 服务器中心：本机结构上无法保护这条 SSH 密码。 */
    ServerCenterCredentialNotSaved("server-center.credential-not-saved.device"),
}

/**
 * 「不再提醒」偏好的落盘接缝。
 *
 * 与 `Shell.Design.md` §8 的立场一致：这里只保存**键**，不保存提醒文案、账号、服务器地址，也不
 * 保存任何密文。因此它走 `SharedPreferences`，随卸载一起消失，不随系统备份跨设备迁移。
 */
interface NoticePreferenceStorage {
    fun read(): Set<String>

    fun write(keys: Set<String>)
}

/** `SharedPreferences` 实现；只存字符串集合。 */
class SharedPreferencesNoticeStorage(context: Context) : NoticePreferenceStorage {
    private val preferences = context.applicationContext
        .getSharedPreferences("relaxkonos.notices", Context.MODE_PRIVATE)

    override fun read(): Set<String> = preferences.getStringSet(SILENCED, emptySet()).orEmpty().toSet()

    override fun write(keys: Set<String>) {
        // 同步提交：静音是一次明确的用户决定，不能让进程在 `apply()` 落盘前退出而把它丢掉。
        check(preferences.edit().putStringSet(SILENCED, keys).commit()) {
            "Unable to save the notice preference."
        }
    }

    private companion object {
        const val SILENCED = "silenced"
    }
}

/** 内存实现，供单元测试使用。 */
class InMemoryNoticeStorage : NoticePreferenceStorage {
    private var keys: Set<String> = emptySet()

    override fun read(): Set<String> = keys

    override fun write(keys: Set<String>) {
        this.keys = keys.toSet()
    }
}

/**
 * 「不再提醒」偏好在界面侧的读法：读一次是否已静音，写一次静音。
 *
 * 单独一个接口，是因为共享反馈组件**不能**自己去拿 `AppContainer`——它在不提供容器的组合里
 * （例如 `ActionFeedbackTest`）也要能渲染。调用方把偏好传进来，组件就只依赖这两个动作。
 */
interface ReminderPreference {
    fun isSilenced(kind: ReminderKind): Boolean

    fun silence(kind: ReminderKind)
}

/**
 * 本机「不再提醒」偏好。
 *
 * 静音只影响**本机**：同一台服务器在另一台手机上该怎么提醒还怎么提醒。偏好可读回、可恢复
 * （`AccountSecurityScreen` 的提醒区），所以「不再提醒」不是一个不可逆的决定。
 */
class NoticePreferenceStore(private val storage: NoticePreferenceStorage) : ReminderPreference {
    @Synchronized
    override fun isSilenced(kind: ReminderKind): Boolean = kind.storageKey in storage.read()

    /** 已静音的提醒，按枚举声明顺序返回，供设置页列出。 */
    @Synchronized
    fun silenced(): List<ReminderKind> =
        ReminderKind.entries.filter { it.storageKey in storage.read() }

    @Synchronized
    override fun silence(kind: ReminderKind) = setSilenced(kind, true)

    @Synchronized
    fun setSilenced(kind: ReminderKind, value: Boolean) {
        val keys = storage.read().toMutableSet()
        if (value) keys += kind.storageKey else keys -= kind.storageKey
        storage.write(keys)
    }
}
