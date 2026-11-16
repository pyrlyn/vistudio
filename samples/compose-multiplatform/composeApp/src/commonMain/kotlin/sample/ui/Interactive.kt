package sample.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay

// Previews for the interactive mode of the Kotlin Compose Preview window: they react to clicks, typing and time.

private val counterColors = listOf(Color(0xFFD32F2F), Color(0xFF2E7D32), Color(0xFF1565C0))

/** A button that counts its clicks; its color cycles red, green, blue. */
@Preview(name = "Counter", widthDp = 240, heightDp = 120, showBackground = true)
@Composable
fun CounterPreview() {
    var count by remember { mutableStateOf(0) }
    Box(Modifier.padding(20.dp)) {
        Box(
            Modifier
                .size(200.dp, 80.dp)
                .background(counterColors[count % counterColors.size], RoundedCornerShape(12.dp))
                .clickable { count++ },
            contentAlignment = Alignment.Center
        ) {
            BasicText("Clicked $count times", style = TextStyle(color = Color.White, fontSize = 18.sp))
        }
    }
}

/** A text field; the bar below it turns green once the text is "abc". */
@Preview(name = "Text field", widthDp = 240, heightDp = 120, showBackground = true)
@Composable
fun TextFieldPreview() {
    var text by remember { mutableStateOf("") }
    Column(Modifier.padding(20.dp)) {
        BasicTextField(
            text, { text = it },
            Modifier.width(200.dp).background(Color(0xFFEEEEEE)).padding(8.dp),
            textStyle = TextStyle(fontSize = 16.sp)
        )
        Spacer(Modifier.height(12.dp))
        Box(Modifier.size(200.dp, 24.dp).background(if (text == "abc") Color(0xFF2E7D32) else Color(0xFFD32F2F)))
    }
}

/** A square that changes color by itself: the preview keeps rendering while effects run. */
@Preview(name = "Timer", widthDp = 120, heightDp = 120)
@Composable
fun TimerPreview() {
    var ticks by remember { mutableStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(300)
            ticks++
        }
    }
    Box(Modifier.size(120.dp).background(counterColors[ticks % counterColors.size]))
}
