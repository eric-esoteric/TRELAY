using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TRELAY;

internal static class ProviderConnectionProbe
{
	public static ProviderConnectionObservation Read(ProviderDefinition provider, DateTime since)
	{
		ProviderConnectionObservation providerConnectionObservation = new ProviderConnectionObservation();
		providerConnectionObservation.State = ProviderConnectionState.Unknown;
		providerConnectionObservation.Detail = "нет свежего ответа приложения";
		ProviderConnectionObservation result = providerConnectionObservation;
		if (provider == null || !provider.CanVerifyConnection)
		{
			return result;
		}
		DateTime latest = DateTime.MinValue;
		foreach (string logPath in provider.LogPaths)
		{
			ReadLog(ProviderCatalog.ExpandPath(logPath), provider, since, ref latest, result);
		}
		return result;
	}

	private static void ReadLog(string path, ProviderDefinition provider, DateTime since, ref DateTime latest, ProviderConnectionObservation result)
	{
		if (!File.Exists(path))
		{
			return;
		}
		string text;
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			int num = (int)Math.Min(fileStream.Length, 2097152L);
			fileStream.Position = fileStream.Length - num;
			byte[] array = new byte[num];
			int count = fileStream.Read(array, 0, num);
			text = Encoding.UTF8.GetString(array, 0, count);
		}
		catch
		{
			return;
		}
		string[] array2 = text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text2 in array2)
		{
			if (text2.Length >= 23 && DateTime.TryParseExact(text2.Substring(0, 23), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var result2) && !(result2 < since) && !(result2 < latest))
			{
				string line = text2.ToLowerInvariant();
				if (provider.SuccessPatterns.Any((string x) => line.Contains((x ?? "").ToLowerInvariant())))
				{
					latest = result2;
					result.ObservedAt = result2;
					result.State = ProviderConnectionState.Connected;
					result.Detail = provider.DisplayName + ": соединение установлено";
				}
				else if (provider.FailurePatterns.Any((string x) => line.Contains((x ?? "").ToLowerInvariant())))
				{
					latest = result2;
					result.ObservedAt = result2;
					result.State = ProviderConnectionState.Disconnected;
					result.Detail = (line.Contains("timeout") ? "тайм-аут подключения" : "соединение разорвано");
				}
			}
		}
	}
}
