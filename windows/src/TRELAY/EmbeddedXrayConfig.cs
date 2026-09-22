using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TRELAY;

internal static class EmbeddedXrayConfig
{
	internal const string Prefix = "xrayjson://";

	private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
	{
		MaxJsonLength = 16777216
	};

	internal static string Encode(Dictionary<string, object> root)
	{
		if (!Validate(root, out var message))
		{
			throw new FormatException(message);
		}
		string s;
		lock (Json)
		{
			s = Json.Serialize(root);
		}
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		try
		{
			string text = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-')
				.Replace('/', '_');
			if ("xrayjson://".Length + text.Length > 65536)
			{
				throw new FormatException("Xray-конфиг превышает допустимый размер.");
			}
			return "xrayjson://" + text;
		}
		finally
		{
			Array.Clear(bytes, 0, bytes.Length);
		}
	}

	internal static bool TryDecode(string value, out Dictionary<string, object> root, out string message)
	{
		root = null;
		value = (value ?? "").Trim();
		if (!value.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase))
		{
			message = "Встроенный Xray-конфиг повреждён.";
			return false;
		}
		try
		{
			string text = value.Substring("xrayjson://".Length).Replace('-', '+').Replace('_', '/');
			while (text.Length % 4 != 0)
			{
				text += "=";
			}
			byte[] array = Convert.FromBase64String(text);
			try
			{
				string input = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(array);
				lock (Json)
				{
					root = Json.DeserializeObject(input) as Dictionary<string, object>;
				}
			}
			finally
			{
				Array.Clear(array, 0, array.Length);
			}
			return Validate(root, out message);
		}
		catch
		{
			root = null;
			message = "Встроенный Xray-конфиг повреждён.";
			return false;
		}
	}

	internal static bool Validate(Dictionary<string, object> root, out string message)
	{
		object value;
		object[] array = ((root != null && root.TryGetValue("outbounds", out value)) ? (value as object[]) : null);
		if (array == null || array.Length == 0)
		{
			message = "Во встроенном Xray-конфиге нет выходного подключения.";
			return false;
		}
		bool flag = false;
		object[] array2 = array;
		foreach (object obj in array2)
		{
			object value2;
			string text = ((obj is Dictionary<string, object> dictionary && dictionary.TryGetValue("protocol", out value2)) ? Convert.ToString(value2, CultureInfo.InvariantCulture).Trim() : "");
			if (text.Length == 0 || text.Length > 40 || text.Any((char c) => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
			{
				message = "Во встроенном Xray-конфиге повреждён список выходных подключений.";
				return false;
			}
			if (!new string[4] { "freedom", "blackhole", "dns", "loopback" }.Contains(text, StringComparer.OrdinalIgnoreCase))
			{
				flag = true;
			}
		}
		if (!flag)
		{
			message = "Во встроенном Xray-конфиге нет VPN-подключения.";
			return false;
		}
		message = "Встроенный Xray-конфиг проверен.";
		return true;
	}

	internal static string GetName(string value)
	{
		if (!TryDecode(value, out var root, out var _))
		{
			return "";
		}
		if (!root.TryGetValue("remarks", out var value2))
		{
			return "";
		}
		string text = new string((from c in Convert.ToString(value2, CultureInfo.InvariantCulture)
			where !char.IsControl(c)
			select c).ToArray()).Trim();
		if (text.Length > 80)
		{
			return text.Substring(0, 80).Trim();
		}
		return text;
	}

	internal static string GetEndpoint(string value)
	{
		if (!TryDecode(value, out var root, out var _))
		{
			return "конфигурация Xray";
		}
		object[] array = (object[])root["outbounds"];
		object[] array2 = array;
		foreach (object obj in array2)
		{
			object value2;
			Dictionary<string, object> dictionary = ((obj is Dictionary<string, object> dictionary2 && dictionary2.TryGetValue("settings", out value2)) ? (value2 as Dictionary<string, object>) : null);
			if (dictionary == null)
			{
				continue;
			}
			string[] array3 = new string[2] { "vnext", "servers" };
			foreach (string key in array3)
			{
				object value3;
				object[] array4 = (dictionary.TryGetValue(key, out value3) ? (value3 as object[]) : null);
				Dictionary<string, object> dictionary3 = ((array4 != null && array4.Length > 0) ? (array4[0] as Dictionary<string, object>) : null);
				if (dictionary3 != null && dictionary3.TryGetValue("address", out var value4) && dictionary3.TryGetValue("port", out var value5))
				{
					string text = Convert.ToString(value4, CultureInfo.InvariantCulture).Trim();
					if (text.Length > 0 && int.TryParse(Convert.ToString(value5, CultureInfo.InvariantCulture), out var result) && result > 0 && result <= 65535)
					{
						return text + ":" + result.ToString(CultureInfo.InvariantCulture);
					}
				}
			}
		}
		return "конфигурация Xray";
	}

	internal static Dictionary<string, object> PrepareForSocks(string value, int socksPort)
	{
		if (!TryDecode(value, out var root, out var message))
		{
			throw new FormatException(message);
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		string[] array = new string[9] { "dns", "routing", "outbounds", "policy", "transport", "observatory", "burstObservatory", "fakedns", "fakeDns" };
		foreach (string key in array)
		{
			if (root.TryGetValue(key, out var value2))
			{
				dictionary[key] = value2;
			}
		}
		string value3 = "trelay-socks";
		object value4;
		object[] array2 = (root.TryGetValue("inbounds", out value4) ? (value4 as object[]) : null);
		Dictionary<string, object> dictionary2 = ((array2 != null && array2.Length > 0) ? (array2[0] as Dictionary<string, object>) : null);
		object value5;
		string text = ((dictionary2 != null && dictionary2.TryGetValue("tag", out value5)) ? Convert.ToString(value5, CultureInfo.InvariantCulture) : "");
		if (!string.IsNullOrWhiteSpace(text) && text.Length <= 64 && text.All((char c) => !char.IsControl(c)))
		{
			value3 = text;
		}
		dictionary["log"] = new Dictionary<string, object> { { "loglevel", "warning" } };
		dictionary["inbounds"] = new object[1]
		{
			new Dictionary<string, object>
			{
				{ "tag", value3 },
				{ "listen", "127.0.0.1" },
				{ "port", socksPort },
				{ "protocol", "socks" },
				{
					"settings",
					new Dictionary<string, object> { { "udp", true } }
				}
			}
		};
		return dictionary;
	}
}
