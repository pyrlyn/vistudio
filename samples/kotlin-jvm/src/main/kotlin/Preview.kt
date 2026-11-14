package sample

fun preview(): String = Greeting("Preview").message()

// Same spelling, different symbol: references to Greeting.message must exclude this call.
class OtherGreeting {
    fun message(): String = "Other"
}

fun otherPreview(): String = OtherGreeting().message()
