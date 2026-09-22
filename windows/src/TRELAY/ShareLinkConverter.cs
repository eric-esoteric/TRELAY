using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;

namespace TRELAY;

internal static class ShareLinkConverter
{
	private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

	public static Dictionary<string, object> Build(string key, int socksPort)
	{
		key = (key ?? "").Trim();
		if (socksPort < 1025 || socksPort > 65535)
		{
			throw new ArgumentOutOfRangeException("socksPort", "Локальный SOCKS-порт должен быть от 1025 до 65535.");
		}
		if (!ConnectionValidator.ValidateKey(key, out var kind, out var message))
		{
			throw new FormatException(message);
		}
		if (key.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase))
		{
			return EmbeddedXrayConfig.PrepareForSocks(key, socksPort);
		}
		key = ShareLinkResolver.Resolve(key);
		if (!ConnectionValidator.ValidateKey(key, out kind, out message))
		{
			throw new ShareLinkResolutionException("Полученная конфигурация VPN повреждена: " + message);
		}
		Dictionary<string, object> dictionary;
		if (key.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
		{
			dictionary = BuildVmess(key);
		}
		else
		{
			Uri uri = new Uri(key);
			if (uri.Scheme.Equals("vless", StringComparison.OrdinalIgnoreCase))
			{
				dictionary = BuildVless(uri);
			}
			else if (uri.Scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase))
			{
				dictionary = BuildTrojan(uri);
			}
			else if (uri.Scheme.Equals("ss", StringComparison.OrdinalIgnoreCase))
			{
				dictionary = BuildShadowsocks(uri);
			}
			else
			{
				if (!uri.Scheme.Equals("socks", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
				{
					throw new NotSupportedException("Этот тип ключа пока не поддерживается движком.");
				}
				dictionary = BuildSocks(uri);
			}
		}
		Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
		dictionary2.Add("log", new Dictionary<string, object> { { "loglevel", "warning" } });
		dictionary2.Add("inbounds", new object[1] { SocksInbound(socksPort) });
		dictionary2.Add("outbounds", new object[1] { dictionary });
		return dictionary2;
	}

	internal static string DecodeShadowsocksCredentials(string userInfo)
	{
		string text = Uri.UnescapeDataString(userInfo ?? "");
		if (text.IndexOf(':') >= 0)
		{
			return text;
		}
		text = text.Replace('-', '+').Replace('_', '/');
		while (text.Length % 4 != 0)
		{
			text += "=";
		}
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(text));
	}

	private static Dictionary<string, object> BuildShadowsocks(Uri uri)
	{
		string text = DecodeShadowsocksCredentials(uri.UserInfo);
		int num = text.IndexOf(':');
		if (num < 1)
		{
			throw new FormatException("Некорректный Shadowsocks-ключ.");
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("protocol", "shadowsocks");
		dictionary.Add("settings", new Dictionary<string, object> { 
		{
			"servers",
			new object[1]
			{
				new Dictionary<string, object>
				{
					{ "address", uri.Host },
					{ "port", uri.Port },
					{
						"method",
						text.Substring(0, num)
					},
					{
						"password",
						text.Substring(num + 1)
					}
				}
			}
		} });
		return dictionary;
	}

	private static Dictionary<string, object> BuildSocks(Uri uri)
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("address", uri.Host);
		dictionary.Add("port", uri.Port);
		Dictionary<string, object> dictionary2 = dictionary;
		string text = Uri.UnescapeDataString(uri.UserInfo ?? "");
		if (text.Length > 0)
		{
			int num = text.IndexOf(':');
			dictionary2["users"] = new object[1]
			{
				new Dictionary<string, object>
				{
					{
						"user",
						text.Substring(0, num)
					},
					{
						"pass",
						text.Substring(num + 1)
					}
				}
			};
		}
		Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
		dictionary3.Add("protocol", "socks");
		dictionary3.Add("settings", new Dictionary<string, object> { 
		{
			"servers",
			new object[1] { dictionary2 }
		} });
		return dictionary3;
	}

	private static Dictionary<string, object> SocksInbound(int port)
	{
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("listen", "127.0.0.1");
		dictionary.Add("port", port);
		dictionary.Add("protocol", "socks");
		dictionary.Add("settings", new Dictionary<string, object> { { "udp", true } });
		return dictionary;
	}

	private static Dictionary<string, object> BuildVless(Uri uri)
	{
		NameValueCollection nameValueCollection = HttpUtility.ParseQueryString(uri.Query);
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("id", Uri.UnescapeDataString(uri.UserInfo));
		dictionary.Add("encryption", Empty(nameValueCollection["encryption"], "none"));
		Dictionary<string, object> dictionary2 = dictionary;
		if (!string.IsNullOrWhiteSpace(nameValueCollection["flow"]))
		{
			dictionary2["flow"] = nameValueCollection["flow"];
		}
		Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
		dictionary3.Add("protocol", "vless");
		dictionary3.Add("settings", new Dictionary<string, object> { 
		{
			"vnext",
			new object[1]
			{
				new Dictionary<string, object>
				{
					{ "address", uri.Host },
					{ "port", uri.Port },
					{
						"users",
						new object[1] { dictionary2 }
					}
				}
			}
		} });
		dictionary3.Add("streamSettings", StreamSettings(nameValueCollection, uri.Host));
		return dictionary3;
	}

	private static Dictionary<string, object> BuildTrojan(Uri uri)
	{
		NameValueCollection nameValueCollection = HttpUtility.ParseQueryString(uri.Query);
		if (string.IsNullOrWhiteSpace(nameValueCollection["security"]))
		{
			nameValueCollection["security"] = "tls";
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("protocol", "trojan");
		dictionary.Add("settings", new Dictionary<string, object> { 
		{
			"servers",
			new object[1]
			{
				new Dictionary<string, object>
				{
					{ "address", uri.Host },
					{ "port", uri.Port },
					{
						"password",
						Uri.UnescapeDataString(uri.UserInfo)
					}
				}
			}
		} });
		dictionary.Add("streamSettings", StreamSettings(nameValueCollection, uri.Host));
		return dictionary;
	}

	private static Dictionary<string, object> BuildVmess(string key)
	{
		string text = key.Substring("vmess://".Length).Trim().Replace('-', '+')
			.Replace('_', '/');
		while (text.Length % 4 != 0)
		{
			text += "=";
		}
		Dictionary<string, object> d;
		lock (Json)
		{
			d = Json.DeserializeObject(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(text))) as Dictionary<string, object>;
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("id", S(d, "id"));
		dictionary.Add("alterId", I(d, "aid", 0));
		dictionary.Add("security", Empty(S(d, "scy"), "auto"));
		Dictionary<string, object> dictionary2 = dictionary;
		NameValueCollection nameValueCollection = HttpUtility.ParseQueryString("");
		nameValueCollection["type"] = Empty(S(d, "net"), "tcp");
		nameValueCollection["security"] = S(d, "tls");
		nameValueCollection["sni"] = S(d, "sni");
		nameValueCollection["host"] = S(d, "host");
		nameValueCollection["path"] = S(d, "path");
		if (nameValueCollection["type"] == "grpc")
		{
			nameValueCollection["serviceName"] = S(d, "path");
		}
		Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
		dictionary3.Add("protocol", "vmess");
		dictionary3.Add("settings", new Dictionary<string, object> { 
		{
			"vnext",
			new object[1]
			{
				new Dictionary<string, object>
				{
					{
						"address",
						S(d, "add")
					},
					{
						"port",
						int.Parse(S(d, "port"), CultureInfo.InvariantCulture)
					},
					{
						"users",
						new object[1] { dictionary2 }
					}
				}
			}
		} });
		dictionary3.Add("streamSettings", StreamSettings(nameValueCollection, S(d, "add")));
		return dictionary3;
	}

	private static Dictionary<string, object> StreamSettings(NameValueCollection q, string fallbackServer)
	{
		string text = Empty(q["type"], "tcp");
		string text2 = Empty(q["security"], "none");
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("network", text);
		dictionary.Add("security", text2);
		Dictionary<string, object> dictionary2 = dictionary;
		string text3 = Empty(q["sni"], fallbackServer);
		if (text2 == "tls")
		{
			dictionary2["tlsSettings"] = new Dictionary<string, object>
			{
				{ "serverName", text3 },
				{
					"fingerprint",
					Empty(q["fp"], "chrome")
				}
			};
		}
		if (text2 == "reality")
		{
			dictionary2["realitySettings"] = new Dictionary<string, object>
			{
				{ "serverName", text3 },
				{
					"fingerprint",
					Empty(q["fp"], "chrome")
				},
				{
					"publicKey",
					q["pbk"] ?? ""
				},
				{
					"shortId",
					q["sid"] ?? ""
				},
				{
					"spiderX",
					q["spx"] ?? ""
				}
			};
		}
		if (text == "ws")
		{
			dictionary2["wsSettings"] = new Dictionary<string, object>
			{
				{
					"path",
					Empty(q["path"], "/")
				},
				{
					"headers",
					new Dictionary<string, object> { 
					{
						"Host",
						Empty(q["host"], text3)
					} }
				}
			};
		}
		if (text == "grpc")
		{
			dictionary2["grpcSettings"] = new Dictionary<string, object>
			{
				{
					"serviceName",
					q["serviceName"] ?? ""
				},
				{
					"multiMode",
					q["mode"] == "multi"
				}
			};
		}
		return dictionary2;
	}

	internal static bool ValidateTransport(NameValueCollection q, out string message)
	{
		string text = Empty(q["type"], "tcp");
		string text2 = Empty(q["security"], "none");
		if (text != "tcp" && text != "raw" && text != "ws" && text != "grpc")
		{
			message = "Этот транспорт ссылки пока не поддерживается. Импортируй полный Xray-конфиг.";
			return false;
		}
		if (text2 != "none" && text2 != "tls" && text2 != "reality")
		{
			message = "В ключе указан неподдерживаемый тип защиты соединения.";
			return false;
		}
		if (text2 == "reality")
		{
			if (string.IsNullOrWhiteSpace(q["sni"]) || string.IsNullOrWhiteSpace(q["pbk"]))
			{
				message = "В REALITY-ключе не хватает SNI или открытого ключа сервера.";
				return false;
			}
			string text3 = q["sid"] ?? "";
			if (text3.Length > 16 || text3.Length % 2 != 0)
			{
				message = "В REALITY-ключе некорректный shortId.";
				return false;
			}
			string text4 = text3;
			foreach (char character in text4)
			{
				if (!Uri.IsHexDigit(character))
				{
					message = "В REALITY-ключе некорректный shortId.";
					return false;
				}
			}
		}
		message = "";
		return true;
	}

	private static string S(Dictionary<string, object> d, string key)
	{
		if (d == null || !d.TryGetValue(key, out var value) || value == null)
		{
			return "";
		}
		return value.ToString();
	}

	private static int I(Dictionary<string, object> d, string key, int fallback)
	{
		if (!int.TryParse(S(d, key), out var result))
		{
			return fallback;
		}
		return result;
	}

	private static string Empty(string value, string fallback)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		return fallback;
	}
}
