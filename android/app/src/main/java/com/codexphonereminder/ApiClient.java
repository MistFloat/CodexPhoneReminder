package com.codexphonereminder;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.ConnectException;
import java.net.HttpURLConnection;
import java.net.NoRouteToHostException;
import java.net.Proxy;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.net.UnknownHostException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLException;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class ApiClient {
    private final SecureStore store;
    private volatile String lastTransport = "local";
    ApiClient(SecureStore store) { this.store = store; }

    JSONObject claim(String address, String code) throws Exception {
        URL entered = new URL(address.trim());
        String pin = entered.getRef();
        if (!"https".equalsIgnoreCase(entered.getProtocol()) || pin == null || !pin.matches("(?i)[0-9a-f]{64}"))
            throw new IllegalArgumentException("请使用电脑配对页面提供的完整 HTTPS 地址");
        String origin = entered.getProtocol() + "://" + entered.getHost() +
            (entered.getPort() < 0 ? "" : ":" + entered.getPort()) + entered.getPath();
        // A pairing URL is deliberately always a direct pinned-TLS request.
        // Cloud fallback becomes available only after this trusted response
        // supplies relay credentials.
        JSONObject result = new JSONObject(requestDirect(origin.replaceAll("/+$", "") + "/api/pair/claim", "POST",
            new JSONObject().put("code", code).toString(), null, pin));
        if (!pin.equalsIgnoreCase(result.optString("fingerprint"))) throw new CertificateException("电脑身份指纹不匹配");
        return result;
    }

    JSONObject health() throws Exception { return new JSONObject(request(store.address() + "/api/health", "GET", null, null, store.fingerprint())); }
    JSONArray tasks() throws Exception { return new JSONArray(request(store.address() + "/api/tasks", "GET", null, store.token(), store.fingerprint())); }
    JSONObject models() throws Exception { return new JSONObject(request(store.address() + "/api/models", "GET", null, store.token(), store.fingerprint())); }
    JSONObject task(String id) throws Exception {
        JSONObject response = new JSONObject(request(store.address() + "/api/tasks/" + id + "?refresh=" + System.currentTimeMillis(), "GET", null, store.token(), store.fingerprint()));
        JSONObject task = response.optJSONObject("task");
        if (task != null && response.optJSONObject("approval") != null) task.put("approval", response.optJSONObject("approval"));
        return task != null ? task : response;
    }
    JSONObject progress(String id, String after) throws Exception {
        String suffix = after == null || after.isEmpty() ? "" : "?after=" + java.net.URLEncoder.encode(after, "UTF-8");
        return new JSONObject(request(store.address() + "/api/tasks/" + id + "/progress" + suffix, "GET", null, store.token(), store.fingerprint()));
    }
    void reply(String id, String message, String model) throws Exception {
        request(store.address() + "/api/tasks/" + id + "/reply", "POST",
            new JSONObject().put("message", message).put("model", model.isEmpty() ? JSONObject.NULL : model).toString(), store.token(), store.fingerprint());
    }
    void decide(String approvalId, String nonce, boolean approved, boolean highRiskConfirmed) throws Exception {
        request(store.address() + "/api/approvals/" + approvalId, "POST",
            new JSONObject().put("approved", approved).put("nonce", nonce)
                .put("highRiskConfirmed", highRiskConfirmed).toString(), store.token(), store.fingerprint());
    }
    void archive(String id) throws Exception {
        request(store.address() + "/api/tasks/" + id + "/archive", "POST", "{}", store.token(), store.fingerprint());
    }

    String connectionMode() { return lastTransport; }

    private String request(String url, String method, String body, String token, String pin) throws Exception {
        try {
            String response = requestDirect(url, method, body, token, pin);
            lastTransport = "local";
            return response;
        } catch (Exception localError) {
            // Only connectivity failures may use the relay.  A failed TLS pin,
            // rejected device token, or normal HTTP error must never be hidden
            // by routing the request somewhere else.
            SecureStore.RelayConfig relay = store.relay();
            if (relay == null || !isLocalNetworkFailure(localError)) throw localError;
            try {
                String response = new RelayClient(relay).request(method, url, body);
                lastTransport = "relay";
                return response;
            } catch (Exception relayError) {
                relayError.addSuppressed(localError);
                throw relayError;
            }
        }
    }

    private String requestDirect(String url, String method, String body, String token, String pin) throws Exception {
        // The agent is a paired LAN endpoint. Do not send its address to an
        // Android/system HTTP proxy, which cannot reach a private computer IP
        // and would also defeat the direct pinned-TLS connection.
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection(Proxy.NO_PROXY);
        if (!(connection instanceof HttpsURLConnection)) throw new CertificateException("已拒绝不安全的 HTTP API 连接");
        HttpsURLConnection secure = (HttpsURLConnection) connection;
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[]{new PinTrustManager(pin)}, null);
        secure.setSSLSocketFactory(context.getSocketFactory());
        secure.setHostnameVerifier((hostname, session) -> true);
        connection.setRequestMethod(method);
        // Once a verified relay is paired, fail a blocked campus-LAN route
        // quickly.  Normal local endpoints respond immediately; a retry via
        // the cloud relay is preferable to leaving the UI frozen for 15 s.
        boolean hasRelay = store.relay() != null;
        connection.setConnectTimeout(hasRelay ? 2_000 : 5_000);
        connection.setReadTimeout(hasRelay ? 4_000 : 15_000);
        connection.setUseCaches(false);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("Cache-Control", "no-cache, no-store");
        connection.setRequestProperty("Pragma", "no-cache");
        if (token != null) connection.setRequestProperty("X-Device-Token", token);
        if (body != null) {
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.getOutputStream().write(body.getBytes(StandardCharsets.UTF_8));
        }
        int status = connection.getResponseCode();
        InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
        StringBuilder text = new StringBuilder();
        if (stream != null) try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line; while ((line = reader.readLine()) != null) text.append(line);
        }
        connection.disconnect();
        if (status < 200 || status >= 300) throw new ApiException(status, text.toString());
        return text.length() == 0 ? "{}" : text.toString();
    }

    private static boolean isLocalNetworkFailure(Throwable error) {
        boolean networkError = false;
        // Follow causes because Android often wraps connection and timeout
        // exceptions while a certificate failure must remain terminal.
        for (Throwable current = error; current != null; current = current.getCause()) {
            if (current instanceof ApiException || current instanceof CertificateException || current instanceof SSLException)
                return false;
            if (current instanceof ConnectException || current instanceof NoRouteToHostException ||
                current instanceof UnknownHostException || current instanceof SocketTimeoutException)
                networkError = true;
            else if (current instanceof IOException) networkError = true;
        }
        return networkError;
    }

    private static final class PinTrustManager implements X509TrustManager {
        private final String expected;
        PinTrustManager(String expected) throws CertificateException {
            if (expected == null || !expected.matches("(?i)[0-9a-f]{64}")) throw new CertificateException("缺少有效的电脑证书指纹");
            this.expected = expected;
        }
        @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException { throw new CertificateException("不接受客户端证书"); }
        @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            if (chain == null || chain.length == 0) throw new CertificateException("电脑没有提供 TLS 证书");
            try {
                byte[] digest = MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                StringBuilder actual = new StringBuilder();
                for (byte value : digest) actual.append(String.format("%02X", value));
                if (!expected.equalsIgnoreCase(actual.toString())) throw new CertificateException("电脑 TLS 证书指纹不匹配");
            } catch (CertificateException error) { throw error; }
            catch (Exception error) { throw new CertificateException("无法验证电脑 TLS 证书", error); }
        }
        @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
    }

    static final class ApiException extends Exception {
        final int status;
        ApiException(int status, String message) { super("HTTP " + status + ": " + message); this.status = status; }
    }
}
