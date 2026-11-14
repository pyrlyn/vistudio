package sample

class GenericBox<T>(val value: T) { val memberItems: List<String> = listOf("Kotlin")
    fun genericValue(): T = value
    private fun <U> List<U>.lexMember(counter: EvaluationCounter): List<U> { counter.next(); return this }
    fun inspect(counter: EvaluationCounter) {
        println("Member calls: ${counter.calls}") // Additional private-member completion breakpoint.
    }
}

// This private extension must not leak into EvaluationMain.kt completion.
private fun List<String>.lexForeignPrivate(counter: EvaluationCounter): Int = counter.next()
