using System;
using System.Collections.Generic;
using System.Linq;

namespace Vistudio.Core.Bazel
{
    /// <summary>
    /// What Vistudio knows about Bazel rules and functions without asking Bazel: the native and common ruleset rules of
    /// the languages this extension supports, their usual attributes, and which attributes hold labels or files.
    /// Unknown rules (macros, other rulesets) get the attributes every rule has.
    /// </summary>
    public static class BazelRules
    {
        public sealed class Function
        {
            internal Function(string name, string description, string[] attributes, BazelFileKind[] files)
            {
                Name = name; Description = description; Attributes = attributes; Files = files;
            }

            public string Name { get; }
            public string Description { get; }
            public IReadOnlyList<string> Attributes { get; }
            /// <summary>The file kinds this function can be called in; empty means any.</summary>
            public IReadOnlyList<BazelFileKind> Files { get; }
        }

        /// <summary>Attributes every rule accepts (Bazel's common definitions).</summary>
        public static readonly string[] CommonAttributes =
        {
            "name", "visibility", "tags", "testonly", "deprecation", "features", "target_compatible_with", "compatible_with",
            "exec_compatible_with", "restricted_to", "toolchains", "applicable_licenses", "package_metadata",
        };

        private static readonly string[] TestAttributes = { "args", "env", "env_inherit", "size", "timeout", "flaky", "shard_count", "local" };
        private static readonly string[] BinaryAttributes = { "args", "env", "output_licenses" };
        private static readonly string[] Cc = { "srcs", "hdrs", "deps", "data", "copts", "conlyopts", "cxxopts", "defines", "local_defines", "includes", "linkopts", "linkstatic", "textual_hdrs", "strip_include_prefix", "include_prefix", "implementation_deps", "additional_linker_inputs", "malloc", "nocopts", "win_def_file", "alwayslink" };
        private static readonly string[] Java = { "srcs", "deps", "runtime_deps", "exports", "resources", "data", "javacopts", "plugins", "exported_plugins", "neverlink", "resource_strip_prefix" };
        private static readonly string[] JavaBinary = { "main_class", "jvm_flags", "launcher", "create_executable", "use_testrunner", "deploy_manifest_lines", "stamp", "classpath_resources" };
        private static readonly string[] Kotlin = { "srcs", "deps", "runtime_deps", "exports", "resources", "data", "plugins", "associates", "kotlinc_opts", "javac_opts", "module_name", "neverlink" };
        private static readonly string[] Rust = { "srcs", "deps", "crate_name", "crate_root", "edition", "crate_features", "data", "compile_data", "proc_macro_deps", "rustc_flags", "rustc_env", "aliases", "version", "build_script" };
        private static readonly string[] Zig = { "main", "srcs", "deps", "copts", "linkopts", "cdeps", "csrcs", "extra_srcs", "zigopts" };
        private static readonly string[] Dart = { "srcs", "deps", "main", "data", "pub_pkg_name", "enable_ddc" };

        private static readonly Dictionary<string, Function> functions = new Dictionary<string, Function>(StringComparer.Ordinal);

        static BazelRules()
        {
            var build = new[] { BazelFileKind.Build };
            var buildOrBzl = new[] { BazelFileKind.Build, BazelFileKind.Extension };
            var module = new[] { BazelFileKind.Module };
            var any = Array.Empty<BazelFileKind>();
            void Add(string name, string description, BazelFileKind[] files, params string[][] groups) =>
                functions[name] = new Function(name, description, groups.SelectMany(g => g).Distinct().ToArray(), files);
            string[] None() => Array.Empty<string>();

            // C and C++ (native, rules_cc).
            Add("cc_library", "C/C++ library (rules_cc).", buildOrBzl, Cc);
            Add("cc_binary", "C/C++ executable (rules_cc).", buildOrBzl, Cc, BinaryAttributes, new[] { "linkshared", "stamp" });
            Add("cc_test", "C/C++ test (rules_cc).", buildOrBzl, Cc, TestAttributes, new[] { "stamp" });
            Add("cc_import", "Prebuilt C/C++ library (rules_cc).", buildOrBzl, new[] { "hdrs", "static_library", "shared_library", "interface_library", "system_provided", "alwayslink", "deps", "includes" });
            Add("cc_shared_library", "Shared library from cc_library targets (rules_cc).", buildOrBzl, new[] { "deps", "dynamic_deps", "additional_linker_inputs", "user_link_flags", "shared_lib_name", "win_def_file", "exports_filter" });
            // Java and Kotlin.
            Add("java_library", "Java library (rules_java).", buildOrBzl, Java);
            Add("java_binary", "Java program (rules_java).", buildOrBzl, Java, JavaBinary, BinaryAttributes);
            Add("java_test", "Java test (rules_java).", buildOrBzl, Java, JavaBinary, TestAttributes, new[] { "test_class" });
            Add("java_import", "Prebuilt jars (rules_java).", buildOrBzl, new[] { "jars", "srcjar", "deps", "runtime_deps", "exports", "neverlink", "data" });
            Add("java_plugin", "Java annotation processor (rules_java).", buildOrBzl, Java, new[] { "processor_class", "generates_api" });
            Add("kt_jvm_library", "Kotlin/JVM library (rules_kotlin).", buildOrBzl, Kotlin);
            Add("kt_jvm_binary", "Kotlin/JVM program (rules_kotlin).", buildOrBzl, Kotlin, new[] { "main_class", "jvm_flags" }, BinaryAttributes);
            Add("kt_jvm_test", "Kotlin/JVM test (rules_kotlin).", buildOrBzl, Kotlin, new[] { "test_class", "main_class", "jvm_flags" }, TestAttributes);
            Add("kt_jvm_import", "Prebuilt Kotlin jars (rules_kotlin).", buildOrBzl, new[] { "jars", "srcjar", "deps", "runtime_deps", "exports", "neverlink" });
            Add("kt_compiler_plugin", "Kotlin compiler plugin (rules_kotlin).", buildOrBzl, new[] { "id", "deps", "options", "target_embedded_compiler", "compile_phase", "stubs_phase" });
            // Rust.
            Add("rust_library", "Rust library crate (rules_rust).", buildOrBzl, Rust);
            Add("rust_binary", "Rust executable crate (rules_rust).", buildOrBzl, Rust, BinaryAttributes);
            Add("rust_test", "Rust test (rules_rust).", buildOrBzl, Rust, TestAttributes, new[] { "crate", "use_libtest_harness" });
            Add("rust_proc_macro", "Rust procedural macro crate (rules_rust).", buildOrBzl, Rust);
            Add("rust_shared_library", "Rust cdylib (rules_rust).", buildOrBzl, Rust);
            Add("rust_static_library", "Rust staticlib (rules_rust).", buildOrBzl, Rust);
            Add("cargo_build_script", "Runs a Cargo build.rs (rules_rust).", buildOrBzl, new[] { "srcs", "deps", "crate_root", "build_script_env", "data", "tools", "rustc_flags", "crate_features", "edition" });
            // Zig, Dart, WebAssembly.
            Add("zig_binary", "Zig executable (rules_zig).", buildOrBzl, Zig, BinaryAttributes);
            Add("zig_library", "Zig static library (rules_zig).", buildOrBzl, Zig);
            Add("zig_test", "Zig test (rules_zig).", buildOrBzl, Zig, TestAttributes);
            Add("zig_module", "Zig module for other Zig targets (rules_zig).", buildOrBzl, Zig);
            Add("dart_library", "Dart library (rules_dart).", buildOrBzl, Dart);
            Add("dart_binary", "Dart program (rules_dart).", buildOrBzl, Dart, BinaryAttributes);
            Add("dart_test", "Dart test (rules_dart).", buildOrBzl, Dart, TestAttributes);
            Add("wasm_cc_binary", "C/C++ target built for WebAssembly (emsdk).", buildOrBzl, new[] { "cc_target", "outputs", "threads", "simd", "backend", "standalone" });
            // Language-independent native rules.
            Add("filegroup", "A named group of files.", buildOrBzl, new[] { "srcs", "data", "output_group" });
            Add("genrule", "Runs a command that writes outs from srcs.", buildOrBzl, new[] { "srcs", "outs", "cmd", "cmd_bat", "cmd_bash", "cmd_ps", "tools", "toolchains", "executable", "local", "message", "output_to_bindir", "stamp" });
            Add("sh_binary", "Shell script program (rules_shell).", buildOrBzl, new[] { "srcs", "deps", "data" }, BinaryAttributes);
            Add("sh_test", "Shell script test (rules_shell).", buildOrBzl, new[] { "srcs", "deps", "data" }, TestAttributes);
            Add("sh_library", "Shell scripts and their data (rules_shell).", buildOrBzl, new[] { "srcs", "deps", "data" });
            Add("py_binary", "Python program (rules_python).", buildOrBzl, new[] { "srcs", "deps", "data", "main", "imports", "python_version" }, BinaryAttributes);
            Add("py_library", "Python library (rules_python).", buildOrBzl, new[] { "srcs", "deps", "data", "imports" });
            Add("py_test", "Python test (rules_python).", buildOrBzl, new[] { "srcs", "deps", "data", "main", "imports", "python_version" }, TestAttributes);
            Add("alias", "Another name for a target.", buildOrBzl, new[] { "actual" });
            Add("test_suite", "A set of tests.", buildOrBzl, new[] { "tests" });
            Add("config_setting", "Condition for select().", buildOrBzl, new[] { "values", "define_values", "flag_values", "constraint_values" });
            Add("platform", "A target platform.", buildOrBzl, new[] { "constraint_values", "parents", "exec_properties", "flags" });
            Add("constraint_setting", "A constraint dimension.", buildOrBzl, new[] { "default_constraint_value" });
            Add("constraint_value", "One value of a constraint_setting.", buildOrBzl, new[] { "constraint_setting" });
            Add("toolchain", "Registers a toolchain implementation.", buildOrBzl, new[] { "toolchain", "toolchain_type", "exec_compatible_with", "target_compatible_with", "target_settings" });
            Add("toolchain_type", "A toolchain type.", buildOrBzl, None());
            Add("exports_files", "Makes source files visible to other packages: exports_files([\"a.txt\"], visibility = ...).", build, new[] { "visibility", "licenses" });
            Add("package", "Defaults for every rule in this package.", build, new[] { "default_visibility", "default_testonly", "default_deprecation", "features", "default_applicable_licenses", "default_package_metadata" });
            Add("package_group", "A set of packages for visibility.", build, new[] { "packages", "includes" });
            Add("licenses", "Licenses of this package.", build, None());
            // MODULE.bazel.
            Add("module", "This module's name and version.", module, new[] { "name", "version", "compatibility_level", "repo_name", "bazel_compatibility" });
            Add("bazel_dep", "Depends on a module from the registry.", module, new[] { "name", "version", "max_compatibility_level", "repo_name", "dev_dependency" });
            Add("use_extension", "Uses a module extension: use_extension(\"//:ext.bzl\", \"ext\").", module, new[] { "dev_dependency", "isolate" });
            Add("use_repo", "Imports repositories that a module extension defines.", module, None());
            Add("use_repo_rule", "Uses a repository rule directly: use_repo_rule(\"@bazel_tools//tools/build_defs/repo:http.bzl\", \"http_archive\").", module, None());
            Add("register_toolchains", "Registers toolchains by label.", module, new[] { "dev_dependency" });
            Add("register_execution_platforms", "Registers execution platforms by label.", module, new[] { "dev_dependency" });
            Add("single_version_override", "Pins a dependency's version, registry or patches.", module, new[] { "module_name", "version", "registry", "patches", "patch_cmds", "patch_strip" });
            Add("multiple_version_override", "Allows several versions of a module.", module, new[] { "module_name", "versions", "registry" });
            Add("archive_override", "Takes a module from an archive.", module, new[] { "module_name", "urls", "integrity", "strip_prefix", "patches", "patch_cmds", "patch_strip" });
            Add("git_override", "Takes a module from a Git commit.", module, new[] { "module_name", "remote", "commit", "tag", "branch", "patches", "patch_cmds", "patch_strip", "init_submodules", "strip_prefix" });
            Add("local_path_override", "Takes a module from a local folder.", module, new[] { "module_name", "path" });
            Add("include", "Includes another .MODULE.bazel segment.", module, None());
            // WORKSPACE (legacy).
            Add("workspace", "Legacy workspace name.", new[] { BazelFileKind.Workspace }, new[] { "name" });
            Add("http_archive", "Downloads and extracts an archive (load it from @bazel_tools//tools/build_defs/repo:http.bzl).", any, new[] { "name", "urls", "url", "sha256", "integrity", "strip_prefix", "build_file", "build_file_content", "patches", "patch_args", "patch_cmds", "type", "add_prefix", "workspace_file" });
            Add("local_repository", "A repository from a local folder.", new[] { BazelFileKind.Workspace }, new[] { "name", "path", "repo_mapping" });
            // Functions usable in BUILD and .bzl files.
            Add("load", "Loads symbols from a .bzl file: load(\"//pkg:defs.bzl\", \"symbol\", alias = \"symbol\").", any, None());
            Add("glob", "Source files matching patterns: glob([\"src/**/*.cc\"], exclude = [...]).", buildOrBzl, new[] { "include", "exclude", "exclude_directories", "allow_empty" });
            Add("select", "Chooses a value by configuration: select({\":cond\": [...], \"//conditions:default\": [...]}).", buildOrBzl, new[] { "no_match_error" });
            Add("subpackages", "Direct subpackages of this package.", build, new[] { "include", "exclude", "allow_empty" });
            Add("package_name", "Name of the package being evaluated.", buildOrBzl, None());
            Add("repository_name", "Name of the repository being evaluated.", buildOrBzl, None());
            Add("Label", "A label value: Label(\"//pkg:target\").", any, None());
            // .bzl definitions.
            var bzl = new[] { BazelFileKind.Extension };
            Add("rule", "Defines a rule.", bzl, new[] { "implementation", "attrs", "outputs", "executable", "test", "toolchains", "fragments", "provides", "doc", "cfg", "exec_groups", "initializer", "parent", "extendable", "subrules" });
            Add("aspect", "Defines an aspect.", bzl, new[] { "implementation", "attr_aspects", "attrs", "required_providers", "provides", "toolchains", "fragments", "doc", "apply_to_generating_rules" });
            Add("provider", "Defines a provider.", bzl, new[] { "doc", "fields", "init" });
            Add("macro", "Defines a symbolic macro.", bzl, new[] { "implementation", "attrs", "inherit_attrs", "finalizer", "doc" });
            Add("module_extension", "Defines a module extension.", bzl, new[] { "implementation", "tag_classes", "doc", "environ", "os_dependent", "arch_dependent" });
            Add("repository_rule", "Defines a repository rule.", bzl, new[] { "implementation", "attrs", "local", "environ", "configure", "remotable", "doc" });
            Add("tag_class", "A tag class of a module extension.", bzl, new[] { "attrs", "doc" });
            Add("transition", "Defines a configuration transition.", bzl, new[] { "implementation", "inputs", "outputs" });
            Add("depset", "A nested set for efficient accumulation.", bzl, new[] { "direct", "order", "transitive" });
            Add("struct", "An immutable struct.", bzl, None());
            Add("fail", "Stops evaluation with an error.", any, new[] { "msg", "attr" });
            Add("print", "Prints a debug message.", any, new[] { "sep" });
            foreach (var builtin in new[] { "len", "str", "int", "bool", "list", "dict", "tuple", "range", "enumerate", "zip", "sorted", "reversed", "min", "max", "any", "all", "hasattr", "getattr", "type", "dir", "repr", "hash", "abs", "float", "isinstance" })
                functions[builtin] = new Function(builtin, "Starlark built-in function.", Array.Empty<string>(), Array.Empty<BazelFileKind>());
        }

        public static IReadOnlyCollection<Function> All => functions.Values;

        public static Function? Find(string name) => functions.TryGetValue(name, out var function) ? function : null;

        /// <summary>The functions offered at the start of a statement in a file of <paramref name="kind"/>.</summary>
        public static IEnumerable<Function> For(BazelFileKind kind) =>
            functions.Values.Where(f => f.Files.Count == 0 || f.Files.Contains(kind) || kind == BazelFileKind.Other);

        /// <summary>Attributes of a rule, macro or function call; unknown names get the common rule attributes.</summary>
        public static IReadOnlyList<string> AttributesOf(string function)
        {
            var known = Find(function);
            if (known == null) return CommonAttributes.Concat(new[] { "srcs", "deps", "data" }).ToList();
            return IsRule(known) ? CommonAttributes.Concat(known.Attributes).Distinct().ToList() : known.Attributes;
        }

        private static readonly HashSet<string> NotRules = new HashSet<string>(StringComparer.Ordinal)
        {
            "load", "glob", "select", "exports_files", "package", "package_group", "licenses", "subpackages", "package_name", "repository_name",
        };

        /// <summary>A rule (or ruleset macro) that declares a target with <c>name</c> and takes the common attributes.</summary>
        public static bool IsRule(Function function) => function.Files.Contains(BazelFileKind.Build) && !NotRules.Contains(function.Name);

        /// <summary>Attributes whose strings are labels of other targets (validated as labels).</summary>
        public static readonly HashSet<string> LabelAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "deps", "runtime_deps", "exports", "implementation_deps", "dynamic_deps", "plugins", "exported_plugins", "proc_macro_deps",
            "tools", "actual", "tests", "associates", "cdeps", "toolchain", "constraint_setting", "constraint_values", "parents",
            "launcher", "malloc", "crate", "build_script", "cc_target",
        };

        /// <summary>Attributes whose strings are source files or labels of file-producing targets.</summary>
        public static readonly HashSet<string> FileAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "srcs", "hdrs", "textual_hdrs", "data", "resources", "compile_data", "additional_linker_inputs", "win_def_file", "main",
            "crate_root", "src", "jars", "srcjar", "static_library", "shared_library", "interface_library", "csrcs", "extra_srcs",
            "build_file", "classpath_resources",
        };

        /// <summary>Calls whose positional strings are file names in the current package.</summary>
        public static readonly HashSet<string> FileFunctions = new HashSet<string>(StringComparer.Ordinal) { "exports_files" };
    }
}
