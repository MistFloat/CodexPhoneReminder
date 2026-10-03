package com.codexphonereminder;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class SecureStore {
    private static final String ALIAS = "codex_phone_reminder_pairing";
    private final SharedPreferences prefs;

    SecureStore(Context context) {
        prefs = context.getSharedPreferences("secure_pairing", Context.MODE_PRIVATE);
    }

    void savePairing(String address, String token, String fingerprint, JSONObject relay) throws Exception {
        SharedPreferences.Editor edit = prefs.edit()
            .putString("address", encrypt(normalize(address)))
            .putString("token", encrypt(token))
            .putString("fingerprint", encrypt(fingerprint));

        RelayConfig config = RelayConfig.from(relay);
        if (config == null) {
            edit.remove("relay_url").remove("relay_agent_id").remove("relay_device_id")
                .remove("relay_device_token").remove("relay_key");
        } else {
            // The local agent response is already authenticated by its pinned TLS
            // certificate.  Keep its relay credentials in the same Android
            // Keystore-backed store as the normal pairing token.
            edit.putString("relay_url", encrypt(config.url))
                .putString("relay_agent_id", encrypt(config.agentId))
                .putString("relay_device_id", encrypt(config.deviceId))
                .putString("relay_device_token", encrypt(config.deviceToken))
                .putString("relay_key", encrypt(config.key));
        }
        edit.apply();
    }

    String address() { return decryptQuietly(prefs.getString("address", null)); }
    String token() { return decryptQuietly(prefs.getString("token", null)); }
    String fingerprint() { return decryptQuietly(prefs.getString("fingerprint", null)); }
    RelayConfig relay() {
        return RelayConfig.from(
            decryptQuietly(prefs.getString("relay_url", null)),
            decryptQuietly(prefs.getString("relay_agent_id", null)),
            decryptQuietly(prefs.getString("relay_device_id", null)),
            decryptQuietly(prefs.getString("relay_device_token", null)),
            decryptQuietly(prefs.getString("relay_key", null)));
    }
    boolean paired() {
        String address = address(), fingerprint = fingerprint();
        return address != null && address.startsWith("https://") && token() != null &&
            fingerprint != null && fingerprint.matches("(?i)[0-9a-f]{64}");
    }
    void clear() { prefs.edit().clear().apply(); }

    static final class RelayConfig {
        final String url;
        final String agentId;
        final String deviceId;
        final String deviceToken;
        final String key;

        private RelayConfig(String url, String agentId, String deviceId, String deviceToken, String key) {
            this.url = url;
            this.agentId = agentId;
            this.deviceId = deviceId;
            this.deviceToken = deviceToken;
            this.key = key;
        }

        static RelayConfig from(JSONObject value) {
            if (value == null) return null;
            return from(value.optString("url", null), value.optString("agentId", null),
                value.optString("deviceId", null), value.optString("deviceToken", null),
                value.optString("key", null));
        }

        static RelayConfig from(String url, String agentId, String deviceId, String deviceToken, String key) {
            if (blank(url) || blank(agentId) || blank(deviceId) || blank(deviceToken) || blank(key)) return null;
            String normalized = normalize(url);
            if (!normalized.startsWith("https://")) return null;
            return new RelayConfig(normalized, agentId, deviceId, deviceToken, key);
        }

        private static boolean blank(String value) { return value == null || value.trim().isEmpty(); }
    }

    private static String normalize(String value) {
        String v = value.trim();
        int fragment = v.indexOf('#');
        if (fragment >= 0) v = v.substring(0, fragment);
        while (v.endsWith("/")) v = v.substring(0, v.length() - 1);
        return v;
    }

    private String encrypt(String plain) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(plain.getBytes(StandardCharsets.UTF_8));
        return Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + "." +
            Base64.encodeToString(encrypted, Base64.NO_WRAP);
    }

    private String decryptQuietly(String value) {
        if (value == null) return null;
        try {
            String[] pieces = value.split("\\.", 2);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(),
                new GCMParameterSpec(128, Base64.decode(pieces[0], Base64.NO_WRAP)));
            return new String(cipher.doFinal(Base64.decode(pieces[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
        } catch (Exception ignored) { return null; }
    }

    private SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (!store.containsAlias(ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build());
            generator.generateKey();
        }
        return ((KeyStore.SecretKeyEntry) store.getEntry(ALIAS, null)).getSecretKey();
    }
}
