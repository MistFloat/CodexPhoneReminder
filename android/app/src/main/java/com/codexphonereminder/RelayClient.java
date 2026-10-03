package com.codexphonereminder;

import android.util.Base64;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;

import javax.crypto.Cipher;
import javax.crypto.spec.SecretKeySpec;
import javax.net.ssl.HttpsURLConnection;

/**
 * Sends an already-paired agent request through the public relay.  The relay
 * only sees routing metadata and AES-GCM ciphertext; the paired computer is
 * the endpoint that decrypts, handles, and encrypts the response.
 */
final class RelayClient {
    private static final SecureRandom RANDOM = new SecureRandom();
    private static final int CONNECT_TIMEOUT_MS = 8_000;
    private static final int POLL_READ_TIMEOUT_MS = 35_000;
    private static final long REQUEST_TIMEOUT_MS = 45_000;

    private final SecureStore.RelayConfig config;

    RelayClient(SecureStore.RelayConfig config) {
        if (config == null) throw new IllegalArgumentException("云中继尚未配置");
        this.config = config;
    }

    String request(String method, String localUrl, String body) throws Exception {
        URL requested = new URL(localUrl);
        String path = requested.getFile();
        if (path == null || path.isEmpty()) path = "/";
        if (!path.startsWith("/")) throw new IOException("无效的电脑 API 路径");

        JSONObject plain = new JSONObject()
            .put("method", method)
            .put("path", path)
            .put("body", body == null ? JSONObject.NULL : body);
        HttpResult created = sendJson("POST", endpoint("/agents/" + segment(config.agentId) + "/requests"),
            seal(plain.toString(), "request"), CONNECT_TIMEOUT_MS);
        requireSuccess(created, "提交云中继请求失败");
        String requestId = json(created, "提交云中继请求失败").optString("requestId");
        if (requestId.isEmpty()) throw new IOException("云中继没有返回请求编号");

        long deadline = System.currentTimeMillis() + REQUEST_TIMEOUT_MS;
        String statusUrl = endpoint("/agents/" + segment(config.agentId) + "/requests/" +
            segment(requestId) + "/response?waitSeconds=25");
        while (System.currentTimeMillis() < deadline) {
            HttpResult result = sendJson("GET", statusUrl, null, POLL_READ_TIMEOUT_MS);
            if (result.status == HttpURLConnection.HTTP_ACCEPTED) {
                pause();
                continue;
            }
            requireSuccess(result, "读取云中继响应失败");
            JSONObject value = json(result, "读取云中继响应失败");
            if ("pending".equalsIgnoreCase(value.optString("status"))) {
                pause();
                continue;
            }
            int statusCode = value.optInt("statusCode", -1);
            JSONObject response = value.optJSONObject("response");
            if (statusCode < 0 || response == null) throw new IOException("云中继返回了不完整的响应");
            String responseText = open(response, "response");
            if (statusCode < 200 || statusCode >= 300) throw new ApiClient.ApiException(statusCode, responseText);
            return responseText.isEmpty() ? "{}" : responseText;
        }
        throw new IOException("云中继等待电脑响应超时");
    }

    private HttpResult sendJson(String method, String endpoint, JSONObject body, int readTimeout) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(endpoint).openConnection();
        if (!(connection instanceof HttpsURLConnection))
            throw new IOException("已拒绝不安全的云中继地址");
        connection.setInstanceFollowRedirects(false);
        connection.setRequestMethod(method);
        connection.setConnectTimeout(CONNECT_TIMEOUT_MS);
        connection.setReadTimeout(readTimeout);
        connection.setUseCaches(false);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("Cache-Control", "no-store");
        connection.setRequestProperty("X-Relay-Device-Token", config.deviceToken);
        if (body != null) {
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.getOutputStream().write(body.toString().getBytes(StandardCharsets.UTF_8));
        }
        int status = connection.getResponseCode();
        InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
        String text = read(stream);
        connection.disconnect();
        return new HttpResult(status, text);
    }

    private JSONObject seal(String text, String direction) throws Exception {
        byte[] nonce = new byte[12];
        RANDOM.nextBytes(nonce);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key(), new javax.crypto.spec.GCMParameterSpec(128, nonce));
        cipher.updateAAD(aad(direction));
        byte[] ciphertext = cipher.doFinal(text.getBytes(StandardCharsets.UTF_8));
        return new JSONObject()
            .put("nonce", Base64.encodeToString(nonce, Base64.NO_WRAP))
            .put("ciphertext", Base64.encodeToString(ciphertext, Base64.NO_WRAP));
    }

    private String open(JSONObject envelope, String direction) throws Exception {
        String nonceText = envelope.optString("nonce");
        String ciphertextText = envelope.optString("ciphertext");
        if (nonceText.isEmpty() || ciphertextText.isEmpty()) throw new IOException("云中继响应缺少加密内容");
        byte[] nonce = decode(nonceText);
        if (nonce.length != 12) throw new IOException("云中继响应的随机数无效");
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), new javax.crypto.spec.GCMParameterSpec(128, nonce));
        cipher.updateAAD(aad(direction));
        return new String(cipher.doFinal(decode(ciphertextText)), StandardCharsets.UTF_8);
    }

    private SecretKeySpec key() throws IOException {
        byte[] value = decode(config.key);
        if (value.length != 32) throw new IOException("云中继密钥长度无效");
        return new SecretKeySpec(value, "AES");
    }

    private byte[] aad(String direction) {
        return (config.agentId + "|" + config.deviceId + "|" + direction).getBytes(StandardCharsets.UTF_8);
    }

    private String endpoint(String suffix) throws IOException {
        String base = config.url;
        while (base.endsWith("/")) base = base.substring(0, base.length() - 1);
        if (!base.startsWith("https://")) throw new IOException("云中继必须使用 HTTPS");
        // The public reverse proxy exposes the relay API below /api/v1.  Accept
        // either a bare HTTPS origin from pairing or an explicitly versioned URL
        // for self-hosted reverse-proxy layouts.
        if (!base.endsWith("/api/v1")) base += "/api/v1";
        return base + suffix;
    }

    private static String segment(String value) throws Exception {
        return URLEncoder.encode(value, "UTF-8").replace("+", "%20");
    }

    private static JSONObject json(HttpResult result, String label) throws IOException {
        try { return new JSONObject(result.text); }
        catch (Exception error) { throw new IOException(label + "：服务返回的不是 JSON", error); }
    }

    private static void requireSuccess(HttpResult result, String label) throws ApiClient.ApiException {
        if (result.status < 200 || result.status >= 300)
            throw new ApiClient.ApiException(result.status, label + "：" + result.text);
    }

    private static String read(InputStream stream) throws IOException {
        StringBuilder text = new StringBuilder();
        if (stream != null) try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) text.append(line);
        }
        return text.toString();
    }

    private static byte[] decode(String value) throws IOException {
        try { return Base64.decode(value, Base64.NO_WRAP); }
        catch (IllegalArgumentException standard) {
            try { return Base64.decode(value, Base64.URL_SAFE | Base64.NO_WRAP); }
            catch (IllegalArgumentException urlSafe) { throw new IOException("云中继密文不是有效的 Base64", urlSafe); }
        }
    }

    private static void pause() throws InterruptedException { Thread.sleep(250); }

    private static final class HttpResult {
        final int status;
        final String text;
        HttpResult(int status, String text) { this.status = status; this.text = text; }
    }
}
