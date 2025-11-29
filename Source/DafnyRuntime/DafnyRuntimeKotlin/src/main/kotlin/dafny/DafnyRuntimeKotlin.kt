// Dafny Runtime for Kotlin - Minimal implementation
// Only essential functions for generated Dafny code

package dafny

// CodePoint type (represents Dafny char)
typealias CodePoint = Char

// DafnySequence - simple wrapper around List
class DafnySequence<T>(val elements: List<T>) : List<T> by elements {
    fun verbatimString(): String = this.joinToString("")

    companion object {
        fun asUnicodeString(value: Any): DafnySequence<Char> {
            return when (value) {
                is String -> DafnySequence(value.toList())
                is DafnySequence<*> -> DafnySequence(value.map { it as Char })
                else -> DafnySequence(value.toString().toList())
            }
        }
    }
}

// Helpers for main entry point
object Helpers {
    fun withHaltHandling(block: () -> Unit) {
        try {
            block()
        } catch (e: Exception) {
            e.printStackTrace()
            System.exit(1)
        }
    }

    fun UnicodeFromMainArguments(args: Array<String>): DafnySequence<DafnySequence<CodePoint>> {
        return DafnySequence(args.map { arg -> DafnySequence(arg.toList()) })
    }
}
