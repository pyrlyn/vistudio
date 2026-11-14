package sample

import androidx.compose.ui.window.Window
import androidx.compose.ui.window.application
import sample.ui.GreetingCard

fun main() = application {
    Window(onCloseRequest = ::exitApplication, title = "Vistudio Compose sample") {
        GreetingCard("Desktop")
    }
}
