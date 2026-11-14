package sample

data class Greeting(val name: String) {
    fun message(): String = "Hello, $name from Visual Studio!"
}
