package mixed

fun main() {
    val ledger = Ledger("Kotlin")
    ledger.add(3)
    val total = ledger.applyKotlinRule(4)
    val report = JavaEntry.describe(ledger)
    println("total=$total $report")
}

fun doubleIt(amount: Int): Int {
    val doubled = amount * 2
    return doubled
}
