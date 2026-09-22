package com.trelay.android;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.SecureRandom;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.JSONArray;
import org.json.JSONObject;

// Routes, keys and settings encrypted with an Android Keystore AES-GCM key (Windows used DPAPI).
final class ConfigStore {
    private static final String ALIAS = "trelay-profiles-v1";
    private static final String CONFIG = "config-v3";
    private static final String LEGACY_PROFILES = "profiles";
    private static final String SUBSCRIPTION_ID = "subscription-client-id";
    private static final Object LOCK = new Object();
    private final SharedPreferences preferences;

    ConfigStore(Context context) {
        preferences = context.getApplicationContext().getSharedPreferences("vault", Context.MODE_PRIVATE);
    }

    private SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        KeyStore.Entry entry = store.getEntry(ALIAS, null);
        if (entry instanceof KeyStore.SecretKeyEntry) return ((KeyStore.SecretKeyEntry) entry).getSecretKey();
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256).build());
        return generator.generateKey();
    }

    private String decrypt(String encoded) throws Exception {
        byte[] payload = Base64.decode(encoded, Base64.NO_WRAP);
        if (payload.length < 13) throw new IllegalStateException("Хранилище повреждено.");
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, payload, 0, 12));
        return new String(cipher.doFinal(payload, 12, payload.length - 12), StandardCharsets.UTF_8);
    }

    private String encrypt(String clear) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] nonce = cipher.getIV();
        byte[] encrypted = cipher.doFinal(clear.getBytes(StandardCharsets.UTF_8));
        byte[] payload = new byte[nonce.length + encrypted.length];
        System.arraycopy(nonce, 0, payload, 0, nonce.length);
        System.arraycopy(encrypted, 0, payload, nonce.length, encrypted.length);
        return Base64.encodeToString(payload, Base64.NO_WRAP);
    }

    Model.Config load() throws Exception {
        synchronized (LOCK) {
            String encoded = preferences.getString(CONFIG, null);
            if (encoded != null) return Model.Config.fromJson(new JSONObject(decrypt(encoded)));
            Model.Config config = new Model.Config();
            String legacy = preferences.getString(LEGACY_PROFILES, null);
            if (legacy != null) {
                // Beta 0.2 kept a flat list of keys without applications: keep them as a disabled route.
                JSONArray profiles = new JSONArray(decrypt(legacy));
                if (profiles.length() > 0) {
                    Model.Route route = new Model.Route();
                    route.name = "Импортированные серверы";
                    route.enabled = false;
                    for (int i = 0; i < profiles.length(); i++) {
                        JSONObject profile = profiles.getJSONObject(i);
                        Model.Server server = new Model.Server();
                        server.name = profile.optString("name", "Сервер");
                        server.link = profile.optString("link");
                        if (!server.link.isEmpty()) route.servers.add(server);
                    }
                    config.routes.add(route);
                }
                save(config);
                preferences.edit().remove(LEGACY_PROFILES).apply();
            }
            return config;
        }
    }

    void save(Model.Config config) throws Exception {
        synchronized (LOCK) {
            if (!preferences.edit().putString(CONFIG, encrypt(config.toJson().toString())).commit())
                throw new IllegalStateException("Не удалось сохранить настройки.");
        }
    }

    String subscriptionClientId(boolean create) throws Exception {
        synchronized (LOCK) {
            String encoded = preferences.getString(SUBSCRIPTION_ID, null);
            if (encoded != null) return decrypt(encoded);
            if (!create) return null;
            byte[] random = new byte[16];
            new SecureRandom().nextBytes(random);
            StringBuilder id = new StringBuilder("android-");
            for (byte value : random) id.append(String.format(java.util.Locale.ROOT, "%02x", value & 0xff));
            if (!preferences.edit().putString(SUBSCRIPTION_ID, encrypt(id.toString())).commit())
                throw new IllegalStateException("Не удалось сохранить ID подписки.");
            return id.toString();
        }
    }
}
