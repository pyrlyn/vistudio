package sample.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import sample.core.greeting
import sample.core.platforms

@Composable
fun GreetingCard(name: String) {
    Column(
        Modifier
            .background(Color(0xFF2B5CE6), RoundedCornerShape(12.dp))
            .padding(16.dp)
    ) {
        BasicText(greeting(name), style = TextStyle(color = Color.White, fontSize = 20.sp))
        Spacer(Modifier.size(8.dp))
        platforms.forEach { platform ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Spacer(Modifier.size(8.dp).background(Color(0xFFFFC107), RoundedCornerShape(4.dp)))
                Spacer(Modifier.width(8.dp))
                BasicText(platform, style = TextStyle(color = Color.White, fontSize = 14.sp))
            }
        }
    }
}

@Preview
@Composable
fun GreetingCardPreview() {
    GreetingCard("Visual Studio")
}

@Preview(name = "Badge on white", widthDp = 160, heightDp = 60, showBackground = true)
@Composable
private fun BadgePreview() {
    BasicText(
        "KMP + Compose",
        Modifier.background(Color(0xFF7F52FF), RoundedCornerShape(50)).padding(horizontal = 12.dp, vertical = 6.dp),
        style = TextStyle(color = Color.White)
    )
}

@Preview(name = "Failing preview")
@Composable
fun FailingPreview() {
    error("This preview fails on purpose")
}
