package sample
import sample.extensions.*
// A separate entry point for Watch/Immediate acceptance checks.
fun main(args: Array<String>) {
    val greeting = Greeting(args.firstOrNull() ?: "Kotlin")
    val counter = EvaluationCounter(); val target = CompletionTarget(counter); val items: List<String> = listOf("Kotlin"); val box = GenericBox(items); fun List<String>.lexLocalStrings(): Int = counter.next()
    val nullableName: String? = null
    println(greeting.name) // Set a breakpoint here before evaluating expressions.
    println("Evaluation calls: ${counter.calls}; fallback: ${nullableName ?: "Guest"}")
    box.inspect(counter); completionFrame(greeting, counter)
}

private fun completionFrame(value: Greeting, counter: EvaluationCounter) {
    val frameOnly = "Inner frame"; val target = InnerCompletionTarget(counter); val items: List<Int> = listOf(42); val box = GenericBox(items); fun List<Int>.lexLocalInts(): Int = counter.next()
    println("Frame locals: $frameOnly; ${value.name}; calls: ${counter.calls}")
}

class EvaluationCounter {
    var calls: Int = 0
        private set

    fun next(): Int {
        calls += 1
        return calls
    }

    // Cross the backend's original 500 ms hover limit after the side effect.
    fun slowNext(): Int {
        val result = next()
        Thread.sleep(750)
        return result
    }
}

private fun List<String>.lexPrivateStrings(counter: EvaluationCounter): Int = counter.next()
private fun List<Int>.lexPrivateInts(counter: EvaluationCounter): Int = counter.next()
private fun <T> List<T>.lexPrivateGeneric(counter: EvaluationCounter): T? { counter.next(); return firstOrNull() }
private fun <T> GenericBox<T>.lexBox(counter: EvaluationCounter): T { counter.next(); return value }

private fun unrelatedCompletionScope(counter: EvaluationCounter) {
    fun List<String>.lexUnrelatedLocal(): Int = counter.next()
    println(counter.calls)
}
