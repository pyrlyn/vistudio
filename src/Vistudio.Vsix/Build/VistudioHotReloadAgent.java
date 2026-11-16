import java.io.File;
import java.io.IOException;
import java.lang.instrument.ClassDefinition;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Kotlin/JVM hot reload for Vistudio: swaps recompiled classes into a running JVM. JDK-only; compiled and packed into a
 * jar by the user's Gradle build (Build/HotReload.gradle), like the Compose preview renderer.
 *
 * Two entry points in one class:
 *   main       - attach client run by the IDE: java -cp agent.jar VistudioHotReloadAgent PID AGENT_JAR REQUEST_FILE
 *   agentmain  - runs inside the program's JVM once the client has loaded the jar there.
 *
 * Request file (UTF-8, one line per class): class TAB binary.class.Name TAB absolute path of the .class file.
 * Result file (REQUEST_FILE + ".result"): reloaded TAB name | notloaded TAB name | failed TAB name TAB message |
 * error TAB message, then a final "done" line. A missing "done" means the agent did not finish.
 */
public final class VistudioHotReloadAgent {
    private VistudioHotReloadAgent() { }

    public static void main(String[] args) {
        if (args.length != 3) {
            System.out.println("VISTUDIO_HOTRELOAD_ERROR\tusage: PID AGENT_JAR REQUEST_FILE");
            System.exit(2);
        }
        try {
            // Reflection keeps this file compilable and loadable on a runtime without the jdk.attach module.
            Class<?> machine;
            try { machine = Class.forName("com.sun.tools.attach.VirtualMachine"); }
            catch (ClassNotFoundException e) {
                throw new IllegalStateException("this Java runtime has no jdk.attach module; hot reload needs a JDK, not a JRE");
            }
            Object vm = machine.getMethod("attach", String.class).invoke(null, args[0]);
            try { machine.getMethod("loadAgent", String.class, String.class).invoke(vm, args[1], args[2]); }
            finally { machine.getMethod("detach").invoke(vm); }
        } catch (Throwable error) {
            Throwable cause = error instanceof InvocationTargetException && error.getCause() != null ? error.getCause() : error;
            System.out.println("VISTUDIO_HOTRELOAD_ERROR\t" + oneLine(describe(cause)));
            System.exit(1);
        }
    }

    public static void agentmain(String request, Instrumentation instrumentation) {
        List<String> result = new ArrayList<String>();
        try { reload(new File(request), instrumentation, result); }
        catch (Throwable error) { result.add("error\t" + oneLine(describe(error))); }
        result.add("done");
        try { Files.write(new File(request + ".result").toPath(), result, StandardCharsets.UTF_8); }
        catch (IOException ignored) { /* The client reports the missing result file. */ }
    }

    private static void reload(File request, Instrumentation instrumentation, List<String> result) throws IOException {
        if (!instrumentation.isRedefineClassesSupported())
            throw new IllegalStateException("this JVM does not support class redefinition");
        // One class name can be loaded by several class loaders; every copy gets the new bytes.
        Map<String, List<Class<?>>> loaded = new HashMap<String, List<Class<?>>>();
        for (Class<?> type : instrumentation.getAllLoadedClasses()) {
            if (type.isArray() || !instrumentation.isModifiableClass(type)) continue;
            List<Class<?>> list = loaded.get(type.getName());
            if (list == null) loaded.put(type.getName(), list = new ArrayList<Class<?>>());
            list.add(type);
        }
        List<String> names = new ArrayList<String>();
        List<ClassDefinition[]> definitions = new ArrayList<ClassDefinition[]>();
        List<ClassDefinition> all = new ArrayList<ClassDefinition>();
        for (String line : Files.readAllLines(request.toPath(), StandardCharsets.UTF_8)) {
            String[] fields = line.split("\t");
            if (fields.length != 3 || !fields[0].equals("class")) continue;
            List<Class<?>> types = loaded.get(fields[1]);
            // A class the program has not used yet is read from the class path when it is first needed.
            if (types == null) { result.add("notloaded\t" + fields[1]); continue; }
            byte[] bytes;
            try { bytes = Files.readAllBytes(new File(fields[2]).toPath()); }
            catch (IOException e) { result.add("failed\t" + fields[1] + "\t" + oneLine("cannot read " + fields[2] + ": " + e.getMessage())); continue; }
            ClassDefinition[] group = new ClassDefinition[types.size()];
            for (int i = 0; i < group.length; i++) group[i] = new ClassDefinition(types.get(i), bytes);
            names.add(fields[1]);
            definitions.add(group);
            for (ClassDefinition definition : group) all.add(definition);
        }
        if (all.isEmpty()) return;
        // All classes in one step first, so classes that changed together never run half old, half new. When the JVM
        // rejects the set (it applies none of it), each class is tried alone to apply what can be applied.
        try {
            instrumentation.redefineClasses(all.toArray(new ClassDefinition[0]));
            for (String name : names) result.add("reloaded\t" + name);
            return;
        } catch (Throwable rejected) {
            if (names.size() == 1) { result.add("failed\t" + names.get(0) + "\t" + oneLine(describe(rejected))); return; }
        }
        for (int i = 0; i < names.size(); i++) {
            try {
                instrumentation.redefineClasses(definitions.get(i));
                result.add("reloaded\t" + names.get(i));
            } catch (Throwable error) {
                result.add("failed\t" + names.get(i) + "\t" + oneLine(describe(error)));
            }
        }
    }

    private static String describe(Throwable error) {
        String message = error.getMessage();
        String name = error.getClass().getSimpleName();
        return message == null || message.isEmpty() ? name : name + ": " + message;
    }

    private static String oneLine(String text) {
        return text.replace('\t', ' ').replace('\r', ' ').replace('\n', ' ');
    }
}
