package app.relaxkonos.mobile

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { RelaxKonOSTheme { RelaxKonOSApp() } }
    }
}

private val navigationItems = listOf("Home", "Files", "Terminal", "Manage", "More")

@Composable
private fun RelaxKonOSTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = androidx.compose.ui.graphics.Color(0xFF8AB4F8),
            background = androidx.compose.ui.graphics.Color(0xFF10151F),
            surface = androidx.compose.ui.graphics.Color(0xFF1B2433),
        ),
        content = content,
    )
}

@Composable
private fun RelaxKonOSApp() {
    var workspaceName by remember { mutableStateOf<String?>(null) }
    if (workspaceName == null) LoginScreen(onConnected = { workspaceName = it })
    else ShellScreen(workspaceName = workspaceName!!)
}

@Composable
private fun LoginScreen(onConnected: (String) -> Unit) {
    var serverUrl by remember { mutableStateOf("http://10.0.2.2:5090") }
    var identifier by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var status by remember { mutableStateOf("Connect to a RelaxKonOS server.") }
    var connecting by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val api = remember { RelaxKonApi() }

    Column(
        modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Card(modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(20.dp)) {
            Column(modifier = Modifier.padding(28.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("RelaxKonOS", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
                Text("Mobile control center", color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedTextField(serverUrl, { serverUrl = it }, Modifier.fillMaxWidth(), label = { Text("Server address") }, singleLine = true)
                OutlinedTextField(identifier, { identifier = it }, Modifier.fillMaxWidth(), label = { Text("Username") }, singleLine = true)
                OutlinedTextField(password, { password = it }, Modifier.fillMaxWidth(), label = { Text("Password") }, singleLine = true, visualTransformation = PasswordVisualTransformation())
                Text(status, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Button(
                    onClick = {
                        connecting = true
                        status = "Connecting…"
                        scope.launch {
                            when (val result = api.login(serverUrl, identifier, password)) {
                                is LoginResult.Success -> onConnected(result.workspaceName)
                                is LoginResult.Failure -> status = result.message
                            }
                            password = ""
                            connecting = false
                        }
                    },
                    modifier = Modifier.fillMaxWidth().height(48.dp),
                    enabled = !connecting,
                ) { Text(if (connecting) "Connecting…" else "Connect") }
            }
        }
    }
}

@Composable
private fun ShellScreen(workspaceName: String) = BoxWithConstraints(
    modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background),
) {
    when (layoutStateFor(maxWidth)) {
        LayoutState.Compact -> CompactShell(workspaceName)
        LayoutState.Medium -> WideShell(workspaceName, compactRail = true, showDetails = false)
        LayoutState.Expanded -> WideShell(workspaceName, compactRail = false, showDetails = true)
    }
}

@Composable
private fun CompactShell(workspaceName: String) {
    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Overview(workspaceName, Modifier.weight(1f))
        NavigationBar {
            navigationItems.forEach { item -> NavigationBarItem(selected = item == "Home", onClick = {}, icon = {}, label = { Text(item) }) }
        }
    }
}

@Composable
private fun WideShell(workspaceName: String, compactRail: Boolean, showDetails: Boolean) {
    Row(Modifier.fillMaxSize().padding(16.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        NavigationRail(Modifier.fillMaxHeight()) {
            navigationItems.forEach { item -> NavigationRailItem(selected = item == "Home", onClick = {}, icon = {}, label = { Text(if (compactRail) item.first().toString() else item) }) }
        }
        Overview(workspaceName, Modifier.weight(1f))
        if (showDetails) Card(Modifier.width(300.dp).fillMaxHeight()) { Text("Details", Modifier.padding(16.dp), style = MaterialTheme.typography.titleMedium) }
    }
}

@Composable
private fun Overview(workspaceName: String, modifier: Modifier = Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(18.dp)) {
        Text(workspaceName, style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        Text("Connected. The Kotlin/Compose Android shell provides the authenticated session foundation; Files, Terminal, and management pages follow in M1–M3.")
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            StatusCard("System status", Modifier.weight(1f))
            StatusCard("Server capabilities", Modifier.weight(1f))
        }
    }
}

@Composable
private fun StatusCard(title: String, modifier: Modifier) = Card(modifier.height(100.dp)) {
    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Text(title)
    }
}
