package sample.extensions

import sample.CompletionTarget
import sample.InnerCompletionTarget

fun CompletionTarget.bumpExtension(): Int = counter.next()
fun InnerCompletionTarget.innerExtension(): Int = counter.next()
