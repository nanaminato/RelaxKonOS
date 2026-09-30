package com.jcraft.jsch

import app.relaxkonos.mobile.servercenter.JschInteractiveTerminal
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import org.junit.Assert.*
import org.junit.Test

// JSch's shell constructor is package-private; use its package for this fixture.
class JschInteractiveTerminalTest {
    private class RecordingShell : ChannelShell() {
        val sizes = mutableListOf<Pair<Int, Int>>()
        val resized = CompletableDeferred<Thread>()
        var connectedForTest = true
        override fun isConnected() = connectedForTest
        override fun setPtySize(columns: Int, rows: Int, width: Int, height: Int) {
            sizes += columns to rows
            resized.complete(Thread.currentThread())
        }
        override fun disconnect() { connectedForTest = false }
    }

    @Test fun `resize dispatches synchronous SSH requests away from the calling thread`() = runBlocking {
        val caller = Thread.currentThread()
        val shell = RecordingShell()
        val terminal = JschInteractiveTerminal(shell, ByteArrayInputStream(byteArrayOf()), ByteArrayOutputStream())
        terminal.resize(48, 10)
        assertNotSame(caller, shell.resized.await())
        assertEquals(listOf(48 to 10), shell.sizes)
    }

    @Test fun `resize waits for pending input to finish without blocking the caller`() = runBlocking {
        val shell = RecordingShell()
        val writeStarted = CountDownLatch(1)
        val releaseWrite = CountDownLatch(1)
        val output = object : ByteArrayOutputStream() {
            override fun write(bytes: ByteArray, offset: Int, length: Int) {
                writeStarted.countDown()
                check(releaseWrite.await(5, TimeUnit.SECONDS))
                super.write(bytes, offset, length)
            }
        }
        val terminal = JschInteractiveTerminal(shell, ByteArrayInputStream(byteArrayOf()), output)
        val write = async(Dispatchers.Default) { terminal.write("echo first\r") }
        try {
            assertTrue(writeStarted.await(5, TimeUnit.SECONDS))
            val resize = async(Dispatchers.Default) { terminal.resize(48, 10) }
            assertNull(withTimeoutOrNull(200) { shell.resized.await() })
            releaseWrite.countDown()
            withTimeout(5_000) { write.await(); resize.await() }
            assertEquals("echo first\r", output.toString("UTF-8"))
            assertEquals(listOf(48 to 10), shell.sizes)
        } finally {
            releaseWrite.countDown()
        }
    }

    @Test fun `keyboard resize and repeated input preserve streams until explicit close`() = runBlocking {
        val shell = RecordingShell()
        val output = ByteArrayOutputStream()
        val terminal = JschInteractiveTerminal(shell, ByteArrayInputStream("手机输出\nuser:~$ ".toByteArray()), output)
        terminal.resize(48, 8)
        terminal.write("echo 手机输入\r")
        terminal.resize(48, 24)
        terminal.write("pwd\r")
        assertTrue(shell.isConnected)
        assertEquals("echo 手机输入\rpwd\r", output.toString("UTF-8"))
        assertEquals("手机输出\nuser:~$ ", terminal.read())
        assertNull(terminal.read())
        terminal.close()
        assertFalse(shell.isConnected)
        terminal.resize(48, 10)
        assertEquals(listOf(48 to 8, 48 to 24), shell.sizes)
    }
}
