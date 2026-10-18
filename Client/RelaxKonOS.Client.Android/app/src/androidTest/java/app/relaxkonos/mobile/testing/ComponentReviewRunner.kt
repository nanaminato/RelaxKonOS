package app.relaxkonos.mobile.testing

import android.app.Application
import android.content.Context
import androidx.test.runner.AndroidJUnitRunner

/** Hosts isolated model/Compose fixtures without initializing unrelated process-wide services.
 * Full application, authentication, service and remote workflows use the normal runner.
 * Components that require AppContainer must be exercised with the production Application.
 */
class ComponentReviewRunner : AndroidJUnitRunner() {
    override fun newApplication(cl: ClassLoader?, className: String?, context: Context?): Application =
        super.newApplication(cl, Application::class.java.name, context)
}
