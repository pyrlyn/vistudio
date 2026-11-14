package sample

/* Outer comment with // ordinary text
   /* Nested block comment */ still outer */
data class Greeting(val name: String)

fun main() {
    val greeting = Greeting("Kotlin")
    val raw = """Hello, ${greeting.name}! ${listOf(1, 2).map { it + 1 }}"""
    val escaped = "line\n${greeting.name}"
    val `odd name` = 0xFFu
    println(raw)
    println(escaped)
    println(`odd name`)
}
