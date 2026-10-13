package dev.crafttheft.melty;

// Melty's test mode (Java). While Melty tests a release it writes %LOCALAPPDATA%\Melty\test\craft-theft-halo.json; its "checks" names the file to append one line per check to, verdict first: "pass <name>" or "fail <name>: why". Report every name in recipe test.checks (and list only names you report). Never quit the game in a test run: Melty closes it when the run's minutes are up. Without the file (a player's game), nothing happens. Call MeltyTest.Start() from your ModInitializer's onInitialize(), then MeltyTest.Report(...) as each check happens.
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.regex.*;

public final class MeltyTest {
    private static Path checksFile;

    public static void start() {
        String local = System.getenv("LOCALAPPDATA");
        if (local == null) return;
        Path test = Paths.get(local, "Melty", "test", "craft-theft-halo.json");
        if (!Files.exists(test)) return; // not a test run
        try {
            String json = new String(Files.readAllBytes(test), StandardCharsets.UTF_8);
            Matcher match = Pattern.compile("\"checks\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").matcher(json);
            if (match.find()) checksFile = Paths.get(match.group(1).replace("\\\\", "\\"));
        } catch (Exception e) { return; }
        report("mod-loads", true, null);
    }

    public static void report(String name, boolean passed, String why) {
        if (checksFile == null) return;
        String line = (passed ? "pass " : "fail ") + name + (!passed && why != null ? ": " + why : "") + System.lineSeparator();
        try { Files.write(checksFile, line.getBytes(StandardCharsets.UTF_8), StandardOpenOption.CREATE, StandardOpenOption.APPEND); }
        catch (Exception e) { /* the run says the line never came */ }
    }
}
