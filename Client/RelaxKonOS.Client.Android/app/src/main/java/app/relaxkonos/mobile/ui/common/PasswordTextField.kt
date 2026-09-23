package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import app.relaxkonos.mobile.R

/**
 * Password entry with a two-state reveal.
 *
 * The trailing eye switches the field between masked and plain text and *stays* there — it is a state,
 * not a press-and-hold gesture, so a long password can be checked without holding a key down. Only two
 * states exist: masked (the default, `PasswordVisualTransformation`) and revealed (`VisualTransformation
 * .None`).
 *
 * The state is presentation only, so it lives in `rememberSaveable`: it survives rotation and process
 * recreation of the screen but is never written to the session, the vault or any file. The password
 * itself keeps following the rule the callers already implement — a `String` only for as long as the
 * field holds it, a `CharArray` zeroed as soon as the request that needed it has finished.
 *
 * [supportingText] is where "a password is saved" is reported. It is the only place that fact may be
 * rendered: writing dots into `value`, or into a placeholder that looks identical to typed text, would
 * make the field claim the user had typed something
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §3, §6.2).
 */
@Composable
fun PasswordTextField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    supportingText: (@Composable () -> Unit)? = null,
) {
    var revealed by rememberSaveable { mutableStateOf(false) }
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        label = { Text(label) },
        singleLine = true,
        enabled = enabled,
        // Shares the caller's field shape so the password box never looks like a different control.
        shape = MaterialTheme.shapes.medium,
        supportingText = supportingText,
        visualTransformation = if (revealed) VisualTransformation.None else PasswordVisualTransformation(),
        trailingIcon = {
            IconButton(onClick = { revealed = !revealed }, enabled = enabled) {
                Icon(
                    painter = painterResource(
                        if (revealed) R.drawable.ic_password_hidden else R.drawable.ic_password_visible,
                    ),
                    contentDescription = stringResource(
                        if (revealed) R.string.password_hide else R.string.password_show,
                    ),
                )
            }
        },
    )
}
