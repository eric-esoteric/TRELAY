package com.trelay.android;

import java.io.ByteArrayInputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.net.URLConnection;
import java.net.URLStreamHandler;
import java.nio.charset.StandardCharsets;
import java.util.List;

public final class SubscriptionUpdatesTest {
    private static final String SOURCE = "https://subscription.example.com/config";
    private static final String OLD = "socks5://old.example.com:1080#Russia";
    private static final String NEW = "socks5://new.example.com:1080#Russia";
    private static final String OTHER = "socks5://other.example.com:1080#France";
    private static String response;
    private static String identity;
    private static boolean timeout;

    public static void main(String[] args) throws Exception {
        URL.setURLStreamHandlerFactory(scheme -> !"https".equals(scheme) ? null : new URLStreamHandler() {
            @Override protected URLConnection openConnection(URL url) {
                return new HttpURLConnection(url) {
                    @Override public void connect() { }
                    @Override public void disconnect() { }
                    @Override public boolean usingProxy() { return false; }
                    @Override public int getResponseCode() throws SocketTimeoutException {
                        identity = getRequestProperty("X-Hwid");
                        if (timeout) throw new SocketTimeoutException("synthetic timeout");
                        return 200;
                    }
                    @Override public InputStream getInputStream() {
                        return new ByteArrayInputStream(response.getBytes(StandardCharsets.UTF_8));
                    }
                };
            }
        });
        Model.Config config = new Model.Config();
        Model.Route route = new Model.Route();
        route.packages.add("com.tline.phone");
        config.routes.add(route);
        Model.Server selected = new Model.Server();
        selected.link = OLD;
        selected.subscription = SOURCE;
        route.servers.add(selected);
        Model.Server manual = new Model.Server();
        manual.link = OTHER;
        route.servers.add(manual);

        response = OLD.replace("#Russia", "#RU") + "\n" + OTHER;
        check(refresh(route) == 0, "label and provider order changes do not replace a route");
        response = OTHER + "\n" + NEW;
        check(refresh(route) == 1 && "test-device".equals(identity), "rotation with saved identity");
        check(route.servers.get(0) == selected && route.servers.get(2) == manual, "old server and manual reserve retained");
        Model.Server candidate = route.servers.get(1);
        check(candidate.link.equals(NEW) && candidate.subscriptionReplacement, "new configuration staged as reserve");
        check(refresh(route) == 0, "repeated response is idempotent");

        Model.Route restored = config.copy().routes.get(0);
        check(restored.servers.get(1).id.equals(candidate.id) &&
                restored.servers.get(0).subscription.equals(SOURCE), "subscription and IDs survive storage round trip");
        timeout = true;
        checkFailedWithoutChanges(restored, "timeout preserves old configuration");
        timeout = false;
        response = "<html>Unexpected subscription error</html>";
        checkFailedWithoutChanges(restored, "invalid response preserves old configuration");

        response = "socks5://a.example.com:1080#Russia\nsocks5://b.example.com:1080#Russia";
        check(refresh(route) == 0, "ambiguous replacement is ignored");
        response = "socks5://c.example.com:1080#Unrelated";
        check(refresh(route) == 0, "one remaining node does not replace a selection from a list");
        selected.singleServerSubscription = true;
        check(refresh(route) == 1, "single-node subscription can change endpoint and label");
        check(SubscriptionUpdates.stage(route, "https://different.example.com/", List.of(NEW)) == 0,
                "updates cannot change another subscription");
        System.out.println("SUBSCRIPTION UPDATES PASSED");
    }

    private static int refresh(Model.Route route) throws Exception {
        return SubscriptionUpdates.stage(route, SOURCE,
                SubscriptionUpdates.candidates(ShareLinkResolver.resolveMany(SOURCE, "test-device")));
    }

    private static void checkFailedWithoutChanges(Model.Route route, String label) throws Exception {
        String previous = route.toJson().toString();
        boolean failed = false;
        try { refresh(route); }
        catch (ShareLinkResolver.ResolutionException expected) { failed = true; }
        check(failed && previous.equals(route.toJson().toString()), label);
    }

    private static void check(boolean condition, String label) {
        if (!condition) throw new AssertionError(label);
        System.out.println("PASS " + label);
    }
}
