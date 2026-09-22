using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TRELAY;

internal sealed class SecretStore
{
	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WebHunterAI/profile-key/v1");

	private readonly string directory;

	private readonly string stateDirectory;

	public string RecoveryMessage { get; private set; }

	public SecretStore(SettingsStore settings)
	{
		stateDirectory = settings.StateDirectory;
		directory = Path.Combine(stateDirectory, "secrets");
		Directory.CreateDirectory(directory);
	}

	public bool Exists(string profileId)
	{
		lock (SettingsStore.IoGate)
		{
			string text = PathFor(profileId);
			return File.Exists(text) || File.Exists(text + ".bak");
		}
	}

	public void Save(string profileId, string value)
	{
		if (!IsValidProfileId(profileId))
		{
			throw new ArgumentException("Некорректный идентификатор профиля.");
		}
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException("Connection key is empty.");
		}
		if (value.Length > 65536)
		{
			throw new ArgumentException("Connection key is too large.");
		}
		byte[] bytes = Encoding.UTF8.GetBytes(value.Trim());
		try
		{
			byte[] value2 = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
			lock (SettingsStore.IoGate)
			{
				using (SettingsStore.AcquireWriteLock(stateDirectory))
				{
					Directory.CreateDirectory(directory);
					string text = PathFor(profileId);
					byte[] clear;
					bool flag = TryDecrypt(text, out clear);
					if (clear != null)
					{
						Array.Clear(clear, 0, clear.Length);
					}
					SettingsStore.WriteAtomic(text, value2, flag ? (text + ".bak") : null);
				}
			}
		}
		finally
		{
			Array.Clear(bytes, 0, bytes.Length);
		}
	}

	public string Load(string profileId)
	{
		lock (SettingsStore.IoGate)
		{
			using (SettingsStore.AcquireWriteLock(stateDirectory))
			{
				string text = PathFor(profileId);
				RecoveryMessage = null;
				if (!TryDecrypt(text, out var clear))
				{
					if (!TryDecrypt(text + ".bak", out clear))
					{
						if (!File.Exists(text) && !File.Exists(text + ".bak"))
						{
							return null;
						}
						throw new CryptographicException("Не удалось расшифровать сохранённый ключ и его резервную копию. Вставь ключ заново в этом профиле.");
					}
					RecoveryMessage = "Ключ маршрута восстановлен из резервной копии. Проверь подключение.";
				}
				try
				{
					return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(clear);
				}
				finally
				{
					Array.Clear(clear, 0, clear.Length);
				}
			}
		}
	}

	public void Delete(string profileId)
	{
		lock (SettingsStore.IoGate)
		{
			using (SettingsStore.AcquireWriteLock(stateDirectory))
			{
				string text = PathFor(profileId);
				if (File.Exists(text + ".bak"))
				{
					File.Delete(text + ".bak");
				}
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
		}
	}

	private static bool TryDecrypt(string path, out byte[] clear)
	{
		clear = null;
		if (!File.Exists(path))
		{
			return false;
		}
		try
		{
			clear = ProtectedData.Unprotect(SettingsStore.ReadSnapshot(path, 262144), Entropy, DataProtectionScope.CurrentUser);
			string text;
			try
			{
				text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(clear);
			}
			catch (DecoderFallbackException)
			{
				Array.Clear(clear, 0, clear.Length);
				clear = null;
				return false;
			}
			if (string.IsNullOrWhiteSpace(text) || text.Length > 65536)
			{
				Array.Clear(clear, 0, clear.Length);
				clear = null;
				return false;
			}
			return true;
		}
		catch (CryptographicException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	internal static bool IsValidProfileId(string profileId)
	{
		if (string.IsNullOrEmpty(profileId) || profileId.Length > 80)
		{
			return false;
		}
		string text = profileId.ToUpperInvariant();
		switch (text)
		{
		default:
			if (text.Length != 4 || (!text.StartsWith("COM", StringComparison.Ordinal) && !text.StartsWith("LPT", StringComparison.Ordinal)) || text[3] < '1' || text[3] > '9')
			{
				break;
			}
			goto case "CON";
		case "CON":
		case "PRN":
		case "AUX":
		case "NUL":
			return false;
		}
		foreach (char c in profileId)
		{
			if ((c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && c != '_' && c != '-')
			{
				return false;
			}
		}
		return true;
	}

	private string PathFor(string profileId)
	{
		if (!IsValidProfileId(profileId))
		{
			throw new ArgumentException("Некорректный идентификатор профиля.");
		}
		return Path.Combine(directory, profileId + ".bin");
	}
}
