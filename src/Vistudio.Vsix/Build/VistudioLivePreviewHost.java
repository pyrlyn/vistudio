import java.awt.image.BufferedImage;
import java.io.BufferedOutputStream;
import java.io.BufferedReader;
import java.io.DataOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.PrintStream;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileVisitResult;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.SimpleFileVisitor;
import java.nio.file.StandardCopyOption;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.Executor;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Runs one Compose @Preview function interactively for Vistudio's Kotlin Compose Preview window.
 *
 * The preview is composed in an offscreen ImageComposeScene. Rendered frames go to standard output, pointer and key
 * input comes from standard input, so the IDE shows and drives the preview without a window of this process. Like
 * VistudioPreviewRenderer this class depends only on the JDK and reaches Compose and Skia through reflection.
 *
 * Class path: this JVM starts with the libraries of the module's JVM target (everything from the Gradle cache). The
 * module's own classes and the outputs of other project modules are loaded by a child class loader from a private copy,
 * so a rebuild never finds its files locked and "load" swaps in the new classes without restarting the JVM.
 *
 * Commands (standard input, UTF-8 lines, tab separated):
 *   load     manifest path                     copy and load the entries of its "R" lines; shows the current preview again
 *   show     class, method, widthDp, heightDp, scale
 *   pointer  move|press|release|scroll|exit, x px, y px, buttons, modifiers, button, scrollX, scrollY
 *   key      press|release|typed, AWT key code, character, modifiers, AWT key location
 *   ack      frame number                      the IDE has taken the frame; the next one may be sent
 *   frame                                      send a frame even when nothing changed
 *   quit
 * buttons: 1 primary, 2 secondary, 4 tertiary. modifiers: 1 Shift, 2 Ctrl, 4 Alt, 8 Meta. button: 0-2, or -1.
 *
 * Messages (standard output): one type byte, a big-endian int length, the payload.
 *   'T'  UTF-8 text, tab separated: ready | loaded, generation | shown, class, method | error, where, message
 *   'F'  frame: int number, int width, int height, float scale, int format (0 = BGRA premultiplied), then the pixels
 * Everything the preview code prints goes to standard error.
 */
public final class VistudioLivePreviewHost {
    private static final int DEFAULT_WIDTH_DP = 720;
    private static final int DEFAULT_HEIGHT_DP = 1280;
    private static final long FRAME_NANOS = 16_000_000L;
    private static final long ACK_TIMEOUT_NANOS = 500_000_000L;
    private static final Object QUIT = new Object();

    private final LinkedBlockingQueue<Object> queue = new LinkedBlockingQueue<>();
    private final DataOutputStream out;
    private final Path work;
    private final ClassLoader parent = VistudioLivePreviewHost.class.getClassLoader();

    private URLClassLoader loader;
    private Path generationDirectory;
    private int generation;

    private String[] shown;          // the arguments of the last "show", kept for reloads
    private Object scene;
    private Scene api;
    private int canvasWidth, canvasHeight;
    private boolean wrapWidth, wrapHeight;
    private int contentWidth, contentHeight;
    private float scale = 1f;
    private boolean dirty;
    private int sentFrame, ackedFrame;
    private long lastSend, lastRender;

    private VistudioLivePreviewHost(OutputStream output, Path work) {
        this.out = new DataOutputStream(new BufferedOutputStream(output, 1 << 16));
        this.work = work;
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 1) throw new IllegalArgumentException("Usage: VistudioLivePreviewHost <work directory>");
        // The protocol owns standard output; whatever the preview prints must not end up inside a frame.
        PrintStream protocol = System.out;
        System.setOut(System.err);
        VistudioLivePreviewHost host = new VistudioLivePreviewHost(protocol, Paths.get(args[0]));
        Thread reader = new Thread(host::read, "vistudio-preview-input");
        reader.setDaemon(true);
        reader.start();
        int code = 0;
        try { host.run(); }
        catch (Throwable failure) { failure.printStackTrace(); code = 1; }
        host.closeGeneration();
        System.err.flush();
        // Compose and AWT can leave non-daemon threads behind.
        System.exit(code);
    }

    private void read() {
        try (BufferedReader input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = input.readLine()) != null) {
                // .NET Framework starts a redirected standard input with a UTF-8 byte order mark.
                if (!line.isEmpty() && line.charAt(0) == 0xFEFF) line = line.substring(1);
                if (!line.isEmpty()) queue.add(line);
            }
        } catch (IOException ignored) { }
        // The IDE closed the pipe or went away.
        queue.add(QUIT);
    }

    /** The scene thread: every Compose call, including the coroutines of the preview, runs here. */
    private void run() throws Exception {
        text("ready", System.getProperty("java.version"));
        while (true) {
            Object item = queue.poll(waitMillis(), TimeUnit.MILLISECONDS);
            while (item != null) {
                if (item == QUIT) return;
                if (item instanceof Runnable) {
                    try { ((Runnable) item).run(); }
                    catch (Throwable failure) { fail("effect", failure); }
                } else if (!command((String) item)) return;
                item = queue.poll();
            }
            if (scene == null) continue;
            long now = System.nanoTime();
            try {
                api.sendApplyNotifications();
                boolean wanted = dirty || api.hasInvalidations(scene);
                boolean free = ackedFrame == sentFrame || now - lastSend > ACK_TIMEOUT_NANOS;
                if (wanted && free && now - lastRender >= FRAME_NANOS - 1_000_000L) render(now);
            } catch (Throwable failure) { fail("render", failure); }
        }
    }

    private long waitMillis() {
        if (scene == null) return 1000;
        boolean wanted;
        try { wanted = dirty || api.hasInvalidations(scene); }
        catch (Throwable failure) { return 50; }
        if (!wanted) return 30;                        // state written on another thread is seen within this time
        if (ackedFrame != sentFrame) return 2;          // waiting for the IDE to take the previous frame
        long sinceRender = System.nanoTime() - lastRender;
        return Math.max(1, (FRAME_NANOS - sinceRender) / 1_000_000L);
    }

    private boolean command(String line) throws IOException {
        String[] field = line.split("\t", -1);
        try {
            switch (field[0]) {
                case "quit": return false;
                case "ack": ackedFrame = Math.max(ackedFrame, Integer.parseInt(field[1])); break;
                case "frame": dirty = true; break;
                case "load": load(Paths.get(field[1])); break;
                case "show": shown = field; show(); break;
                case "pointer": if (scene != null) { pointer(field); dirty = true; } break;
                case "key": if (scene != null) { key(field); dirty = true; } break;
                default: text("error", "protocol", "Unknown command " + field[0]);
            }
        } catch (Throwable failure) {
            String where = field[0].equals("pointer") || field[0].equals("key") ? "input" : field[0];
            fail(where, failure);
        }
        return true;
    }

    // ---- class loading ----

    private void load(Path manifest) throws Throwable {
        List<Path> entries = new ArrayList<>();
        for (String line : Files.readAllLines(manifest, StandardCharsets.UTF_8))
            if (line.startsWith("R\t")) entries.add(Paths.get(line.substring(2)));
        closeScene();
        closeGeneration();
        generation++;
        generationDirectory = work.resolve("classes-" + generation);
        Path jars = work.resolve("jars");
        Files.createDirectories(generationDirectory);
        Files.createDirectories(jars);
        List<URL> urls = new ArrayList<>();
        Set<Path> usedJars = new HashSet<>();
        int index = 0;
        for (Path entry : entries) {
            if (Files.isDirectory(entry)) {
                Path copy = generationDirectory.resolve("d" + index++);
                copyTree(entry, copy);
                urls.add(copy.toUri().toURL());
            } else if (Files.isRegularFile(entry)) {
                // Jars of other modules rarely change: one copy per content, shared by the generations that use it.
                String key = Integer.toHexString(entry.toAbsolutePath().toString().toLowerCase().hashCode()) + "-" + Files.size(entry)
                    + "-" + Files.getLastModifiedTime(entry).toMillis() + "-" + entry.getFileName();
                Path copy = jars.resolve(key);
                if (!Files.exists(copy)) Files.copy(entry, copy, StandardCopyOption.REPLACE_EXISTING);
                usedJars.add(copy);
                urls.add(copy.toUri().toURL());
            }
        }
        try (java.util.stream.Stream<Path> old = Files.list(jars)) {
            old.filter(path -> !usedJars.contains(path)).forEach(path -> { try { Files.deleteIfExists(path); } catch (IOException ignored) { } });
        }
        loader = new URLClassLoader(urls.toArray(new URL[0]), parent);
        // Compose resources and ServiceLoader lookups of the preview go through the context class loader.
        Thread.currentThread().setContextClassLoader(loader);
        api = null;
        text("loaded", Integer.toString(generation));
        if (shown != null) show();
    }

    private static void copyTree(Path from, Path to) throws IOException {
        Files.walkFileTree(from, new SimpleFileVisitor<Path>() {
            @Override public FileVisitResult preVisitDirectory(Path directory, BasicFileAttributes attributes) throws IOException {
                Files.createDirectories(to.resolve(from.relativize(directory).toString()));
                return FileVisitResult.CONTINUE;
            }
            @Override public FileVisitResult visitFile(Path file, BasicFileAttributes attributes) throws IOException {
                Files.copy(file, to.resolve(from.relativize(file).toString()), StandardCopyOption.REPLACE_EXISTING);
                return FileVisitResult.CONTINUE;
            }
        });
    }

    private void closeGeneration() {
        if (loader != null) {
            try { loader.close(); } catch (IOException ignored) { }
            loader = null;
        }
        if (generationDirectory != null) {
            try {
                Files.walkFileTree(generationDirectory, new SimpleFileVisitor<Path>() {
                    @Override public FileVisitResult visitFile(Path file, BasicFileAttributes attributes) throws IOException {
                        Files.deleteIfExists(file);
                        return FileVisitResult.CONTINUE;
                    }
                    @Override public FileVisitResult postVisitDirectory(Path directory, IOException failure) throws IOException {
                        Files.deleteIfExists(directory);
                        return FileVisitResult.CONTINUE;
                    }
                });
            } catch (IOException ignored) { }
            generationDirectory = null;
        }
    }

    // ---- scene ----

    private void show() throws Throwable {
        closeScene();
        if (loader == null) throw new IllegalStateException("No classes are loaded yet.");
        if (shown.length != 6) throw new IllegalArgumentException("Malformed show command.");
        int widthDp = Integer.parseInt(shown[3]), heightDp = Integer.parseInt(shown[4]);
        scale = Math.max(0.5f, Math.min(4f, Float.parseFloat(shown[5])));
        if (api == null) api = new Scene(loader, queue::add);
        Object content = VistudioPreviewRenderer.previewContent(loader, shown[1], shown[2]);
        wrapWidth = widthDp <= 0;
        wrapHeight = heightDp <= 0;
        canvasWidth = Math.round((wrapWidth ? DEFAULT_WIDTH_DP : widthDp) * scale);
        canvasHeight = Math.round((wrapHeight ? DEFAULT_HEIGHT_DP : heightDp) * scale);
        contentWidth = wrapWidth ? 0 : canvasWidth;
        contentHeight = wrapHeight ? 0 : canvasHeight;
        scene = VistudioPreviewRenderer.createScene(loader, canvasWidth, canvasHeight, scale, api.dispatcher, api.function2, content);
        dirty = true;
        lastRender = 0;
        // The first frame composes the preview, so a preview that throws is reported as the answer to "show".
        render(System.nanoTime());
        text("shown", shown[1], shown[2]);
    }

    private void closeScene() {
        if (scene == null) return;
        Object old = scene;
        scene = null;
        try { old.getClass().getMethod("close").invoke(old); } catch (Throwable ignored) { }
    }

    private void render(long now) throws Throwable {
        dirty = false;
        lastRender = now;
        Object image = api.render(scene, now);
        byte[] pixels = api.pixels(image, canvasWidth, canvasHeight);
        if (wrapWidth || wrapHeight) grow(pixels);
        int width = Math.max(1, contentWidth), height = Math.max(1, contentHeight);
        synchronized (out) {
            out.writeByte('F');
            out.writeInt(20 + width * height * 4);
            out.writeInt(++sentFrame);
            out.writeInt(width);
            out.writeInt(height);
            out.writeFloat(scale);
            out.writeInt(0);
            if (width == canvasWidth) out.write(pixels, 0, width * height * 4);
            else for (int y = 0; y < height; y++) out.write(pixels, y * canvasWidth * 4, width * 4);
            out.flush();
        }
        lastSend = System.nanoTime();
    }

    /**
     * Wrap-content dimensions: the frame covers everything drawn since the preview was shown. It only grows, so the
     * image in the IDE does not jump while a hover or press effect comes and goes.
     */
    private void grow(byte[] pixels) {
        int right = contentWidth, bottom = contentHeight;
        for (int y = 0; y < canvasHeight; y++) {
            int row = y * canvasWidth * 4 + 3;
            // Inside the known bounds only the part to their right can extend them.
            int from = y < contentHeight ? right : 0;
            for (int x = canvasWidth - 1; x >= from; x--) {
                if (pixels[row + x * 4] != 0) {
                    if (x + 1 > right) right = x + 1;
                    if (y + 1 > bottom) bottom = y + 1;
                    break;
                }
            }
        }
        if (wrapWidth) contentWidth = right;
        if (wrapHeight) contentHeight = bottom;
    }

    // ---- input ----

    private void pointer(String[] field) throws Throwable {
        float x = Float.parseFloat(field[2]), y = Float.parseFloat(field[3]);
        int buttons = Integer.parseInt(field[4]), modifiers = Integer.parseInt(field[5]), button = Integer.parseInt(field[6]);
        float scrollX = field.length > 8 ? Float.parseFloat(field[7]) : 0, scrollY = field.length > 8 ? Float.parseFloat(field[8]) : 0;
        api.pointer(scene, field[1], x, y, buttons, modifiers, button, scrollX, scrollY);
    }

    private void key(String[] field) throws Throwable {
        api.key(scene, field[1], Integer.parseInt(field[2]), (char) Integer.parseInt(field[3]), Integer.parseInt(field[4]),
            field.length > 5 ? Integer.parseInt(field[5]) : 0);
    }

    // ---- output ----

    private void text(String... fields) throws IOException {
        StringBuilder line = new StringBuilder();
        for (String value : fields) {
            if (line.length() != 0) line.append('\t');
            line.append(VistudioPreviewRenderer.escape(value == null ? "" : value));
        }
        byte[] payload = line.toString().getBytes(StandardCharsets.UTF_8);
        synchronized (out) {
            out.writeByte('T');
            out.writeInt(payload.length);
            out.write(payload);
            out.flush();
        }
    }

    /** A preview that threw is closed: its composition cannot be trusted after an exception. */
    private void fail(String where, Throwable failure) throws IOException {
        if (where.equals("render") || where.equals("input") || where.equals("effect") || where.equals("show")) closeScene();
        text("error", where, VistudioPreviewRenderer.describe(failure));
    }

    /** The reflective Compose and Skia calls of one class loader generation. */
    private static final class Scene {
        final Class<?> function2;
        final Object dispatcher;
        private final ClassLoader loader;
        private final Class<?> sceneType;
        private final Method render, hasInvalidations, sendPointer, sendKey, toComposeKey;
        private final Method applyNotifications;
        private final Object snapshotCompanion;
        private final Method buttonsOf, modifiersOf, boxButtons, boxModifiers, boxButton;
        private final int press, release, move, exit, scroll;
        private final java.awt.Component keySource = new javax.swing.JPanel();
        private Method bitmapFromImage, bitmapRead, bitmapClose;
        private Object bitmapCompanion;
        private boolean rawPixels = true;

        Scene(ClassLoader loader, Executor sceneThread) throws Throwable {
            this.loader = loader;
            function2 = Class.forName("kotlin.jvm.functions.Function2", false, loader);
            try { sceneType = Class.forName("androidx.compose.ui.ImageComposeScene", true, loader); }
            catch (ClassNotFoundException missing) {
                throw new IllegalStateException("Compose UI for desktop (ImageComposeScene) is not on this JVM target's runtime classpath. "
                    + "Add compose.desktop.currentOs to the JVM source set.");
            }
            Object context = null;
            try {
                // Effects and animations of the preview resume on the scene thread, like on the UI thread of an application.
                context = Class.forName("kotlinx.coroutines.ExecutorsKt", true, loader).getMethod("from", Executor.class).invoke(null, sceneThread);
            } catch (Throwable unavailable) { }
            dispatcher = context;
            render = sceneType.getMethod("render", long.class);
            hasInvalidations = sceneType.getMethod("hasInvalidations");

            Method notifications = null;
            Object companion = null;
            try {
                Class<?> snapshot = Class.forName("androidx.compose.runtime.snapshots.Snapshot", true, loader);
                companion = snapshot.getField("Companion").get(null);
                notifications = companion.getClass().getMethod("sendApplyNotifications");
            } catch (Throwable unavailable) { }
            applyNotifications = notifications;
            snapshotCompanion = companion;

            // sendPointerEvent(eventType, position, scrollDelta, timeMillis, type, buttons, keyboardModifiers, nativeEvent, button):
            // value classes mangle its name, so the Kotlin defaults bridge is found by shape.
            Method pointer = null;
            for (Method method : sceneType.getMethods()) {
                Class<?>[] types = method.getParameterTypes();
                if (method.getName().startsWith("sendPointerEvent") && method.getName().endsWith("$default") && Modifier.isStatic(method.getModifiers())
                    && types.length == 12 && types[1] == int.class && types[2] == long.class && types[3] == long.class && types[4] == long.class
                    && types[5] == int.class && types[10] == int.class) pointer = method;
            }
            sendPointer = pointer;
            Method buttons = null, modifiers = null;
            for (String holder : new String[] { "PointerEvent_skikoKt", "PointerEvent_desktopKt", "PointerEvent_jvmKt" }) {
                try {
                    Class<?> type = Class.forName("androidx.compose.ui.input.pointer." + holder, true, loader);
                    buttons = type.getMethod("PointerButtons", boolean.class, boolean.class, boolean.class, boolean.class, boolean.class);
                    modifiers = type.getMethod("PointerKeyboardModifiers", boolean.class, boolean.class, boolean.class, boolean.class, boolean.class,
                        boolean.class, boolean.class, boolean.class, boolean.class, boolean.class);
                    break;
                } catch (ClassNotFoundException | NoSuchMethodException next) { }
            }
            buttonsOf = buttons;
            modifiersOf = modifiers;
            boxButtons = box("androidx.compose.ui.input.pointer.PointerButtons");
            boxModifiers = box("androidx.compose.ui.input.pointer.PointerKeyboardModifiers");
            boxButton = box("androidx.compose.ui.input.pointer.PointerButton");
            press = eventType("Press", 1);
            release = eventType("Release", 2);
            move = eventType("Move", 3);
            exit = eventType("Exit", 5);
            scroll = eventType("Scroll", 6);

            Method key = null, compose = null;
            for (Method method : sceneType.getMethods())
                if (method.getName().startsWith("sendKeyEvent") && method.getParameterCount() == 1) key = method;
            for (String holder : new String[] { "KeyEvent_desktopKt", "KeyEvent_jvmKt" }) {
                try {
                    compose = Class.forName("androidx.compose.ui.input.key." + holder, true, loader).getMethod("toComposeEvent", java.awt.event.KeyEvent.class);
                    break;
                } catch (ClassNotFoundException | NoSuchMethodException next) { }
            }
            sendKey = key;
            toComposeKey = compose;
        }

        private Method box(String type) {
            try { return Class.forName(type, true, loader).getMethod("box-impl", int.class); }
            catch (Throwable unavailable) { return null; }
        }

        private int eventType(String name, int fallback) {
            try {
                Object companion = Class.forName("androidx.compose.ui.input.pointer.PointerEventType", true, loader).getField("Companion").get(null);
                for (Method method : companion.getClass().getMethods())
                    if ((method.getName().equals("get" + name) || method.getName().startsWith("get" + name + "-")) && method.getParameterCount() == 0)
                        return (Integer) method.invoke(companion);
            } catch (Throwable unavailable) { }
            return fallback;
        }

        void sendApplyNotifications() throws Throwable {
            if (applyNotifications != null) VistudioPreviewRenderer.invoke(applyNotifications, snapshotCompanion);
        }

        boolean hasInvalidations(Object scene) throws Throwable {
            return (Boolean) VistudioPreviewRenderer.invoke(hasInvalidations, scene);
        }

        Object render(Object scene, long nanoTime) throws Throwable {
            return VistudioPreviewRenderer.invoke(render, scene, nanoTime);
        }

        void pointer(Object scene, String kind, float x, float y, int buttons, int modifiers, int button, float scrollX, float scrollY) throws Throwable {
            if (sendPointer == null || buttonsOf == null || modifiersOf == null || boxButtons == null || boxModifiers == null)
                throw new IllegalStateException("This Compose version has no pointer input the preview host knows (ImageComposeScene.sendPointerEvent).");
            int type;
            switch (kind) {
                case "press": type = press; break;
                case "release": type = release; break;
                case "scroll": type = scroll; break;
                case "exit": type = exit; break;
                default: type = move;
            }
            Object pressed = boxButtons.invoke(null, buttonsOf.invoke(null, (buttons & 1) != 0, (buttons & 2) != 0, (buttons & 4) != 0, false, false));
            Object keys = boxModifiers.invoke(null, modifiersOf.invoke(null, (modifiers & 2) != 0, (modifiers & 8) != 0, (modifiers & 4) != 0,
                (modifiers & 1) != 0, false, false, false, false, false, false));
            Object changed = button >= 0 && boxButton != null ? boxButton.invoke(null, button) : null;
            // Defaults (mask bits): timeMillis (3), pointer type Mouse (4), nativeEvent (7).
            VistudioPreviewRenderer.invoke(sendPointer, null, scene, type, offset(x, y), offset(scrollX, scrollY), 0L, 0, pressed, keys, null, changed,
                (1 << 3) | (1 << 4) | (1 << 7), null);
        }

        /** androidx.compose.ui.geometry.Offset packs x and y as two floats in a long. */
        private static long offset(float x, float y) {
            return ((long) Float.floatToRawIntBits(x) << 32) | (Float.floatToRawIntBits(y) & 0xFFFFFFFFL);
        }

        void key(Object scene, String kind, int code, char character, int modifiers, int location) throws Throwable {
            if (sendKey == null || toComposeKey == null)
                throw new IllegalStateException("This Compose version has no key input the preview host knows (ImageComposeScene.sendKeyEvent).");
            int id = kind.equals("press") ? java.awt.event.KeyEvent.KEY_PRESSED : kind.equals("release") ? java.awt.event.KeyEvent.KEY_RELEASED
                : java.awt.event.KeyEvent.KEY_TYPED;
            int mask = ((modifiers & 1) != 0 ? java.awt.event.InputEvent.SHIFT_DOWN_MASK : 0) | ((modifiers & 2) != 0 ? java.awt.event.InputEvent.CTRL_DOWN_MASK : 0)
                | ((modifiers & 4) != 0 ? java.awt.event.InputEvent.ALT_DOWN_MASK : 0) | ((modifiers & 8) != 0 ? java.awt.event.InputEvent.META_DOWN_MASK : 0);
            boolean typed = id == java.awt.event.KeyEvent.KEY_TYPED;
            java.awt.event.KeyEvent event = new java.awt.event.KeyEvent(keySource, id, System.currentTimeMillis(), mask,
                typed ? java.awt.event.KeyEvent.VK_UNDEFINED : code, character == 0 ? java.awt.event.KeyEvent.CHAR_UNDEFINED : character,
                typed ? java.awt.event.KeyEvent.KEY_LOCATION_UNKNOWN : location == 0 ? java.awt.event.KeyEvent.KEY_LOCATION_STANDARD : location);
            VistudioPreviewRenderer.invoke(sendKey, scene, VistudioPreviewRenderer.invoke(toComposeKey, null, event));
        }

        /** The whole canvas as BGRA premultiplied rows. */
        byte[] pixels(Object image, int width, int height) throws Throwable {
            if (rawPixels) {
                try {
                    if (bitmapFromImage == null) {
                        Class<?> bitmapType = Class.forName("org.jetbrains.skia.Bitmap", true, loader);
                        bitmapCompanion = bitmapType.getField("Companion").get(null);
                        bitmapFromImage = bitmapCompanion.getClass().getMethod("makeFromImage", Class.forName("org.jetbrains.skia.Image", false, loader));
                        bitmapClose = bitmapType.getMethod("close");
                        for (Method method : bitmapType.getMethods()) {
                            Class<?>[] types = method.getParameterTypes();
                            // readPixels(dstInfo = imageInfo, dstRowBytes, srcX, srcY): the defaults bridge keeps all four.
                            if (method.getName().equals("readPixels$default") && types.length == 7 && method.getReturnType() == byte[].class) bitmapRead = method;
                        }
                        if (bitmapRead == null) throw new NoSuchMethodException("Bitmap.readPixels");
                    }
                    Object bitmap = VistudioPreviewRenderer.invoke(bitmapFromImage, bitmapCompanion, image);
                    try {
                        byte[] pixels = (byte[]) VistudioPreviewRenderer.invoke(bitmapRead, null, bitmap, null, 0, 0, 0, 15, null);
                        if (pixels != null && pixels.length == width * height * 4) {
                            if (isRgba(bitmap)) swapRedBlue(pixels);
                            return pixels;
                        }
                    } finally { bitmapClose.invoke(bitmap); }
                } catch (ReflectiveOperationException | LinkageError | ClassCastException unsupported) { }
                rawPixels = false;
            }
            // Slow path for a Skia version without the calls above: through PNG.
            BufferedImage decoded = VistudioPreviewRenderer.toBufferedImage(loader, image);
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < Math.min(height, decoded.getHeight()); y++)
                for (int x = 0; x < Math.min(width, decoded.getWidth()); x++) {
                    int argb = decoded.getRGB(x, y), alpha = argb >>> 24, at = (y * width + x) * 4;
                    pixels[at] = (byte) ((argb & 0xFF) * alpha / 255);
                    pixels[at + 1] = (byte) (((argb >> 8) & 0xFF) * alpha / 255);
                    pixels[at + 2] = (byte) (((argb >> 16) & 0xFF) * alpha / 255);
                    pixels[at + 3] = (byte) alpha;
                }
            return pixels;
        }

        private static boolean isRgba(Object bitmap) {
            try {
                Object info = bitmap.getClass().getMethod("getImageInfo").invoke(bitmap);
                return String.valueOf(info.getClass().getMethod("getColorType").invoke(info)).startsWith("RGBA");
            } catch (Throwable unknown) { return false; }
        }

        private static void swapRedBlue(byte[] pixels) {
            for (int i = 0; i + 3 < pixels.length; i += 4) { byte red = pixels[i]; pixels[i] = pixels[i + 2]; pixels[i + 2] = red; }
        }
    }
}
