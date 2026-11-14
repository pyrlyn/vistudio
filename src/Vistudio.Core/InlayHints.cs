using System.Collections.Generic;

namespace Vistudio.Core
{
    /// <summary>When Kotlin and Java editors show inlay hints (Visual Studio's InlayHintsEnableKind).</summary>
    public enum InlayHintsDisplay { Always, WhileHoldingAltF1, Never }

    /// <summary>Java parameter name hints (JDT LS java.inlayHints.parameterNames.enabled).</summary>
    public enum JavaParameterNameHints { All, Literals, None }

    /// <summary>Maps Vistudio settings to the inlay hint options of the Kotlin and Java language servers.</summary>
    public static class InlayHints
    {
        /// <summary>
        /// Kotlin LSP 271 option ids (IntelliJ InlayInfoOption names and provider ids) with their state. The server
        /// asks for them through workspace/configuration section <see cref="KotlinSection"/> before each request.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, bool>> KotlinOptions(KotlinSettings settings) => new[]
        {
            Option("hints.type.property", settings.InlayHintsPropertyTypes),
            Option("hints.type.variable", settings.InlayHintsLocalVariableTypes),
            Option("hints.type.function.return", settings.InlayHintsFunctionReturnTypes),
            Option("hints.type.function.parameter", settings.InlayHintsParameterTypes),
            Option("hints.lambda.return", settings.InlayHintsLambdaReturns),
            Option("hints.lambda.receivers.parameters", settings.InlayHintsImplicitReceivers),
            Option("hints.value.ranges", settings.InlayHintsRanges),
            Option("hints.value.kotlin.time", settings.InlayHintsTimeDurations),
            Option("hints.parameters", settings.InlayHintsParameterNames),
            Option("hints.parameters.context", settings.InlayHintsParameterNames && settings.InlayHintsContextParameters),
            Option("hints.parameters.compiled", settings.InlayHintsParameterNames && settings.InlayHintsCompiledParameterNames),
            Option("hints.parameters.excluded", settings.InlayHintsParameterNames && settings.InlayHintsExcludedParameterNames),
            Option("hints.call.chains", settings.InlayHintsCallChains),
        };

        public const string KotlinSection = "jetbrains.kotlin";

        /// <summary>JDT LS java.inlayHints values: parameter names mode and on/off switches.</summary>
        public static string JavaParameterNames(KotlinSettings settings) => settings.JavaInlayHintsParameterNames switch
        {
            JavaParameterNameHints.Literals => "literals",
            JavaParameterNameHints.None => "none",
            _ => "all",
        };

        /// <summary>Visual Studio InlayHintsEnableKind value: Always 0, Never 1, WhilePressingHotKey 2.</summary>
        public static int EnableKind(InlayHintsDisplay display) => display switch
        {
            InlayHintsDisplay.Never => 1,
            InlayHintsDisplay.WhileHoldingAltF1 => 2,
            _ => 0,
        };

        private static KeyValuePair<string, bool> Option(string id, bool enabled) => new KeyValuePair<string, bool>(id, enabled);
    }
}
