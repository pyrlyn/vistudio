package sample

object OwnerCompletionMain {
    @JvmStatic
    fun main(args: Array<String>) {
        val counter = EvaluationCounter()
        OuterOwner("Kotlin").pause(counter)
        println("Owner completion calls: ${counter.calls}")
    }
}

private class OuterOwner<T : CharSequence>(val value: T) {
    private fun T.ownerOuterValue(counter: EvaluationCounter): T { counter.next(); return this }
    private fun List<T>.ownerOuterList(counter: EvaluationCounter): List<T> { counter.next(); return this }
    private fun Int.ownerWrong(counter: EvaluationCounter): Int = counter.next() + this

    fun pause(counter: EvaluationCounter) {
        val items: List<T> = listOf(value)
        println("Outer pause calls: ${counter.calls}") // Outer breakpoint.
        InnerOwner(42).pause(counter)
        println("Outer resume calls: ${counter.calls}")
    }
}

private class InnerOwner<T : Number>(val value: T) {
    private fun T.ownerInnerValue(counter: EvaluationCounter): T { counter.next(); return this }
    private fun List<T>.ownerInnerList(counter: EvaluationCounter): List<T> { counter.next(); return this }
    private fun String.ownerWrong(counter: EvaluationCounter): Int = counter.next() + length

    fun pause(counter: EvaluationCounter) {
        val items: List<T> = listOf(value)
        println("Inner pause calls: ${counter.calls}") // Inner breakpoint.
    }
}
