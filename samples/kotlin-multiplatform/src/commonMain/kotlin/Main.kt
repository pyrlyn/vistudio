package sample

fun greeting(name: String): String = "Hello, $name!"

fun main() {
    check(greeting("Kotlin") == "Hello, Kotlin!")
    check(listOf(1, 2, 3).map { it * 2 }.sum() == 12)
    check(runCatching { error("expected") }.exceptionOrNull()?.message == "expected")
    check(platformName().isNotEmpty())
    println("Platform: " + platformName())
    println("VISTUDIO_KMP_PASS")
}
