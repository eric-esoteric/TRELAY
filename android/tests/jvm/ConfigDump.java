package com.trelay.android;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.json.JSONObject;

// JVM check: parse sample links, build Xray/Mihomo configs into a folder for `xray run -test` / `mihomo -t`.
public class ConfigDump {
    public static void main(String[] args) throws Exception {
        File out = new File(args[0]);
        out.mkdirs();
        String ssCred = java.util.Base64.getEncoder().encodeToString("chacha20-ietf-poly1305:pa+ss/word".getBytes(StandardCharsets.UTF_8));
        String vmessJson = new JSONObject().put("v", "2").put("ps", "VMess Тест").put("add", "vm.example.com").put("port", "443")
                .put("id", "b831381d-6324-4d53-ad4f-8cda48b30811").put("aid", "0").put("net", "ws").put("type", "none")
                .put("host", "vm.example.com").put("path", "/ws").put("tls", "tls").put("sni", "vm.example.com").toString();
        List<String> links = new ArrayList<>(Arrays.asList(
                "vless://b831381d-6324-4d53-ad4f-8cda48b30811@1.2.3.4:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.microsoft.com&fp=chrome&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&sid=6ba85179e30d4fc2&type=tcp#Россия 🇷🇺",
                "vless://provider-id-123@ru.example.com:8443?security=tls&type=grpc&serviceName=gun&sni=ru.example.com#gRPC",
                "vless://b831381d-6324-4d53-ad4f-8cda48b30811@x.example.com:443?security=tls&type=xhttp&path=%2Fxh&host=x.example.com&mode=auto#XHTTP",
                "vmess://" + java.util.Base64.getEncoder().encodeToString(vmessJson.getBytes(StandardCharsets.UTF_8)),
                "trojan://pass%40word@tr.example.com:443?sni=tr.example.com#Trojan",
                "ss://" + ssCred + "@ss.example.com:8388#SS",
                "ss://2022-blake3-aes-128-gcm:YctPZ6U7xPPcU%2Bgp3u%2B0tx%2FtRizJN9K8y%2BuKlW2qjlI%3D@ss2.example.com:443#SS2022",
                "socks5://user:pass@10.0.0.5:1080#Socks",
                "hy2://secret@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=xyz&insecure=1#HY2",
                "tuic://b831381d-6324-4d53-ad4f-8cda48b30811:pw@tuic.example.com:443?sni=tuic.example.com&congestion_control=bbr#TUIC",
                "wireguard://cGFzcw%3D%3D@wg.example.com:51820?publickey=Z1XXLsKYkYxuiYjJIkRvtIKFepCYHTgON%2BGwPq7SOV4%3D&address=10.0.0.2%2F32,fd00::2%2F128&mtu=1280#WG",
                "anytls://pw@any.example.com:443?sni=any.example.com#AnyTLS"
        ));
        String clash = "proxies:\n  - name: \"RU-1\"\n    type: vless\n    server: ru.example.com\n    port: 443\n    uuid: b831381d-6324-4d53-ad4f-8cda48b30811\n    tls: true\n    servername: ru.example.com\n    network: ws\n    ws-opts: {path: /ws}\n  - {name: US-hy2, type: hysteria2, server: us.example.com, port: 443, password: pw, sni: us.example.com}\nproxy-groups:\n  - {name: auto, type: select, proxies: [RU-1]}\nrules:\n  - MATCH,auto\n";
        links.addAll(ShareLinkResolver.extractSupportedLinks(clash, "clash"));
        String singbox = "{\"outbounds\":[{\"type\":\"tuic\",\"tag\":\"sb-tuic\",\"server\":\"t.example.com\",\"server_port\":443,\"uuid\":\"b831381d-6324-4d53-ad4f-8cda48b30811\",\"password\":\"x\",\"tls\":{\"enabled\":true,\"server_name\":\"t.example.com\"}},{\"type\":\"direct\",\"tag\":\"direct\"}]}";
        links.addAll(ShareLinkResolver.extractSupportedLinks(singbox, "singbox"));
        String xrayArray = "[{\"remarks\":\"Happ RU\",\"inbounds\":[{\"tag\":\"socks\",\"port\":10808,\"listen\":\"0.0.0.0\",\"protocol\":\"socks\"}],\"outbounds\":[{\"tag\":\"proxy\",\"protocol\":\"vless\",\"settings\":{\"vnext\":[{\"address\":\"h.example.com\",\"port\":443,\"users\":[{\"id\":\"b831381d-6324-4d53-ad4f-8cda48b30811\",\"encryption\":\"none\"}]}]}},{\"tag\":\"direct\",\"protocol\":\"freedom\"}],\"routing\":{\"rules\":[{\"type\":\"field\",\"ip\":[\"geoip:private\"],\"outboundTag\":\"direct\"}]},\"api\":{\"tag\":\"api\",\"listen\":\"127.0.0.1:1111\"}}]";
        links.addAll(ShareLinkResolver.extractSupportedLinks(xrayArray, "xray"));
        String base64List = java.util.Base64.getEncoder().encodeToString(String.join("\n", links.subList(0, 3)).getBytes(StandardCharsets.UTF_8));
        List<String> decoded = ShareLinkResolver.extractSupportedLinks(base64List, "b64");
        if (decoded.size() != 3) throw new AssertionError("base64 list: " + decoded.size());

        int index = 0;
        for (String link : links) {
            index++;
            String message = Links.validate(link);
            String name = Links.suggestName(link);
            String engine = Links.engine(link);
            System.out.println(index + " " + Links.kindLabel(link) + " engine=" + engine + " name=" + name + " endpoint=" + Links.endpoint(link) + " :: " + message);
            if (engine.equals("xray")) {
                Files.write(new File(out, index + "-xray.json").toPath(), XrayConfigs.backend(link, 20000 + index).toString(2).getBytes(StandardCharsets.UTF_8));
            } else {
                Files.write(new File(out, index + "-mihomo.yaml").toPath(), MihomoConfig.prepareForSocks(link, 20000 + index).getBytes(StandardCharsets.UTF_8));
            }
        }
        List<XrayConfigs.FrontGroup> groups = new ArrayList<>();
        groups.add(new XrayConfigs.FrontGroup("g0", Arrays.asList(10123, 10124), 10810));
        groups.add(new XrayConfigs.FrontGroup("g1", Arrays.asList(10200), 10811));
        JSONObject front = XrayConfigs.front(groups, 5353, 1500);
        // tun inbound cannot be created in the test VM: swap it for socks to validate the rest.
        front.getJSONArray("inbounds").put(0, XrayConfigs.socksInbound(19999, "tun"));
        Files.write(new File(out, "front.json").toPath(), front.toString(2).getBytes(StandardCharsets.UTF_8));
        Files.write(new File(out, "fragment.json").toPath(), XrayConfigs.fragmentProxy(19998).toString(2).getBytes(StandardCharsets.UTF_8));
        // Negative checks
        for (String bad : new String[] {"vless://not uuid@h:1", "vless://id@host?security=reality", "hy2://@:0", "https://localhost/sub", "key://AAAA"}) {
            try { Links.validate(bad); System.out.println("NOT REJECTED: " + bad); }
            catch (IllegalArgumentException expected) { System.out.println("rejected: " + bad + " -> " + expected.getMessage()); }
        }
        System.out.println("OK " + links.size());
    }
}
