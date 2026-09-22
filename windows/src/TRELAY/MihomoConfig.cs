using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;
using YamlDotNet.Serialization;

namespace TRELAY;

internal static class MihomoConfig
{
	internal const string Prefix = "trelay-mihomo://";

	private const int MaxNodes = 128;

	private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
	{
		MaxJsonLength = 2097152
	};

	internal static bool IsDirectLink(string value)
	{
		string text = Scheme(value);
		switch (text)
		{
		case "ss":
		{
			string text2 = (value ?? "").Substring("ss://".Length);
			int num = text2.IndexOfAny(new char[2] { '?', '#' });
			if (num >= 0)
			{
				text2 = text2.Substring(0, num);
			}
			if (text2.IndexOf('@') >= 0)
			{
				if (Uri.TryCreate(value, UriKind.Absolute, out var result))
				{
					return !string.IsNullOrWhiteSpace(HttpUtility.ParseQueryString(result.Query)["plugin"]);
				}
				return false;
			}
			return true;
		}
		default:
			return text == "anytls";
		case "hysteria":
		case "hysteria2":
		case "hy2":
		case "tuic":
		case "ssr":
		case "wireguard":
		case "wg":
			return true;
		}
	}

	internal static bool TryExtractClash(string text, out List<string> links)
	{
		links = new List<string>();
		if (string.IsNullOrWhiteSpace(text) || text.Length > 2097152)
		{
			return false;
		}
		try
		{
			object value = new DeserializerBuilder().Build().Deserialize<object>(text);
			Dictionary<string, object> dictionary = Normalize(value, 0) as Dictionary<string, object>;
			object value2;
			List<object> list = ((dictionary != null && dictionary.TryGetValue("proxies", out value2)) ? AsList(value2) : null);
			if (list == null || list.Count == 0)
			{
				return false;
			}
			Dictionary<string, Dictionary<string, object>> proxies = NamedMap(list);
			object value3;
			List<object> items = (dictionary.TryGetValue("proxy-groups", out value3) ? AsList(value3) : new List<object>());
			Dictionary<string, Dictionary<string, object>> groups = NamedMap(items);
			foreach (object item in list.Take(128))
			{
				Dictionary<string, object> dictionary2 = item as Dictionary<string, object>;
				if (ValidateProxy(dictionary2, out var _))
				{
					string text2 = Convert.ToString(dictionary2["name"], CultureInfo.InvariantCulture);
					Dictionary<string, Dictionary<string, object>> dictionary3 = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
					Dictionary<string, Dictionary<string, object>> dictionary4 = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
					CollectDependency(text2, proxies, groups, dictionary3, dictionary4, new HashSet<string>(StringComparer.Ordinal), 0);
					if (!dictionary3.ContainsKey(text2))
					{
						dictionary3[text2] = dictionary2;
					}
					try
					{
						links.Add(EncodePack(text2, dictionary3.Values.Cast<object>().ToList(), dictionary4.Values.Cast<object>().ToList()));
					}
					catch (InvalidOperationException)
					{
					}
				}
			}
			return links.Count > 0;
		}
		catch
		{
			links.Clear();
			return false;
		}
	}

	internal static bool TryExtractSingBox(object parsed, out List<string> links)
	{
		links = new List<string>();
		Dictionary<string, object> dictionary = parsed as Dictionary<string, object>;
		object value;
		List<object> list = ((dictionary != null && dictionary.TryGetValue("outbounds", out value)) ? AsList(value) : null);
		List<object> list2 = list ?? AsList(parsed);
		if (list2 == null && dictionary != null)
		{
			List<object> list3 = new List<object>();
			list3.Add(dictionary);
			list2 = list3;
		}
		if (list2 == null)
		{
			return false;
		}
		foreach (object item in list2.Take(128))
		{
			Dictionary<string, object> source = item as Dictionary<string, object>;
			if (TryMapSingBox(source, out var proxy) || (list == null && HasPlugin(source) && TryMapSip008(source, out proxy)))
			{
				string selected = Convert.ToString(proxy["name"], CultureInfo.InvariantCulture);
				try
				{
					links.Add(EncodePack(selected, new List<object> { proxy }, new List<object>()));
				}
				catch (InvalidOperationException)
				{
				}
			}
		}
		return links.Count > 0;
	}

	internal static bool TryExtractSip008(object parsed, out List<string> links)
	{
		links = new List<string>();
		List<object> list = AsList(parsed);
		Dictionary<string, object> dictionary = parsed as Dictionary<string, object>;
		if (list == null && dictionary != null)
		{
			List<object> list2 = new List<object>();
			list2.Add(dictionary);
			list = list2;
		}
		if (list == null)
		{
			return false;
		}
		foreach (object item in list.Take(128))
		{
			if (TryMapSip008(item as Dictionary<string, object>, out var proxy))
			{
				string selected = Convert.ToString(proxy["name"], CultureInfo.InvariantCulture);
				try
				{
					links.Add(EncodePack(selected, new List<object> { proxy }, new List<object>()));
				}
				catch (InvalidOperationException)
				{
				}
			}
		}
		return links.Count > 0;
	}

	internal static List<string> ExtractClashProviderUrls(string text)
	{
		List<string> list = new List<string>();
		try
		{
			object value = new DeserializerBuilder().Build().Deserialize<object>(text);
			object value2;
			Dictionary<string, object> dictionary = ((Normalize(value, 0) is Dictionary<string, object> dictionary2 && dictionary2.TryGetValue("proxy-providers", out value2)) ? (value2 as Dictionary<string, object>) : null);
			if (dictionary == null)
			{
				return list;
			}
			foreach (object item in dictionary.Values.Take(16))
			{
				if (item is Dictionary<string, object> dictionary3 && dictionary3.TryGetValue("type", out var value3) && string.Equals(Convert.ToString(value3, CultureInfo.InvariantCulture), "http", StringComparison.OrdinalIgnoreCase) && dictionary3.TryGetValue("url", out var value4) && value4 is string && Uri.TryCreate((string)value4, UriKind.Absolute, out var result) && result.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(result.Host) && string.IsNullOrEmpty(result.UserInfo) && !result.IsLoopback && result.Fragment.Length == 0)
				{
					list.Add(result.AbsoluteUri);
				}
			}
		}
		catch
		{
		}
		return list.Distinct(StringComparer.Ordinal).ToList();
	}

	internal static bool TryEncodeDirect(string value, out string encoded, out string message)
	{
		encoded = "";
		message = "Ссылка повреждена или содержит неподдерживаемые параметры.";
		try
		{
			if (!TryMapDirect(value, out var proxy, out message))
			{
				return false;
			}
			string selected = Convert.ToString(proxy["name"], CultureInfo.InvariantCulture);
			encoded = EncodePack(selected, new List<object> { proxy }, new List<object>());
			message = Convert.ToString(proxy["type"], CultureInfo.InvariantCulture).ToUpperInvariant() + "-ключ проверен.";
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryDecode(string value, out Dictionary<string, object> pack, out string message)
	{
		pack = null;
		message = "Встроенная конфигурация Mihomo повреждена.";
		try
		{
			if (!value.StartsWith("trelay-mihomo://", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			byte[] array = DecodeBase64(value.Substring("trelay-mihomo://".Length));
			if (array.Length == 0 || array.Length > 1048576)
			{
				return false;
			}
			lock (Json)
			{
				pack = Json.DeserializeObject(StrictUtf8.GetString(array)) as Dictionary<string, object>;
			}
			object value2 = null;
			object value3;
			List<object> list = ((pack != null && pack.TryGetValue("selected", out value2) && value2 is string && pack.TryGetValue("proxies", out value3)) ? AsList(value3) : null);
			if (list == null || list.Count == 0 || list.Count > 32)
			{
				pack = null;
				return false;
			}
			string b = (string)value2;
			bool flag = false;
			foreach (object item in list)
			{
				Dictionary<string, object> dictionary = item as Dictionary<string, object>;
				if (!ValidateProxy(dictionary, out var _))
				{
					pack = null;
					return false;
				}
				if (string.Equals(Convert.ToString(dictionary["name"], CultureInfo.InvariantCulture), b, StringComparison.Ordinal))
				{
					flag = true;
				}
			}
			if (!flag)
			{
				pack = null;
				return false;
			}
			message = "Конфигурация Mihomo проверена.";
			return true;
		}
		catch
		{
			pack = null;
			return false;
		}
	}

	internal static string PrepareForSocks(string key, int port)
	{
		if (IsDirectLink(key))
		{
			if (!TryEncodeDirect(key, out var encoded, out var message))
			{
				throw new FormatException(message);
			}
			key = encoded;
		}
		if (!TryDecode(key, out var pack, out var message2))
		{
			throw new FormatException(message2);
		}
		List<object> list = AsList(pack["proxies"]);
		object value;
		List<object> list2 = (pack.TryGetValue("groups", out value) ? AsList(value) : new List<object>());
		string text = Convert.ToString(pack["selected"], CultureInfo.InvariantCulture);
		string text2 = UniqueName("TRELAY", list.Concat(list2));
		list2.Add(new Dictionary<string, object>
		{
			{ "name", text2 },
			{ "type", "select" },
			{
				"proxies",
				new object[1] { text }
			}
		});
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("socks-port", port);
		dictionary.Add("allow-lan", false);
		dictionary.Add("bind-address", "127.0.0.1");
		dictionary.Add("mode", "rule");
		dictionary.Add("log-level", "warning");
		dictionary.Add("ipv6", true);
		dictionary.Add("proxies", list);
		dictionary.Add("proxy-groups", list2);
		dictionary.Add("rules", new object[1] { "MATCH," + text2 });
		Dictionary<string, object> graph = dictionary;
		return new SerializerBuilder().DisableAliases().Build().Serialize(graph);
	}

	internal static string GetName(string value)
	{
		if (!TryDecode(value, out var pack, out var _))
		{
			return "";
		}
		return CleanName(Convert.ToString(pack["selected"], CultureInfo.InvariantCulture));
	}

	internal static string GetEndpoint(string value)
	{
		if (!TryDecode(value, out var pack, out var _))
		{
			return "сервер";
		}
		foreach (object item in AsList(pack["proxies"]))
		{
			if (item is Dictionary<string, object> dictionary && string.Equals(Convert.ToString(dictionary["name"], CultureInfo.InvariantCulture), Convert.ToString(pack["selected"], CultureInfo.InvariantCulture), StringComparison.Ordinal))
			{
				return Convert.ToString(dictionary["server"], CultureInfo.InvariantCulture) + ":" + Convert.ToString(dictionary["port"], CultureInfo.InvariantCulture);
			}
		}
		return "сервер";
	}

	private static string EncodePack(string selected, List<object> proxies, List<object> groups)
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("selected", selected);
		dictionary.Add("proxies", proxies);
		dictionary.Add("groups", groups);
		Dictionary<string, object> obj = dictionary;
		string s;
		lock (Json)
		{
			s = Json.Serialize(obj);
		}
		string text = "trelay-mihomo://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-')
			.Replace('/', '_');
		if (text.Length > 65536)
		{
			throw new InvalidOperationException("Конфигурация одного сервера превышает 64 КБ.");
		}
		return text;
	}

	private static Dictionary<string, Dictionary<string, object>> NamedMap(List<object> items)
	{
		Dictionary<string, Dictionary<string, object>> dictionary = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
		foreach (object item in items)
		{
			if (item is Dictionary<string, object> dictionary2 && dictionary2.TryGetValue("name", out var value) && value is string && !string.IsNullOrWhiteSpace((string)value) && !dictionary.ContainsKey((string)value))
			{
				dictionary[(string)value] = dictionary2;
			}
		}
		return dictionary;
	}

	private static void CollectDependency(string name, Dictionary<string, Dictionary<string, object>> proxies, Dictionary<string, Dictionary<string, object>> groups, Dictionary<string, Dictionary<string, object>> resultProxies, Dictionary<string, Dictionary<string, object>> resultGroups, HashSet<string> visiting, int depth)
	{
		if (depth > 12 || !visiting.Add(name) || resultProxies.Count + resultGroups.Count > 31)
		{
			return;
		}
		Dictionary<string, object> value3;
		if (proxies.TryGetValue(name, out var value))
		{
			resultProxies[name] = value;
			if (value.TryGetValue("dialer-proxy", out var value2) && value2 is string)
			{
				CollectDependency((string)value2, proxies, groups, resultProxies, resultGroups, visiting, depth + 1);
			}
		}
		else if (groups.TryGetValue(name, out value3))
		{
			resultGroups[name] = value3;
			object value4;
			List<object> list = (value3.TryGetValue("proxies", out value4) ? AsList(value4) : null);
			if (list != null)
			{
				foreach (string item in list.OfType<string>())
				{
					CollectDependency(item, proxies, groups, resultProxies, resultGroups, visiting, depth + 1);
				}
			}
		}
		visiting.Remove(name);
	}

	private static bool TryMapDirect(string value, out Dictionary<string, object> proxy, out string message)
	{
		proxy = null;
		message = "Неподдерживаемый протокол.";
		string text = Scheme(value);
		if (text == "ss")
		{
			return TryMapShadowsocks(value, out proxy, out message);
		}
		if (text == "ssr")
		{
			return TryMapSsr(value, out proxy, out message);
		}
		if (!Uri.TryCreate(value, UriKind.Absolute, out var result) || string.IsNullOrWhiteSpace(result.Host) || result.Port < 1 || result.Port > 65535)
		{
			message = "В ссылке нет сервера или порта.";
			return false;
		}
		NameValueCollection q = HttpUtility.ParseQueryString(result.Query);
		string value2 = FragmentName(result, result.Host);
		proxy = new Dictionary<string, object>
		{
			{ "name", value2 },
			{ "server", result.Host },
			{ "port", result.Port },
			{ "udp", true }
		};
		switch (text)
		{
		case "hysteria":
		{
			proxy["type"] = "hysteria";
			string value5 = Uri.UnescapeDataString(result.UserInfo ?? "");
			if (!string.IsNullOrWhiteSpace(value5))
			{
				proxy["auth-str"] = value5;
			}
			CopyQuery(q, proxy, "auth-str", "auth", "auth_str");
			CopyQuery(q, proxy, "sni", "sni", "peer");
			CopyQuery(q, proxy, "protocol", "protocol");
			CopyQuery(q, proxy, "obfs", "obfs");
			if (int.TryParse(First(q, "up", "upmbps"), out var result3))
			{
				proxy["up"] = result3;
			}
			if (int.TryParse(First(q, "down", "downmbps"), out var result4))
			{
				proxy["down"] = result4;
			}
			break;
		}
		case "hysteria2":
		case "hy2":
			proxy["type"] = "hysteria2";
			proxy["password"] = Uri.UnescapeDataString(result.UserInfo ?? "");
			CopyQuery(q, proxy, "sni", "sni");
			CopyQuery(q, proxy, "obfs", "obfs");
			CopyQuery(q, proxy, "obfs-password", "obfs-password");
			CopyQuery(q, proxy, "obfs-password", "obfs_password");
			CopyQuery(q, proxy, "ports", "mport");
			CopyQuery(q, proxy, "ports", "ports");
			break;
		case "tuic":
		{
			proxy["type"] = "tuic";
			string text2 = Uri.UnescapeDataString(result.UserInfo ?? "");
			int num = text2.IndexOf(':');
			if (num > 0)
			{
				proxy["uuid"] = text2.Substring(0, num);
				proxy["password"] = text2.Substring(num + 1);
			}
			else
			{
				proxy["token"] = text2;
			}
			CopyQuery(q, proxy, "sni", "sni");
			CopyQuery(q, proxy, "congestion-controller", "congestion_control");
			CopyQuery(q, proxy, "udp-relay-mode", "udp_relay_mode");
			break;
		}
		case "wireguard":
		case "wg":
		{
			proxy["type"] = "wireguard";
			proxy["private-key"] = Uri.UnescapeDataString(result.UserInfo ?? "");
			CopyQuery(q, proxy, "public-key", "publickey");
			CopyQuery(q, proxy, "public-key", "public_key");
			CopyQuery(q, proxy, "pre-shared-key", "presharedkey");
			CopyQuery(q, proxy, "pre-shared-key", "pre_shared_key");
			string text3 = First(q, "ip", "address", "local_address");
			if (!string.IsNullOrWhiteSpace(text3))
			{
				string[] source = (from x in text3.Split(new char[1] { ',' }, StringSplitOptions.RemoveEmptyEntries)
					select x.Trim()).ToArray();
				string value3 = source.FirstOrDefault((string x) => x.IndexOf(':') < 0);
				string value4 = source.FirstOrDefault((string x) => x.IndexOf(':') >= 0);
				if (!string.IsNullOrWhiteSpace(value3))
				{
					proxy["ip"] = value3;
				}
				if (!string.IsNullOrWhiteSpace(value4))
				{
					proxy["ipv6"] = value4;
				}
			}
			if (int.TryParse(First(q, "mtu"), out var result2))
			{
				proxy["mtu"] = result2;
			}
			string text4 = First(q, "reserved");
			if (!string.IsNullOrWhiteSpace(text4))
			{
				proxy["reserved"] = (from x in text4.Split(',')
					select Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
			}
			break;
		}
		case "anytls":
			proxy["type"] = "anytls";
			proxy["password"] = Uri.UnescapeDataString(result.UserInfo ?? "");
			CopyQuery(q, proxy, "sni", "sni");
			break;
		default:
			proxy = null;
			return false;
		}
		string value6 = First(q, "insecure", "allow_insecure", "skip-cert-verify");
		if (!string.IsNullOrEmpty(value6))
		{
			proxy["skip-cert-verify"] = ParseBool(value6);
		}
		string text5 = First(q, "alpn");
		if (!string.IsNullOrWhiteSpace(text5))
		{
			proxy["alpn"] = text5.Split(',');
		}
		if (!ValidateProxy(proxy, out var message2))
		{
			message = message2;
			proxy = null;
			return false;
		}
		return true;
	}

	private static bool TryMapSsr(string value, out Dictionary<string, object> proxy, out string message)
	{
		proxy = null;
		message = "SSR-ключ повреждён.";
		try
		{
			string text = StrictUtf8.GetString(DecodeBase64(value.Substring("ssr://".Length)));
			int num = text.IndexOf("/?", StringComparison.Ordinal);
			string text2 = ((num >= 0) ? text.Substring(0, num) : text);
			NameValueCollection nameValueCollection = HttpUtility.ParseQueryString((num >= 0) ? text.Substring(num + 1) : "");
			string[] array = text2.Split(':');
			if (array.Length != 6)
			{
				return false;
			}
			if (!int.TryParse(array[1], out var result) || result < 1 || result > 65535)
			{
				return false;
			}
			proxy = new Dictionary<string, object>
			{
				{
					"name",
					DecodeOptional(nameValueCollection["remarks"], array[0])
				},
				{ "type", "ssr" },
				{
					"server",
					array[0]
				},
				{ "port", result },
				{
					"cipher",
					array[3]
				},
				{
					"password",
					StrictUtf8.GetString(DecodeBase64(array[5]))
				},
				{
					"protocol",
					array[2]
				},
				{
					"obfs",
					array[4]
				},
				{ "udp", true }
			};
			string text3 = DecodeOptional(nameValueCollection["protoparam"], "");
			if (text3.Length > 0)
			{
				proxy["protocol-param"] = text3;
			}
			string text4 = DecodeOptional(nameValueCollection["obfsparam"], "");
			if (text4.Length > 0)
			{
				proxy["obfs-param"] = text4;
			}
			return true;
		}
		catch
		{
			proxy = null;
			return false;
		}
	}

	private static bool TryMapShadowsocks(string value, out Dictionary<string, object> proxy, out string message)
	{
		proxy = null;
		message = "Shadowsocks-ключ повреждён.";
		try
		{
			string text = value.Substring("ss://".Length);
			int num = text.IndexOf('#');
			string text2 = ((num >= 0) ? Uri.UnescapeDataString(text.Substring(num + 1)) : "");
			if (num >= 0)
			{
				text = text.Substring(0, num);
			}
			int num2 = text.IndexOf('?');
			NameValueCollection nameValueCollection = ((num2 >= 0) ? HttpUtility.ParseQueryString(text.Substring(num2)) : HttpUtility.ParseQueryString(""));
			if (num2 >= 0)
			{
				text = text.Substring(0, num2);
			}
			int num3 = text.LastIndexOf('@');
			string text3;
			string text4;
			if (num3 >= 0)
			{
				text3 = Uri.UnescapeDataString(text.Substring(0, num3));
				text4 = text.Substring(num3 + 1);
			}
			else
			{
				string text5 = StrictUtf8.GetString(DecodeBase64(text));
				num3 = text5.LastIndexOf('@');
				if (num3 < 1)
				{
					return false;
				}
				text3 = text5.Substring(0, num3);
				text4 = text5.Substring(num3 + 1);
			}
			if (text3.IndexOf(':') < 0)
			{
				text3 = StrictUtf8.GetString(DecodeBase64(text3));
			}
			int num4 = text3.IndexOf(':');
			if (num4 < 1)
			{
				return false;
			}
			if (!Uri.TryCreate("ss://x@" + text4, UriKind.Absolute, out var result) || result.Port < 1)
			{
				return false;
			}
			proxy = new Dictionary<string, object>
			{
				{
					"name",
					CleanName(string.IsNullOrWhiteSpace(text2) ? result.Host : text2)
				},
				{ "type", "ss" },
				{ "server", result.Host },
				{ "port", result.Port },
				{
					"cipher",
					text3.Substring(0, num4)
				},
				{
					"password",
					text3.Substring(num4 + 1)
				},
				{ "udp", true }
			};
			string text6 = nameValueCollection["plugin"];
			if (!string.IsNullOrWhiteSpace(text6))
			{
				string[] array = Uri.UnescapeDataString(text6).Split(';');
				proxy["plugin"] = array[0];
				Dictionary<string, object> dictionary = new Dictionary<string, object>();
				foreach (string item in array.Skip(1))
				{
					int num5 = item.IndexOf('=');
					if (num5 > 0)
					{
						dictionary[item.Substring(0, num5).Replace('_', '-')] = item.Substring(num5 + 1);
					}
					else if (item.Length > 0)
					{
						dictionary[item.Replace('_', '-')] = true;
					}
				}
				if (dictionary.Count > 0)
				{
					proxy["plugin-opts"] = dictionary;
				}
			}
			return true;
		}
		catch
		{
			proxy = null;
			return false;
		}
	}

	private static bool TryMapSingBox(Dictionary<string, object> source, out Dictionary<string, object> proxy)
	{
		proxy = null;
		if (source == null || !source.TryGetValue("type", out var value) || !source.TryGetValue("server", out var value2) || !source.TryGetValue("server_port", out var value3))
		{
			return false;
		}
		string text = Convert.ToString(value, CultureInfo.InvariantCulture).ToLowerInvariant();
		HashSet<string> hashSet = new HashSet<string>(new string[12]
		{
			"shadowsocks", "socks", "http", "vmess", "vless", "trojan", "hysteria", "hysteria2", "tuic", "wireguard",
			"anytls", "ssh"
		});
		if (!hashSet.Contains(text))
		{
			return false;
		}
		if (!int.TryParse(Convert.ToString(value3, CultureInfo.InvariantCulture), out var result))
		{
			return false;
		}
		proxy = new Dictionary<string, object>
		{
			{
				"name",
				CleanName(source.TryGetValue("tag", out var value4) ? Convert.ToString(value4, CultureInfo.InvariantCulture) : Convert.ToString(value2, CultureInfo.InvariantCulture))
			},
			{
				"type",
				(text == "shadowsocks") ? "ss" : ((text == "socks") ? "socks5" : text)
			},
			{
				"server",
				Convert.ToString(value2, CultureInfo.InvariantCulture)
			},
			{ "port", result },
			{ "udp", true }
		};
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		dictionary.Add("uuid", "uuid");
		dictionary.Add("password", "password");
		dictionary.Add("method", "cipher");
		dictionary.Add("alter_id", "alterId");
		dictionary.Add("security", "cipher");
		dictionary.Add("flow", "flow");
		dictionary.Add("network", "network");
		dictionary.Add("username", "username");
		dictionary.Add("private_key", "private-key");
		dictionary.Add("public_key", "public-key");
		dictionary.Add("peer_public_key", "public-key");
		dictionary.Add("pre_shared_key", "pre-shared-key");
		dictionary.Add("token", "token");
		dictionary.Add("up_mbps", "up");
		dictionary.Add("down_mbps", "down");
		dictionary.Add("auth", "auth-str");
		dictionary.Add("auth_str", "auth-str");
		dictionary.Add("protocol", "protocol");
		dictionary.Add("congestion_control", "congestion-controller");
		dictionary.Add("udp_relay_mode", "udp-relay-mode");
		dictionary.Add("recv_window_conn", "recv-window-conn");
		dictionary.Add("recv_window", "recv-window");
		dictionary.Add("disable_mtu_discovery", "disable-mtu-discovery");
		dictionary.Add("mtu", "mtu");
		dictionary.Add("heartbeat", "heartbeat-interval");
		dictionary.Add("obfs_password", "obfs-password");
		Dictionary<string, string> dictionary2 = dictionary;
		foreach (KeyValuePair<string, string> item in dictionary2)
		{
			if (source.TryGetValue(item.Key, out var value5) && value5 != null)
			{
				proxy[item.Value] = value5;
			}
		}
		object value6;
		Dictionary<string, object> dictionary3 = (source.TryGetValue("tls", out value6) ? (value6 as Dictionary<string, object>) : null);
		if (dictionary3 != null)
		{
			bool flag = text == "vmess" || text == "vless" || text == "trojan";
			if (flag && dictionary3.TryGetValue("enabled", out var value7))
			{
				proxy["tls"] = value7;
			}
			if (dictionary3.TryGetValue("server_name", out var value8))
			{
				proxy[flag ? "servername" : "sni"] = value8;
			}
			if (dictionary3.TryGetValue("insecure", out var value9))
			{
				proxy["skip-cert-verify"] = value9;
			}
			if (dictionary3.TryGetValue("alpn", out var value10))
			{
				proxy["alpn"] = value10;
			}
			object value11;
			Dictionary<string, object> dictionary4 = (dictionary3.TryGetValue("reality", out value11) ? (value11 as Dictionary<string, object>) : null);
			if (dictionary4 != null)
			{
				Dictionary<string, object> dictionary5 = new Dictionary<string, object>();
				if (dictionary4.TryGetValue("public_key", out var value12))
				{
					dictionary5["public-key"] = value12;
				}
				if (dictionary4.TryGetValue("short_id", out var value13))
				{
					dictionary5["short-id"] = value13;
				}
				if (dictionary5.Count > 0)
				{
					proxy["reality-opts"] = dictionary5;
				}
			}
		}
		object value14;
		Dictionary<string, object> dictionary6 = (source.TryGetValue("obfs", out value14) ? (value14 as Dictionary<string, object>) : null);
		if (dictionary6 != null)
		{
			if (dictionary6.TryGetValue("type", out var value15))
			{
				proxy["obfs"] = value15;
			}
			if (dictionary6.TryGetValue("password", out value15))
			{
				proxy["obfs-password"] = value15;
			}
		}
		else if (source.TryGetValue("obfs", out value14) && value14 is string)
		{
			proxy["obfs"] = value14;
		}
		if (text == "wireguard" && source.TryGetValue("local_address", out var value16))
		{
			List<object> list = AsList(value16);
			if (list == null)
			{
				List<object> list2 = new List<object>();
				list2.Add(value16);
				list = list2;
			}
			List<object> source2 = list;
			string[] source3 = source2.Select((object x) => Convert.ToString(x, CultureInfo.InvariantCulture).Split('/')[0]).ToArray();
			string value17 = source3.FirstOrDefault((string x) => x.IndexOf(':') < 0);
			string value18 = source3.FirstOrDefault((string x) => x.IndexOf(':') >= 0);
			if (!string.IsNullOrWhiteSpace(value17))
			{
				proxy["ip"] = value17;
			}
			if (!string.IsNullOrWhiteSpace(value18))
			{
				proxy["ipv6"] = value18;
			}
		}
		if (text == "wireguard" && source.TryGetValue("reserved", out var value19))
		{
			proxy["reserved"] = value19;
		}
		object value20;
		Dictionary<string, object> dictionary7 = (source.TryGetValue("transport", out value20) ? (value20 as Dictionary<string, object>) : null);
		if (dictionary7 != null && dictionary7.TryGetValue("type", out var value21))
		{
			string text2 = Convert.ToString(value21, CultureInfo.InvariantCulture);
			proxy["network"] = text2;
			Dictionary<string, object> dictionary8 = new Dictionary<string, object>();
			if (dictionary7.TryGetValue("path", out var value22))
			{
				dictionary8["path"] = value22;
			}
			if (dictionary7.TryGetValue("headers", out value22))
			{
				dictionary8["headers"] = value22;
			}
			if (dictionary7.TryGetValue("service_name", out value22))
			{
				dictionary8["grpc-service-name"] = value22;
			}
			if (dictionary7.TryGetValue("max_early_data", out value22))
			{
				dictionary8["max-early-data"] = value22;
			}
			if (dictionary7.TryGetValue("early_data_header_name", out value22))
			{
				dictionary8["early-data-header-name"] = value22;
			}
			if (dictionary8.Count > 0)
			{
				proxy[text2 + "-opts"] = dictionary8;
			}
		}
		string message;
		return ValidateProxy(proxy, out message);
	}

	private static bool TryMapSip008(Dictionary<string, object> source, out Dictionary<string, object> proxy)
	{
		proxy = null;
		if (source == null || !source.TryGetValue("server", out var value) || !source.TryGetValue("server_port", out var value2) || !source.TryGetValue("method", out var value3) || !source.TryGetValue("password", out var value4) || !int.TryParse(Convert.ToString(value2, CultureInfo.InvariantCulture), out var result) || result < 1 || result > 65535)
		{
			return false;
		}
		proxy = new Dictionary<string, object>
		{
			{
				"name",
				CleanName((source.TryGetValue("remarks", out var value5) || source.TryGetValue("name", out value5)) ? Convert.ToString(value5, CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture))
			},
			{ "type", "ss" },
			{
				"server",
				Convert.ToString(value, CultureInfo.InvariantCulture)
			},
			{ "port", result },
			{
				"cipher",
				Convert.ToString(value3, CultureInfo.InvariantCulture)
			},
			{
				"password",
				Convert.ToString(value4, CultureInfo.InvariantCulture)
			},
			{ "udp", true }
		};
		if (source.TryGetValue("plugin", out var value6) && !string.IsNullOrWhiteSpace(Convert.ToString(value6, CultureInfo.InvariantCulture)))
		{
			proxy["plugin"] = value6;
		}
		if (source.TryGetValue("plugin_opts", out var value7) && value7 != null)
		{
			if (value7 is string text)
			{
				Dictionary<string, object> dictionary = new Dictionary<string, object>();
				string[] array = text.Split(';');
				foreach (string text2 in array)
				{
					int num = text2.IndexOf('=');
					if (num > 0)
					{
						dictionary[text2.Substring(0, num).Replace('_', '-')] = text2.Substring(num + 1);
					}
				}
				if (dictionary.Count > 0)
				{
					proxy["plugin-opts"] = dictionary;
				}
			}
			else
			{
				proxy["plugin-opts"] = value7;
			}
		}
		return true;
	}

	private static bool HasPlugin(Dictionary<string, object> source)
	{
		if (source != null)
		{
			if (!source.TryGetValue("plugin", out var value) || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
			{
				return source.ContainsKey("plugin_opts");
			}
			return true;
		}
		return false;
	}

	private static bool ValidateProxy(Dictionary<string, object> proxy, out string message)
	{
		message = "У сервера не хватает имени, типа, адреса или порта.";
		if (proxy == null)
		{
			return false;
		}
		if (!proxy.TryGetValue("name", out var value) || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)) || !proxy.TryGetValue("type", out var value2) || string.IsNullOrWhiteSpace(Convert.ToString(value2, CultureInfo.InvariantCulture)) || !proxy.TryGetValue("server", out var value3) || string.IsNullOrWhiteSpace(Convert.ToString(value3, CultureInfo.InvariantCulture)) || !proxy.TryGetValue("port", out var value4) || !int.TryParse(Convert.ToString(value4, CultureInfo.InvariantCulture), out var result) || result < 1 || result > 65535)
		{
			return false;
		}
		if (string.Equals(Convert.ToString(value2, CultureInfo.InvariantCulture), "wireguard", StringComparison.OrdinalIgnoreCase))
		{
			object value5;
			List<object> list = (proxy.TryGetValue("ip", out value5) ? AsList(value5) : null);
			if (list != null)
			{
				string[] source = list.Select((object x) => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
				string value6 = source.FirstOrDefault((string x) => x.IndexOf(':') < 0);
				string value7 = source.FirstOrDefault((string x) => x.IndexOf(':') >= 0);
				if (!string.IsNullOrWhiteSpace(value6))
				{
					proxy["ip"] = value6;
				}
				else
				{
					proxy.Remove("ip");
				}
				if (!string.IsNullOrWhiteSpace(value7))
				{
					proxy["ipv6"] = value7;
				}
			}
		}
		return true;
	}

	private static object Normalize(object value, int depth)
	{
		if (value == null || depth > 16)
		{
			return null;
		}
		if (value is IDictionary dictionary)
		{
			Dictionary<string, object> dictionary2 = new Dictionary<string, object>(StringComparer.Ordinal);
			{
				foreach (DictionaryEntry item in dictionary)
				{
					string text = item.Key as string;
					if (!string.IsNullOrWhiteSpace(text) && !dictionary2.ContainsKey(text))
					{
						dictionary2[text] = Normalize(item.Value, depth + 1);
					}
				}
				return dictionary2;
			}
		}
		if (value is IEnumerable enumerable && !(value is string))
		{
			List<object> list = new List<object>();
			{
				foreach (object item2 in enumerable)
				{
					list.Add(Normalize(item2, depth + 1));
				}
				return list;
			}
		}
		return value;
	}

	private static List<object> AsList(object value)
	{
		if (value is List<object> collection)
		{
			return new List<object>(collection);
		}
		if (value is object[] source)
		{
			return source.ToList();
		}
		return null;
	}

	private static string UniqueName(string baseName, IEnumerable<object> items)
	{
		HashSet<string> hashSet = new HashSet<string>(from x in items
			select x as Dictionary<string, object> into x
			where x?.ContainsKey("name") ?? false
			select Convert.ToString(x["name"], CultureInfo.InvariantCulture), StringComparer.Ordinal);
		string text = baseName;
		int num = 1;
		while (hashSet.Contains(text))
		{
			text = baseName + "-" + ++num;
		}
		return text;
	}

	private static string Scheme(string value)
	{
		int num = (value ?? "").IndexOf("://", StringComparison.Ordinal);
		if (num > 0)
		{
			return value.Substring(0, num).ToLowerInvariant();
		}
		return "";
	}

	private static string FragmentName(Uri uri, string fallback)
	{
		return CleanName((uri.Fragment.Length > 1) ? Uri.UnescapeDataString(uri.Fragment.Substring(1)) : fallback);
	}

	private static string CleanName(string value)
	{
		value = new string((value ?? "сервер").Where((char c) => !char.IsControl(c)).ToArray()).Trim();
		if (value.Length != 0)
		{
			if (value.Length > 80)
			{
				return value.Substring(0, 80);
			}
			return value;
		}
		return "сервер";
	}

	private static string First(NameValueCollection q, params string[] names)
	{
		foreach (string name in names)
		{
			if (!string.IsNullOrWhiteSpace(q[name]))
			{
				return q[name];
			}
		}
		return "";
	}

	private static void CopyQuery(NameValueCollection q, Dictionary<string, object> target, string targetName, params string[] sourceNames)
	{
		string value = First(q, sourceNames);
		if (!string.IsNullOrWhiteSpace(value))
		{
			target[targetName] = value;
		}
	}

	private static bool ParseBool(string value)
	{
		if (!(value == "1") && !value.Equals("true", StringComparison.OrdinalIgnoreCase))
		{
			return value.Equals("yes", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static string DecodeOptional(string value, string fallback)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}
		try
		{
			return CleanName(StrictUtf8.GetString(DecodeBase64(value)));
		}
		catch
		{
			return fallback;
		}
	}

	private static byte[] DecodeBase64(string value)
	{
		value = (value ?? "").Trim().Replace('-', '+').Replace('_', '/');
		while (value.Length % 4 != 0)
		{
			value += "=";
		}
		return Convert.FromBase64String(value);
	}
}
