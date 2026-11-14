using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Vistudio
{
    internal static class KotlinEvaluation
    {
        // Kotlin server 263.7084 treats watch/variables as asynchronous presentation
        // subscriptions. VS expects a complete evaluate response, so use its repl path.
        // The backend caches by expression + frame even for repl. A unique trailing
        // comment makes each host request fresh without wrapping or executing it twice.
        internal static void Prepare(JObject request, long evaluationId)
        {
            if ((string?)request["command"] != "evaluate" || !(request["arguments"] is JObject args)) return;
            var context = (string?)args["context"];
            // VS Immediate omits context (observed on host 18.12); DAP permits this.
            if (context != null && context != "watch" && context != "repl" && context != "variables") return;
            if ((bool?)args["noSideEffects"] == true || (bool?)args["noFuncEval"] == true) return;
            if (!(args["expression"] is JValue expression) || expression.Type != JTokenType.String) return;
            args["context"] = "repl";
            // The newline also terminates a user's trailing line comment. Keep the
            // original expression verbatim, including multiline Kotlin and backticks.
            args["expression"] = (string?)expression + "\n/* vistudio-evaluation:" +
                evaluationId.ToString(CultureInfo.InvariantCulture) + " */";
        }
    }
}
