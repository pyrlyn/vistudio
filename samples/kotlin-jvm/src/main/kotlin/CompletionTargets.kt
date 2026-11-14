package sample

// Showing completion for these methods must not invoke them.
class CompletionTarget(val counter: EvaluationCounter) {
    fun bumpMethod(): Int = counter.next()
    fun overload(value: Int): Int = counter.next() + value
    fun overload(value: String): String = "${counter.next()}: $value"
}

class InnerCompletionTarget(val counter: EvaluationCounter) {
    fun innerMethod(): Int = counter.next()
}
