import java.awt.Color;
import java.awt.Graphics2D;
import java.awt.image.BufferedImage;
import java.io.ByteArrayInputStream;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Proxy;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.List;
import javax.imageio.ImageIO;

/**
 * Renders Compose @Preview functions of a JVM (desktop) compilation to PNG files.
 *
 * Runs in its own JVM on the module's runtime classpath, started by ComposePreview.gradle. It depends only on the JDK:
 * Compose (ImageComposeScene, Skia) is reached through reflection so the extension ships no Compose version.
 *
 * Request lines (UTF-8, tab-separated): id, JVM class, method, widthDp, heightDp, showBackground, backgroundArgbHex.
 * A size of 0 means "wrap content" up to the default canvas. The manifest results.tsv receives one line per request:
 * id, OK, file name, pixel width, pixel height, scale — or id, ERROR, message (tabs and line breaks escaped).
 */
public final class VistudioPreviewRenderer {
    private static final int DEFAULT_WIDTH_DP = 720;
    private static final int DEFAULT_HEIGHT_DP = 1280;
    private static final float SCALE = 2f;

    public static void main(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("Usage: VistudioPreviewRenderer <request> <output directory>");
        Path output = Paths.get(args[1]);
        Files.createDirectories(output);
        StringBuilder manifest = new StringBuilder();
        for (String line : Files.readAllLines(Paths.get(args[0]), StandardCharsets.UTF_8)) {
            if (line.trim().isEmpty()) continue;
            String[] field = line.split("\t", -1);
            String id = field[0];
            String result;
            try {
                if (field.length != 7) throw new IllegalArgumentException("Malformed preview request.");
                BufferedImage image = render(field[1], field[2], Integer.parseInt(field[3]), Integer.parseInt(field[4]),
                    Boolean.parseBoolean(field[5]), Long.parseUnsignedLong(field[6], 16));
                String file = id + ".png";
                ImageIO.write(image, "png", output.resolve(file).toFile());
                result = id + "\tOK\t" + file + "\t" + image.getWidth() + "\t" + image.getHeight() + "\t" + SCALE;
            } catch (Throwable failure) {
                result = id + "\tERROR\t" + escape(describe(failure));
            }
            manifest.append(result).append('\n');
            System.out.println("VISTUDIO_PREVIEW\t" + result);
        }
        Files.write(output.resolve("results.tsv"), manifest.toString().getBytes(StandardCharsets.UTF_8));
        // Compose and AWT can leave non-daemon threads behind; the manifest is complete, so end the JVM explicitly.
        System.out.flush();
        System.exit(0);
    }

    private static BufferedImage render(String className, String methodName, int widthDp, int heightDp,
                                        boolean showBackground, long background) throws Throwable {
        ClassLoader loader = Thread.currentThread().getContextClassLoader();
        Class<?> function2 = Class.forName("kotlin.jvm.functions.Function2", false, loader);
        Object content = previewContent(loader, className, methodName);

        int width = Math.round((widthDp > 0 ? widthDp : DEFAULT_WIDTH_DP) * SCALE);
        int height = Math.round((heightDp > 0 ? heightDp : DEFAULT_HEIGHT_DP) * SCALE);
        Object scene = createScene(loader, width, height, function2, content);
        BufferedImage image;
        try {
            Method render = scene.getClass().getMethod("render", long.class);
            invoke(render, scene, 0L);
            // The second frame applies state written by the first composition (for example layout-driven effects).
            Object skiaImage = invoke(render, scene, 0L);
            image = toBufferedImage(loader, skiaImage);
        } finally {
            try { scene.getClass().getMethod("close").invoke(scene); } catch (Throwable ignored) { }
        }
        image = crop(image, widthDp <= 0, heightDp <= 0);
        if (showBackground) image = fill(image, background == 0 ? 0xFFFFFFFFL : background);
        return image;
    }

    /**
     * The preview function as Compose content: a Function2&lt;Composer, Int, Unit&gt; that calls the static method the
     * Compose compiler generated for a parameterless top-level @Composable function. Also used by VistudioLivePreviewHost.
     */
    static Object previewContent(ClassLoader loader, String className, String methodName) throws Throwable {
        Class<?> composer;
        try {
            composer = Class.forName("androidx.compose.runtime.Composer", false, loader);
            Class.forName("androidx.compose.ui.ImageComposeScene", false, loader);
        } catch (ClassNotFoundException missing) {
            throw new IllegalStateException("Compose UI for desktop (ImageComposeScene) is not on this JVM target's runtime classpath. "
                + "Add compose.desktop.currentOs to the JVM source set.");
        }
        Class<?> owner;
        try { owner = Class.forName(className, true, loader); }
        catch (ClassNotFoundException missing) {
            throw new IllegalStateException("Class " + className + " is not in the JVM compilation. Is the file part of the JVM target's source sets?");
        }
        Method preview = null;
        for (Method method : owner.getDeclaredMethods()) {
            Class<?>[] parameters = method.getParameterTypes();
            if (method.getName().equals(methodName) && Modifier.isStatic(method.getModifiers()) && parameters.length == 2
                && parameters[0] == composer && parameters[1] == int.class) preview = method;
        }
        if (preview == null)
            throw new IllegalStateException(className + "." + methodName + " is not a parameterless top-level @Composable function. "
                + "Was the Compose compiler plugin applied?");
        preview.setAccessible(true);
        Method target = preview;
        Object unit = Class.forName("kotlin.Unit", true, loader).getField("INSTANCE").get(null);
        Class<?> function2 = Class.forName("kotlin.jvm.functions.Function2", false, loader);
        return Proxy.newProxyInstance(loader, new Class<?>[] { function2 }, (proxy, method, arguments) -> {
            switch (method.getName()) {
                case "invoke":
                    try { target.invoke(null, arguments[0], 0); }
                    catch (InvocationTargetException e) { throw e.getCause(); }
                    return unit;
                case "hashCode": return System.identityHashCode(proxy);
                case "equals": return proxy == arguments[0];
                default: return "VistudioPreview(" + methodName + ")";
            }
        });
    }

    private static Object createScene(ClassLoader loader, int width, int height, Class<?> function2, Object content) throws Throwable {
        return createScene(loader, width, height, SCALE, null, function2, content);
    }

    /**
     * Fills the constructor parameters of ImageComposeScene by type; this works across Compose versions with extra defaults.
     * A null context runs effects unconfined, which is enough for a single rendered image.
     */
    static Object createScene(ClassLoader loader, int width, int height, float scale, Object context, Class<?> function2, Object content) throws Throwable {
        Class<?> sceneType = Class.forName("androidx.compose.ui.ImageComposeScene", true, loader);
        Class<?> densityType = Class.forName("androidx.compose.ui.unit.Density", false, loader);
        Object density = Class.forName("androidx.compose.ui.unit.DensityKt", true, loader)
            .getMethod("Density", float.class, float.class).invoke(null, scale, 1f);
        if (context == null) {
            try { context = Class.forName("kotlinx.coroutines.Dispatchers", true, loader).getMethod("getUnconfined").invoke(null); }
            catch (Throwable unavailable) { context = Class.forName("kotlin.coroutines.EmptyCoroutineContext", true, loader).getField("INSTANCE").get(null); }
        }
        Class<?> contextType = Class.forName("kotlin.coroutines.CoroutineContext", false, loader);
        Constructor<?> best = null;
        Object[] bestArguments = null;
        for (Constructor<?> constructor : sceneType.getConstructors()) {
            Class<?>[] types = constructor.getParameterTypes();
            Object[] arguments = new Object[types.length];
            int ints = 0;
            boolean usable = true;
            boolean hasContent = false;
            for (int i = 0; i < types.length && usable; i++) {
                Class<?> type = types[i];
                if (type == int.class && ints < 2) arguments[i] = ints++ == 0 ? width : height;
                else if (type == densityType) arguments[i] = density;
                else if (type == contextType) arguments[i] = context;
                else if (type == function2) { arguments[i] = content; hasContent = true; }
                else if (type.isEnum() && type.getName().equals("androidx.compose.ui.unit.LayoutDirection"))
                    arguments[i] = enumValue(type, "Ltr");
                else usable = false;
            }
            if (usable && hasContent && ints == 2 && (best == null || types.length > best.getParameterCount())) {
                best = constructor;
                bestArguments = arguments;
            }
        }
        if (best == null) throw new IllegalStateException("Unsupported Compose version: no known ImageComposeScene constructor.");
        try { return best.newInstance(bestArguments); }
        catch (InvocationTargetException e) { throw e.getCause(); }
    }

    @SuppressWarnings({ "unchecked", "rawtypes" })
    private static Object enumValue(Class<?> type, String name) { return Enum.valueOf((Class) type, name); }

    static Object invoke(Method method, Object target, Object... arguments) throws Throwable {
        try { return method.invoke(target, arguments); }
        catch (InvocationTargetException e) { throw e.getCause(); }
    }

    static BufferedImage toBufferedImage(ClassLoader loader, Object skiaImage) throws Throwable {
        Class<?> formatType = Class.forName("org.jetbrains.skia.EncodedImageFormat", true, loader);
        Object pngFormat = enumValue(formatType, "PNG");
        Object data = null;
        boolean encoded = false;
        // Skiko changes the trailing encoder parameters between versions; prefer Kotlin's defaults bridge, which keeps
        // every parameter after the format at its default: (image, format, ints..., mask, marker).
        for (Method method : skiaImage.getClass().getMethods()) {
            Class<?>[] types = method.getParameterTypes();
            if (!encoded && method.getName().equals("encodeToData$default") && Modifier.isStatic(method.getModifiers())
                && types.length >= 4 && types[1] == formatType) {
                int defaults = types.length - 4;
                Object[] arguments = new Object[types.length];
                arguments[0] = skiaImage;
                arguments[1] = pngFormat;
                for (int i = 0; i < defaults; i++) arguments[2 + i] = 0;
                arguments[types.length - 2] = ((1 << (defaults + 1)) - 1) & ~1;
                data = invoke(method, null, arguments);
                encoded = true;
            }
        }
        for (Method method : skiaImage.getClass().getMethods()) {
            Class<?>[] types = method.getParameterTypes();
            if (!encoded && method.getName().equals("encodeToData") && types.length >= 1 && types[0] == formatType) {
                Object[] arguments = new Object[types.length];
                arguments[0] = pngFormat;
                for (int i = 1; i < types.length; i++) arguments[i] = i == 1 ? 100 : 0;
                data = invoke(method, skiaImage, arguments);
                encoded = true;
            }
        }
        if (!encoded) throw new IllegalStateException("Unsupported Skia version: Image.encodeToData is unavailable.");
        if (data == null) throw new IllegalStateException("Skia could not encode the rendered frame.");
        byte[] png = (byte[]) data.getClass().getMethod("getBytes").invoke(data);
        BufferedImage decoded = ImageIO.read(new ByteArrayInputStream(png));
        BufferedImage argb = new BufferedImage(decoded.getWidth(), decoded.getHeight(), BufferedImage.TYPE_INT_ARGB);
        Graphics2D graphics = argb.createGraphics();
        graphics.drawImage(decoded, 0, 0, null);
        graphics.dispose();
        return argb;
    }

    /** Wrap-content dimensions: content is laid out from the top-left corner, so trailing transparent space is removed. */
    private static BufferedImage crop(BufferedImage image, boolean cropWidth, boolean cropHeight) {
        if (!cropWidth && !cropHeight) return image;
        int right = 0, bottom = 0;
        for (int y = 0; y < image.getHeight(); y++)
            for (int x = 0; x < image.getWidth(); x++)
                if ((image.getRGB(x, y) >>> 24) != 0) { right = Math.max(right, x + 1); bottom = Math.max(bottom, y + 1); }
        int width = cropWidth ? Math.max(1, right) : image.getWidth();
        int height = cropHeight ? Math.max(1, bottom) : image.getHeight();
        return image.getSubimage(0, 0, width, height);
    }

    private static BufferedImage fill(BufferedImage image, long argb) {
        BufferedImage result = new BufferedImage(image.getWidth(), image.getHeight(), BufferedImage.TYPE_INT_ARGB);
        Graphics2D graphics = result.createGraphics();
        graphics.setColor(new Color((int) argb, true));
        graphics.fillRect(0, 0, image.getWidth(), image.getHeight());
        graphics.drawImage(image, 0, 0, null);
        graphics.dispose();
        return result;
    }

    static String describe(Throwable failure) {
        while ((failure instanceof InvocationTargetException || failure instanceof ExceptionInInitializerError) && failure.getCause() != null)
            failure = failure.getCause();
        StringBuilder text = new StringBuilder(failure.getClass().getName());
        if (failure.getMessage() != null) text.append(": ").append(failure.getMessage());
        List<String> frames = new ArrayList<>();
        for (StackTraceElement frame : failure.getStackTrace()) {
            String type = frame.getClassName();
            if (type.startsWith("java.") || type.startsWith("jdk.") || type.startsWith("sun.") || type.startsWith("kotlin.")
                || type.startsWith("androidx.") || type.startsWith("org.jetbrains.skia") || type.startsWith("com.sun.proxy")
                || type.startsWith("VistudioPreviewRenderer") || type.startsWith("VistudioLivePreviewHost") || type.startsWith("jdk.proxy")) continue;
            frames.add("at " + frame);
            if (frames.size() == 5) break;
        }
        for (String frame : frames) text.append('\n').append(frame);
        if (frames.isEmpty()) {
            StringWriter trace = new StringWriter();
            failure.printStackTrace(new PrintWriter(trace));
            String[] lines = trace.toString().split("\r?\n");
            for (int i = 1; i < Math.min(lines.length, 6); i++) text.append('\n').append(lines[i].trim());
        }
        return text.toString();
    }

    static String escape(String value) {
        return value.replace("\\", "\\\\").replace("\t", "\\t").replace("\r", "").replace("\n", "\\n");
    }
}
