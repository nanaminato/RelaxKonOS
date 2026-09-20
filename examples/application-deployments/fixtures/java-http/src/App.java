import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.time.OffsetDateTime;
import java.util.concurrent.Executors;

/**
 * RelaxKonOS application-deployment fixture: an executable JAR that serves HTTP.
 *
 * <p>The JavaJar template requires exactly one JAR in the archive whose manifest carries a non-empty
 * {@code Main-Class}. It copies that JAR to {@code /app/app.jar} and starts it as
 * {@code java -XX:MaxRAMPercentage=75 -jar /app/app.jar}. Arguments supplied with the deployment
 * become the container command, which is why {@code --port=} is read from {@code args} here.
 *
 * <p>Only {@code /} and {@code /healthz} answer 200; any other path answers 404, so the readiness
 * path configured in the deployment definition is actually exercised instead of always succeeding.
 */
public final class App {

    private static final int DEFAULT_PORT = 8080;
    private static final String HEALTH_PATH = "/healthz";

    private App() {
    }

    public static void main(String[] args) throws IOException {
        int port = resolvePort(args);

        HttpServer server = HttpServer.create(new InetSocketAddress("0.0.0.0", port), 0);
        server.createContext(HEALTH_PATH, exchange -> respond(exchange, 200, "ok"));
        server.createContext("/", App::index);
        server.setExecutor(Executors.newFixedThreadPool(4));
        server.start();

        Runtime.getRuntime().addShutdownHook(new Thread(() -> {
            System.out.println("[java-http] stopping at " + OffsetDateTime.now());
            server.stop(0);
        }));

        System.out.println("[java-http] listening on 0.0.0.0:" + port);
        System.out.println("[java-http] health path " + HEALTH_PATH);
        System.out.println("[java-http] java.version " + System.getProperty("java.version"));
        System.out.println("[java-http] started at " + OffsetDateTime.now());
    }

    /** Resolves {@code --port=N} first, then {@code PORT}, then 8080. */
    private static int resolvePort(String[] args) {
        for (String argument : args) {
            if (argument.startsWith("--port=")) {
                return requirePort(argument.substring("--port=".length()).trim(), argument);
            }
        }
        String fromEnvironment = System.getenv("PORT");
        if (fromEnvironment != null && !fromEnvironment.isBlank()) {
            return requirePort(fromEnvironment.trim(), "PORT=" + fromEnvironment);
        }
        return DEFAULT_PORT;
    }

    private static int requirePort(String value, String origin) {
        try {
            int port = Integer.parseInt(value);
            if (port >= 1 && port <= 65535) {
                return port;
            }
        } catch (NumberFormatException ignored) {
            // Falls through to the shared failure below so both causes report the same way.
        }
        throw new IllegalArgumentException("not a usable TCP port: " + origin);
    }

    private static void index(HttpExchange exchange) throws IOException {
        String path = exchange.getRequestURI().getPath();
        if (!"/".equals(path)) {
            respond(exchange, 404, "not found: " + path + "\n");
            return;
        }
        String body = "{\"app\":\"relaxkonos-ad-java-http\""
            + ",\"java\":\"" + System.getProperty("java.version") + "\""
            + ",\"host\":\"" + System.getenv().getOrDefault("HOSTNAME", "unknown") + "\""
            + ",\"time\":\"" + OffsetDateTime.now() + "\"}";
        respond(exchange, 200, body);
    }

    private static void respond(HttpExchange exchange, int status, String body) throws IOException {
        byte[] payload = body.getBytes(StandardCharsets.UTF_8);
        exchange.getResponseHeaders().set("Content-Type", "text/plain; charset=utf-8");
        exchange.sendResponseHeaders(status, payload.length);
        try (OutputStream stream = exchange.getResponseBody()) {
            stream.write(payload);
        }
    }
}
