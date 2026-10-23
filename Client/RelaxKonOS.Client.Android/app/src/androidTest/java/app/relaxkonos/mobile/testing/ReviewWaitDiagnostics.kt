package app.relaxkonos.mobile.testing

import android.util.Log
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.rules.TestWatcher
import org.junit.runner.Description

/** Records waits without changing test deadlines or requiring a debugger/rooted device. */
class ReviewWaitDiagnostics : TestWatcher() {
    companion object { private val generation = AtomicInteger() }
    private var finished = CountDownLatch(1)
    private var startedAt = 0

    private fun dump(description: Description, phase: String) {
        Log.e("ReviewWait", "$phase: ${description.className}.${description.methodName}")
        Thread.getAllStackTraces().forEach { (thread, frames) ->
            if (thread.name == "main" || thread.name.contains("Instr", ignoreCase = true)) {
                Log.e("ReviewWait", "${thread.name}: ${thread.state}")
                frames.forEach { Log.e("ReviewWait", it.toString()) }
            }
        }
    }

    override fun starting(description: Description) {
        startedAt = generation.incrementAndGet()
        val completion = CountDownLatch(1)
        finished = completion
        Thread({
            if (!completion.await(30, TimeUnit.SECONDS)) {
                dump(description, "Still running")
            }
        }, "Review wait diagnostics").apply { isDaemon = true; start() }
    }

    override fun finished(description: Description) {
        finished.countDown()
        val completedAt = startedAt
        Thread({
            Thread.sleep(30_000)
            if (generation.get() == completedAt) dump(description, "No next test after completion")
        }, "Review transition diagnostics").apply { isDaemon = true; start() }
    }
}
