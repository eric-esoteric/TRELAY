using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;

namespace TRELAY;

internal static class ConnectionValidator
{
	internal const int MaxKeyLength = 65536;

	private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
	{
		MaxJsonLength = 16777216
	};

	public static string SuggestProfileName(string value)
	{
		value = (value ?? "").Trim();
		if (value.Length == 0 || value.Length > 65536)
		{
			return "";
		}
		try
		{
			if (value.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase))
			{
				return EmbeddedXrayConfig.GetName(value);
			}
			if (value.StartsWith("trelay-mihomo://", StringComparison.OrdinalIgnoreCase))
			{
				return MihomoConfig.GetName(value);
			}
			string encoded;
			string message;
			if (MihomoConfig.IsDirectLink(value))
			{
				return MihomoConfig.TryEncodeDirect(value, out encoded, out message) ? MihomoConfig.GetName(encoded) : "";
			}
			if (value.StartsWith("key://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				value = ShareLinkResolver.Resolve(value);
			}
			if (value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
			{
				string text = value.Substring("vmess://".Length).Replace('-', '+').Replace('_', '/');
				while (text.Length % 4 != 0)
				{
					text += "=";
				}
				Dictionary<string, object> dictionary;
				lock (Json)
				{
					dictionary = Json.DeserializeObject(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(text))) as Dictionary<string, object>;
				}
				object value2;
				return CleanProfileName((dictionary != null && dictionary.TryGetValue("ps", out value2)) ? Convert.ToString(value2, CultureInfo.InvariantCulture) : "");
			}
			if (!Uri.TryCreate(value, UriKind.Absolute, out var result))
			{
				return "";
			}
			string value3 = ((result.Fragment.Length > 1) ? Uri.UnescapeDataString(result.Fragment.Substring(1)) : "");
			value3 = CleanProfileName(value3);
			return (value3.Length > 0) ? value3 : CleanProfileName(result.Host);
		}
		catch
		{
			return "";
		}
	}

	private static string CleanProfileName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		string text = new string(value.Where((char c) => !char.IsControl(c)).ToArray()).Trim();
		if (text.Length > 80)
		{
			return text.Substring(0, 80).Trim();
		}
		return text;
	}

	public static bool ValidateKey(string value, out string kind, out string message)
	{
		kind = "";
		value = (value ?? "").Trim();
		if (value.Length == 0)
		{
			message = "Вставь ключ или ссылку подключения.";
			return false;
		}
		if (value.Length > 65536)
		{
			message = "Ключ превышает допустимый размер (64 КБ).";
			return false;
		}
		string text = value;
		foreach (char c in text)
		{
			if (char.IsControl(c))
			{
				message = "Ключ содержит недопустимые управляющие символы.";
				return false;
			}
		}
		if (value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
		{
			return ValidateVmess(value, out kind, out message);
		}
		if (value.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
		{
			return ValidateShadowsocks(value, out kind, out message);
		}
		if (value.StartsWith("key://", StringComparison.OrdinalIgnoreCase))
		{
			kind = "key";
			return ShareLinkResolver.ValidateKeyContainer(value, out message);
		}
		if (value.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
		{
			kind = "ssconf";
			return ShareLinkResolver.ValidateSsconf(value, out message);
		}
		if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			kind = "subscription";
			return ShareLinkResolver.ValidateHttpsSubscription(value, out message);
		}
		if (value.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase))
		{
			kind = "xray";
			Dictionary<string, object> root;
			return EmbeddedXrayConfig.TryDecode(value, out root, out message);
		}
		if (value.StartsWith("trelay-mihomo://", StringComparison.OrdinalIgnoreCase))
		{
			kind = "mihomo";
			Dictionary<string, object> pack;
			return MihomoConfig.TryDecode(value, out pack, out message);
		}
		if (MihomoConfig.IsDirectLink(value))
		{
			kind = "mihomo";
			string encoded;
			return MihomoConfig.TryEncodeDirect(value, out encoded, out message);
		}
		if (!Uri.TryCreate(value, UriKind.Absolute, out var result))
		{
			message = "Ссылка повреждена или обрезана.";
			return false;
		}
		switch (result.Scheme.ToLowerInvariant())
		{
		case "vless":
			if (!IsValidVlessId(Uri.UnescapeDataString(result.UserInfo)))
			{
				message = "В VLESS-ключе некорректный идентификатор клиента.";
				return false;
			}
			if (!HasServer(result))
			{
				message = "В VLESS-ключе нет сервера или порта.";
				return false;
			}
			if (!ValidateTransport(result, out message))
			{
				return false;
			}
			kind = "vless";
			message = "VLESS-ключ проверен.";
			return true;
		case "trojan":
			if (string.IsNullOrWhiteSpace(result.UserInfo))
			{
				message = "В Trojan-ключе нет пароля.";
				return false;
			}
			if (!HasServer(result))
			{
				message = "В Trojan-ключе нет сервера или порта.";
				return false;
			}
			if (!ValidateTransport(result, out message))
			{
				return false;
			}
			kind = "trojan";
			message = "Trojan-ключ проверен.";
			return true;
		case "socks":
		case "socks5":
		{
			if (!HasServer(result))
			{
				message = "В SOCKS5-ссылке нет сервера или порта.";
				return false;
			}
			string text2 = Uri.UnescapeDataString(result.UserInfo ?? "");
			if (text2.Length > 0 && (text2.IndexOf(':') < 1 || text2.EndsWith(":", StringComparison.Ordinal)))
			{
				message = "В SOCKS5-ссылке некорректные логин или пароль.";
				return false;
			}
			kind = "socks5";
			message = "SOCKS5-ссылка проверена.";
			return true;
		}
		default:
			message = "Поддерживаются VLESS, VMess, Trojan, Shadowsocks, SOCKS5, Hysteria 2, TUIC, SSR, WireGuard, AnyTLS, KEY, SSCONF, HTTPS-подписки, Clash, sing-box и Xray.";
			return false;
		}
	}

	private static bool IsValidVlessId(string value)
	{
		value = value ?? "";
		if (value.Length < 1 || value.Length > 128)
		{
			return false;
		}
		string text = value;
		foreach (char c in text)
		{
			if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.' && c != '~')
			{
				return false;
			}
		}
		return true;
	}

	private static bool ValidateShadowsocks(string value, out string kind, out string message)
	{
		kind = "";
		try
		{
			if (MihomoConfig.IsDirectLink(value))
			{
				kind = "mihomo";
				string encoded;
				return MihomoConfig.TryEncodeDirect(value, out encoded, out message);
			}
			Uri uri = new Uri(value);
			if (!HasServer(uri))
			{
				message = "В Shadowsocks-ключе нет сервера или порта.";
				return false;
			}
			string text = ShareLinkConverter.DecodeShadowsocksCredentials(uri.UserInfo);
			int num = text.IndexOf(':');
			if (num < 1 || num == text.Length - 1)
			{
				message = "В Shadowsocks-ключе нет метода или пароля.";
				return false;
			}
			kind = "ss2022";
			message = "Shadowsocks TCP/UDP ключ проверен.";
			return true;
		}
		catch
		{
			message = "Shadowsocks-ключ повреждён или обрезан.";
			return false;
		}
	}

	public static bool ValidateFile(string path, out string message)
	{
		string kind;
		return ValidateFile(path, out kind, out message);
	}

	public static bool ValidateFile(string path, out string kind, out string message)
	{
		kind = "";
		path = (path ?? "").Trim();
		if (!File.Exists(path))
		{
			message = "Выбранный файл не существует.";
			return false;
		}
		try
		{
			if (new FileInfo(path).Length > 16777216)
			{
				message = "Xray-конфиг превышает допустимый размер (16 МБ).";
				return false;
			}
			string text = File.ReadAllText(path);
			Dictionary<string, object> dictionary = null;
			object obj = null;
			try
			{
				lock (Json)
				{
					obj = Json.DeserializeObject(text);
					dictionary = obj as Dictionary<string, object>;
				}
			}
			catch
			{
			}
			if (dictionary == null || !dictionary.TryGetValue("outbounds", out var value))
			{
				if (MihomoConfig.TryExtractClash(text, out var links) || (obj != null && (MihomoConfig.TryExtractSingBox(obj, out links) || MihomoConfig.TryExtractSip008(obj, out links))))
				{
					kind = "mihomo";
					message = "Clash/sing-box конфиг проверен; будет использован первый сервер.";
					return true;
				}
				message = "Это не Xray, Clash или sing-box конфиг с VPN-серверами.";
				return false;
			}
			if (!(value is object[] array) || array.Length == 0)
			{
				message = "В Xray-конфиге нет выходного подключения.";
				return false;
			}
			if (array.Any((object item) => item is Dictionary<string, object> dictionary3 && dictionary3.ContainsKey("type") && !dictionary3.ContainsKey("protocol")) && MihomoConfig.TryExtractSingBox(obj, out var _))
			{
				kind = "mihomo";
				message = "sing-box конфиг проверен; будет использован первый сервер.";
				return true;
			}
			object[] array2 = array;
			foreach (object obj3 in array2)
			{
				if (!(obj3 is Dictionary<string, object> dictionary2) || !dictionary2.TryGetValue("protocol", out var value2) || !(value2 is string) || string.IsNullOrWhiteSpace((string)value2))
				{
					message = "В Xray-конфиге повреждена секция outbounds: у каждого подключения должен быть protocol.";
					return false;
				}
			}
			kind = "xray";
			message = "Xray-конфиг проверен.";
			return true;
		}
		catch (Exception ex)
		{
			message = ((ex is UnauthorizedAccessException || ex is IOException) ? "Не удалось прочитать конфиг: проверь доступ к файлу." : "Не удалось прочитать конфиг: некорректный JSON или структура файла.");
			return false;
		}
	}

	private static bool HasServer(Uri uri)
	{
		if (!string.IsNullOrWhiteSpace(uri.Host) && uri.Port > 0)
		{
			return uri.Port <= 65535;
		}
		return false;
	}

	private static bool ValidateTransport(Uri uri, out string message)
	{
		return ShareLinkConverter.ValidateTransport(HttpUtility.ParseQueryString(uri.Query), out message);
	}

	private static bool ValidateVmess(string value, out string kind, out string message)
	{
		kind = "";
		try
		{
			string text = value.Substring("vmess://".Length).Trim();
			text = text.Replace('-', '+').Replace('_', '/');
			while (text.Length % 4 != 0)
			{
				text += "=";
			}
			string input = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(text));
			Dictionary<string, object> dictionary;
			lock (Json)
			{
				dictionary = Json.DeserializeObject(input) as Dictionary<string, object>;
			}
			if (dictionary == null || !dictionary.ContainsKey("id") || !dictionary.ContainsKey("add") || !dictionary.ContainsKey("port"))
			{
				message = "В VMess-ключе не хватает id, сервера или порта.";
				return false;
			}
			if (!(dictionary["id"] is string) || !Guid.TryParse((string)dictionary["id"], out var _))
			{
				message = "В VMess-ключе некорректный UUID.";
				return false;
			}
			string text2 = dictionary["add"] as string;
			if (string.IsNullOrWhiteSpace(text2) || Uri.CheckHostName(text2) == UriHostNameType.Unknown || dictionary["port"] == null || !int.TryParse(Convert.ToString(dictionary["port"], CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var result2) || result2 < 1 || result2 > 65535)
			{
				message = "В VMess-ключе некорректный сервер или порт.";
				return false;
			}
			if (dictionary.TryGetValue("aid", out var value2) && value2 != null && (!int.TryParse(Convert.ToString(value2, CultureInfo.InvariantCulture), out var result3) || result3 < 0))
			{
				message = "В VMess-ключе некорректный alterId.";
				return false;
			}
			NameValueCollection nameValueCollection = HttpUtility.ParseQueryString("");
			if (dictionary.TryGetValue("net", out var value3))
			{
				nameValueCollection["type"] = value3 as string;
			}
			if (dictionary.TryGetValue("tls", out var value4))
			{
				nameValueCollection["security"] = value4 as string;
			}
			if (!ShareLinkConverter.ValidateTransport(nameValueCollection, out message))
			{
				return false;
			}
			kind = "vmess";
			message = "VMess-ключ проверен.";
			return true;
		}
		catch
		{
			message = "VMess-ключ повреждён или обрезан.";
			return false;
		}
	}
}
