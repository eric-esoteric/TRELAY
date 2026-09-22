using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TRELAY;

internal static class ProviderTrafficObserver
{
	private static readonly Regex Destination = new Regex("(?:tcp:|udp:)?(?<host>[a-z0-9][a-z0-9.-]*\\.[a-z]{2,})(?=:\\d{1,5}\\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(150.0));

	internal static string AccessLogPath(SettingsStore store, TunnelProfile profile)
	{
		if (store == null || profile == null || !SecretStore.IsValidProfileId(profile.Id))
		{
			throw new ArgumentException("Некорректный идентификатор профиля.");
		}
		return Path.Combine(store.StateDirectory, "runtime", profile.Id + ".access.log");
	}

	public static IEnumerable<string> ReadHosts(SettingsStore store, TunnelProfile profile)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (store == null || profile == null || !SecretStore.IsValidProfileId(profile.Id))
		{
			return hashSet;
		}
		string path = AccessLogPath(store, profile);
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long length = fileStream.Length;
			int num = (int)Math.Min(length, 262144L);
			fileStream.Position = length - num;
			byte[] array = new byte[num];
			int i;
			int num2;
			for (i = 0; i < num; i += num2)
			{
				num2 = fileStream.Read(array, i, num - i);
				if (num2 == 0)
				{
					break;
				}
			}
			string text = Encoding.UTF8.GetString(array, 0, i);
			if (length > num)
			{
				int num3 = text.IndexOf('\n');
				text = ((num3 < 0) ? "" : text.Substring(num3 + 1));
			}
			foreach (Match item in Destination.Matches(text))
			{
				string value = item.Groups["host"].Value;
				if (value.Length <= 253 && Uri.CheckHostName(value) == UriHostNameType.Dns)
				{
					hashSet.Add(value.ToLowerInvariant());
				}
				if (hashSet.Count >= 256)
				{
					break;
				}
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		catch (RegexMatchTimeoutException)
		{
		}
		return hashSet;
	}
}
