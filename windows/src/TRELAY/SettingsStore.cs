using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TRELAY;

internal sealed class SettingsStore
{
	private sealed class WriteLockLease : IDisposable
	{
		private FileStream stream;

		private readonly string path;

		public WriteLockLease(FileStream stream, string path)
		{
			this.stream = stream;
			this.path = path;
		}

		public void Dispose()
		{
			if (stream == null)
			{
				return;
			}
			try
			{
				stream.Dispose();
			}
			finally
			{
				stream = null;
				heldWriteLocks.Remove(path);
			}
		}
	}

	private readonly JavaScriptSerializer json = new JavaScriptSerializer
	{
		MaxJsonLength = 16777216
	};

	internal static readonly object IoGate = new object();

	[ThreadStatic]
	private static HashSet<string> heldWriteLocks;

	private readonly bool discoverDefaults;

	public readonly string StateDirectory;

	public readonly string ConfigPath;

	public string RecoveryMessage { get; private set; }

	public SettingsStore()
		: this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TRELAY"))
	{
		discoverDefaults = true;
		try
		{
			lock (IoGate)
			{
				using (AcquireWriteLock(StateDirectory))
				{
					MigrateLegacyState();
				}
			}
		}
		catch (Exception ex)
		{
			if (!(ex is IOException) && !(ex is UnauthorizedAccessException))
			{
				throw;
			}
			RecoveryMessage = "Не удалось перенести прежние настройки. Исходные файлы сохранены.";
		}
	}

	internal SettingsStore(string stateDirectory)
	{
		if (string.IsNullOrWhiteSpace(stateDirectory))
		{
			throw new ArgumentException("State directory is required.");
		}
		StateDirectory = Path.GetFullPath(stateDirectory);
		ConfigPath = Path.Combine(StateDirectory, "config.json");
		Directory.CreateDirectory(StateDirectory);
	}

	public AppConfig Load()
	{
		lock (IoGate)
		{
			using (AcquireWriteLock(StateDirectory))
			{
				return LoadCore();
			}
		}
	}

	private AppConfig LoadCore()
	{
		AppConfig config = null;
		if (!TryReadConfig(ConfigPath, out config))
		{
			if (TryReadConfig(ConfigPath + ".bak", out config))
			{
				RecoveryMessage = "Настройки восстановлены из резервной копии. Проверь маршруты перед подключением.";
			}
			else if (File.Exists(ConfigPath) || File.Exists(ConfigPath + ".bak"))
			{
				throw new IOException("Не удалось прочитать настройки и резервную копию. Файлы сохранены; автоматический сброс маршрутов отменён.");
			}
		}
		if (config == null)
		{
			config = CreateDefaults();
		}
		if (config.Profiles == null)
		{
			config.Profiles = new List<TunnelProfile>();
		}
		if (config.AcknowledgedVpsRecoveryIds == null)
		{
			config.AcknowledgedVpsRecoveryIds = new List<string>();
		}
		if (config.TrustedSshHosts == null)
		{
			config.TrustedSshHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
		else
		{
			config.TrustedSshHosts = (from g in config.TrustedSshHosts.Where((KeyValuePair<string, string> x) => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value)).GroupBy((KeyValuePair<string, string> x) => x.Key, StringComparer.OrdinalIgnoreCase)
				where g.Count() == 1
				select g).ToDictionary((IGrouping<string, KeyValuePair<string, string>> g) => g.Key, (IGrouping<string, KeyValuePair<string, string>> g) => g.First().Value, StringComparer.OrdinalIgnoreCase);
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (config.Profiles.RemoveAll((TunnelProfile p) => p == null) != 0)
		{
			RecoveryMessage = "В настройках обнаружены пустые маршруты. Проверь список перед подключением.";
		}
		foreach (TunnelProfile profile in config.Profiles)
		{
			if (!SecretStore.IsValidProfileId(profile.Id) || !hashSet.Add(profile.Id))
			{
				profile.Id = Guid.NewGuid().ToString("N");
				hashSet.Add(profile.Id);
				profile.Enabled = false;
				RecoveryMessage = "Повреждённые идентификаторы маршрутов исправлены. Эти маршруты отключены; проверь их ключи.";
			}
			if (profile.AppNames == null)
			{
				profile.AppNames = new List<string>();
			}
			if (profile.AppPaths == null)
			{
				profile.AppPaths = new List<string>();
			}
			profile.AppNames.RemoveAll(string.IsNullOrWhiteSpace);
			profile.AppPaths.RemoveAll(string.IsNullOrWhiteSpace);
			if (string.IsNullOrWhiteSpace(profile.Name))
			{
				profile.Name = "Новый профиль";
			}
			if (string.IsNullOrWhiteSpace(profile.ConnectionType))
			{
				profile.ConnectionType = "file";
			}
			if (profile.KeyKind == null)
			{
				profile.KeyKind = "";
			}
			if (string.IsNullOrWhiteSpace(profile.Country))
			{
				profile.Country = GuessCountry(profile.Name);
			}
			else
			{
				profile.Country = NormalizeCountry(profile.Country);
			}
			if (string.IsNullOrWhiteSpace(profile.ProviderId))
			{
				profile.ProviderId = "auto";
			}
		}
		RecoverVpsDrafts(config);
		string text = Path.Combine(ApplicationDirectory(), "engine", "xray.exe");
		if (File.Exists(text) && (!File.Exists(config.XrayPath) || IsNetProxyXray(config.XrayPath) || IsPreviousTrelayEngine(config.XrayPath, "xray.exe")))
		{
			config.XrayPath = text;
		}
		string text2 = Path.Combine(ApplicationDirectory(), "engine", "mihomo.exe");
		if (File.Exists(text2) && (!File.Exists(config.MihomoPath) || IsPreviousTrelayEngine(config.MihomoPath, "mihomo.exe")))
		{
			config.MihomoPath = text2;
		}
		string text3 = Path.Combine(ApplicationDirectory(), "engine", "ProxiFyre.exe");
		if (File.Exists(text3) && (!File.Exists(config.ProxiFyrePath) || IsPreviousTrelayEngine(config.ProxiFyrePath, "ProxiFyre.exe")))
		{
			config.ProxiFyrePath = text3;
		}
		return config;
	}

	public void Save(AppConfig config, bool allowRemovingLastProfile = false)
	{
		if (config == null)
		{
			throw new ArgumentNullException("config");
		}
		lock (IoGate)
		{
			using (AcquireWriteLock(StateDirectory))
			{
				SaveCore(config, allowRemovingLastProfile);
			}
		}
	}

	private void SaveCore(AppConfig config, bool allowRemovingLastProfile)
	{
		Directory.CreateDirectory(StateDirectory);
		AppConfig config2;
		bool flag = TryReadConfig(ConfigPath, out config2);
		if (!flag)
		{
			TryReadConfig(ConfigPath + ".bak", out config2);
		}
		if (config2 == null && (File.Exists(ConfigPath) || File.Exists(ConfigPath + ".bak")))
		{
			throw new IOException("Настройки и резервная копия повреждены или недоступны. Сохранение отменено, исходные файлы сохранены.");
		}
		if (!allowRemovingLastProfile && (config.Profiles == null || config.Profiles.Count == 0) && config2 != null && config2.Profiles != null && config2.Profiles.Any((TunnelProfile p) => p != null))
		{
			throw new InvalidOperationException("Защита профилей остановила случайную очистку маршрутов.");
		}
		if (config.Profiles == null || config.Profiles.Any((TunnelProfile p) => p == null))
		{
			throw new InvalidOperationException("Настройки содержат пустой список или повреждённый маршрут.");
		}
		if (config.Profiles.Any((TunnelProfile p) => !SecretStore.IsValidProfileId(p.Id)) || config.Profiles.Select((TunnelProfile p) => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Profiles.Count)
		{
			throw new InvalidOperationException("Идентификаторы маршрутов должны быть допустимыми и уникальными.");
		}
		HashSet<string> hashSet = new HashSet<string>(config.AcknowledgedVpsRecoveryIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		foreach (TunnelProfile profile in config.Profiles)
		{
			hashSet.Add(profile.Id);
		}
		if (config2 != null)
		{
			if (config2.AcknowledgedVpsRecoveryIds != null)
			{
				hashSet.UnionWith(config2.AcknowledgedVpsRecoveryIds);
			}
			foreach (TunnelProfile item in config2.Profiles.Where((TunnelProfile p) => p != null))
			{
				hashSet.Add(item.Id);
			}
		}
		List<string> acknowledgedVpsRecoveryIds = config.AcknowledgedVpsRecoveryIds;
		config.AcknowledgedVpsRecoveryIds = hashSet.Where((string id) => SecretStore.IsValidProfileId(id) && id.StartsWith("vps-recovery-", StringComparison.OrdinalIgnoreCase)).ToList();
		try
		{
			string s = json.Serialize(config);
			if (Encoding.UTF8.GetByteCount(s) > 16777216)
			{
				throw new InvalidOperationException("Файл настроек превышает допустимый размер.");
			}
			if (!flag && File.Exists(ConfigPath))
			{
				File.Copy(ConfigPath, ConfigPath + ".corrupt-" + Guid.NewGuid().ToString("N"), overwrite: false);
			}
			WriteAtomic(ConfigPath, Encoding.UTF8.GetBytes(s), flag ? (ConfigPath + ".bak") : null);
		}
		catch
		{
			config.AcknowledgedVpsRecoveryIds = acknowledgedVpsRecoveryIds;
			throw;
		}
	}

	private bool TryReadConfig(string path, out AppConfig config)
	{
		config = null;
		if (!File.Exists(path))
		{
			return false;
		}
		try
		{
			string input;
			using (StreamReader streamReader = new StreamReader(new MemoryStream(ReadSnapshot(path, 16777216)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true))
			{
				input = streamReader.ReadToEnd();
			}
			if (!(json.DeserializeObject(input) is Dictionary<string, object> dictionary) || !dictionary.ContainsKey("Profiles"))
			{
				return false;
			}
			config = json.Deserialize<AppConfig>(input);
			object[] array = dictionary["Profiles"] as object[];
			if (config != null && config.Profiles != null && array != null && array.Length == config.Profiles.Count)
			{
				for (int i = 0; i < array.Length; i++)
				{
					Dictionary<string, object> dictionary2 = array[i] as Dictionary<string, object>;
					if (config.Profiles[i] != null && (dictionary2 == null || !dictionary2.TryGetValue("Id", out var value) || !(value is string)))
					{
						config.Profiles[i].Id = null;
					}
				}
			}
			return config != null && config.Profiles != null && (config.Profiles.Count == 0 || config.Profiles.Any((TunnelProfile p) => p != null));
		}
		catch (Exception ex)
		{
			Exception baseException = ex.GetBaseException();
			if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) && !(ex is InvalidOperationException) && !(baseException is FormatException) && !(baseException is OverflowException))
			{
				throw;
			}
			config = null;
			return false;
		}
	}

	private void RecoverVpsDrafts(AppConfig config)
	{
		string path = Path.Combine(StateDirectory, "secrets");
		if (!Directory.Exists(path))
		{
			return;
		}
		try
		{
			HashSet<string> hashSet = new HashSet<string>(config.Profiles.Select((TunnelProfile p) => p.Id), StringComparer.OrdinalIgnoreCase);
			if (config.AcknowledgedVpsRecoveryIds != null)
			{
				hashSet.UnionWith(config.AcknowledgedVpsRecoveryIds);
			}
			if (TryReadConfig(ConfigPath + ".bak", out var config2))
			{
				foreach (TunnelProfile item in config2.Profiles.Where((TunnelProfile p) => p != null))
				{
					hashSet.Add(item.Id);
				}
			}
			IEnumerable<string> enumerable = (from name in Directory.GetFiles(path, "vps-recovery-*.bin*").Select(Path.GetFileName)
				where name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin.bak", StringComparison.OrdinalIgnoreCase)
				select name.Substring(0, name.Length - (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? 8 : 4))).Where(SecretStore.IsValidProfileId).Distinct(StringComparer.OrdinalIgnoreCase);
			SecretStore secretStore = new SecretStore(this);
			foreach (string item2 in enumerable)
			{
				if (hashSet.Contains(item2))
				{
					continue;
				}
				string value;
				try
				{
					value = secretStore.Load(item2);
				}
				catch (CryptographicException)
				{
					AppendRecoveryMessage("Найден ключ незавершённой настройки VPS, но прочитать его не удалось. Исходный файл сохранён; проверь VPS и ключ подключения.");
					continue;
				}
				if (!ConnectionValidator.ValidateKey(value, out var kind, out var _))
				{
					AppendRecoveryMessage("Найден повреждённый ключ незавершённой настройки VPS. Исходный файл сохранён; проверь VPS и ключ подключения.");
					continue;
				}
				int num = 10810;
				for (HashSet<int> hashSet2 = new HashSet<int>(config.Profiles.Select((TunnelProfile p) => p.SocksPort)); hashSet2.Contains(num) && num < 65535; num++)
				{
				}
				config.Profiles.Add(new TunnelProfile
				{
					Id = item2,
					Name = "Незавершённая настройка VPS",
					ConnectionType = "key",
					KeyKind = kind,
					Country = "Авто",
					Enabled = false,
					SocksPort = num
				});
				hashSet.Add(item2);
				AppendRecoveryMessage("Восстановлен маршрут незавершённой настройки VPS. VPS мог остаться не настроен; проверь маршрут перед подключением.");
			}
		}
		catch (IOException)
		{
			AppendRecoveryMessage("Не удалось проверить ключи незавершённой настройки VPS. Исходные файлы сохранены.");
		}
		catch (UnauthorizedAccessException)
		{
			AppendRecoveryMessage("Нет доступа к ключам незавершённой настройки VPS. Исходные файлы сохранены.");
		}
	}

	private void AppendRecoveryMessage(string message)
	{
		if (string.IsNullOrEmpty(RecoveryMessage))
		{
			RecoveryMessage = message;
		}
		else if (RecoveryMessage.IndexOf(message, StringComparison.Ordinal) < 0)
		{
			RecoveryMessage = RecoveryMessage + "\n\n" + message;
		}
	}

	internal static void WriteAtomic(string path, byte[] value, string backup)
	{
		string text = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				fileStream.Write(value, 0, value.Length);
				fileStream.Flush(flushToDisk: true);
			}
			if (File.Exists(path))
			{
				File.Replace(text, path, backup, ignoreMetadataErrors: true);
			}
			else
			{
				File.Move(text, path);
			}
		}
		finally
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	internal static byte[] ReadSnapshot(string path, int maximumBytes)
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		if (fileStream.Length > maximumBytes)
		{
			throw new IOException("Файл превышает допустимый размер.");
		}
		byte[] array = new byte[(int)fileStream.Length];
		int num;
		for (int i = 0; i < array.Length; i += num)
		{
			num = fileStream.Read(array, i, array.Length - i);
			if (num == 0)
			{
				throw new EndOfStreamException();
			}
		}
		return array;
	}

	internal static IDisposable AcquireWriteLock(string stateDirectory)
	{
		Directory.CreateDirectory(stateDirectory);
		string text = Path.Combine(Path.GetFullPath(stateDirectory), ".store.lock");
		if (heldWriteLocks != null && heldWriteLocks.Contains(text))
		{
			return new WriteLockLease(null, null);
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (true)
		{
			try
			{
				FileStream stream = new FileStream(text, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				if (heldWriteLocks == null)
				{
					heldWriteLocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				}
				heldWriteLocks.Add(text);
				return new WriteLockLease(stream, text);
			}
			catch (IOException ex)
			{
				int num = ex.HResult & 0xFFFF;
				if ((num != 32 && num != 33) || stopwatch.ElapsedMilliseconds >= 5000)
				{
					throw new IOException("Не удалось получить доступ к сохранению настроек. Повтори попытку после завершения другой операции.", ex);
				}
				Thread.Sleep(25);
			}
		}
	}

	public void SetAutostart(bool enabled)
	{
		if (!discoverDefaults)
		{
			throw new InvalidOperationException("Автозапуск недоступен в изолированном режиме проверки.");
		}
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
		if (registryKey == null)
		{
			throw new InvalidOperationException("Не удалось открыть настройки автозапуска Windows.");
		}
		if (enabled)
		{
			registryKey.SetValue("TRELAY", "\"" + Application.ExecutablePath + "\" --tray");
		}
		else
		{
			registryKey.DeleteValue("TRELAY", throwOnMissingValue: false);
		}
		registryKey.DeleteValue("CallHunterAI", throwOnMissingValue: false);
		registryKey.DeleteValue("WebHunterAI", throwOnMissingValue: false);
	}

	private void MigrateLegacyState()
	{
		if (!MigratePreferredCallHunterState())
		{
			MigrateLegacyStateFrom("WebHunterAI", ".legacy-webhunter-migration-v3");
		}
	}

	private bool MigratePreferredCallHunterState()
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CallHunterAI");
		string path2 = Path.Combine(path, "config.json");
		if (!File.Exists(path2))
		{
			return false;
		}
		string path3 = Path.Combine(StateDirectory, ".preferred-callhunter-migration-v2");
		if (File.Exists(path3))
		{
			return true;
		}
		MigrateLegacyStateFrom("CallHunterAI", ".preferred-callhunter-migration-v2");
		return true;
	}

	private void MigrateLegacyStateFrom(string productId, string markerName)
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), productId);
		string path = Path.Combine(text, "config.json");
		if (!File.Exists(path))
		{
			return;
		}
		string path2 = Path.Combine(StateDirectory, markerName);
		if (File.Exists(path2))
		{
			return;
		}
		if (!TryReadConfig(path, out var config))
		{
			RecoveryMessage = "Прежний файл настроек повреждён; перенос отменён. Исходный файл сохранён.";
			return;
		}
		if (!File.Exists(ConfigPath) && !File.Exists(ConfigPath + ".bak"))
		{
			WriteAtomic(ConfigPath, File.ReadAllBytes(path), null);
		}
		else
		{
			AppConfig appConfig = LoadCore();
			if (appConfig.Profiles == null)
			{
				appConfig.Profiles = new List<TunnelProfile>();
			}
			if (config != null && config.Profiles != null)
			{
				foreach (TunnelProfile profile in config.Profiles)
				{
					if (profile == null || !SecretStore.IsValidProfileId(profile.Id) || appConfig.Profiles.Any((TunnelProfile p) => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase) || (string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(p.ConnectionType, profile.ConnectionType, StringComparison.OrdinalIgnoreCase) && string.Equals(p.XrayConfigPath ?? "", profile.XrayConfigPath ?? "", StringComparison.OrdinalIgnoreCase))))
					{
						continue;
					}
					if (profile.Enabled && profile.AppNames != null)
					{
						foreach (TunnelProfile item in appConfig.Profiles.Where((TunnelProfile p) => p.Enabled && p.AppNames != null))
						{
							if (item.AppNames.Intersect(profile.AppNames, StringComparer.OrdinalIgnoreCase).Any())
							{
								profile.Enabled = false;
							}
						}
					}
					appConfig.Profiles.Add(profile);
				}
			}
			SaveCore(appConfig, allowRemovingLastProfile: false);
		}
		CopySecrets(text);
		File.WriteAllText(path2, DateTime.UtcNow.ToString("O"));
	}

	private void CopySecrets(string legacy)
	{
		string path = Path.Combine(legacy, "secrets");
		string text = Path.Combine(StateDirectory, "secrets");
		if (!Directory.Exists(path))
		{
			return;
		}
		Directory.CreateDirectory(text);
		string[] files = Directory.GetFiles(path, "*.bin");
		foreach (string path2 in files)
		{
			string text2 = Path.Combine(text, Path.GetFileName(path2));
			if (!File.Exists(text2) && !File.Exists(text2 + ".bak"))
			{
				WriteAtomic(text2, File.ReadAllBytes(path2), null);
			}
		}
	}

	private static string GuessCountry(string name)
	{
		string text = (name ?? "").ToLowerInvariant();
		if (text.Contains("рос") || text.Contains("russia"))
		{
			return "Россия";
		}
		if (text.Contains("сша") || text.Contains("usa") || text.Contains("united states"))
		{
			return "США";
		}
		return "Авто";
	}

	private static string NormalizeCountry(string country)
	{
		string text = (country ?? "").Trim();
		if (text.Length == 0)
		{
			return "Авто";
		}
		if (text.IndexOf('Р') >= 0 || text.IndexOf('С') >= 0)
		{
			try
			{
				string text2 = Encoding.UTF8.GetString(Encoding.GetEncoding(1251).GetBytes(text));
				if (!text2.Contains("\ufffd"))
				{
					text = text2;
				}
			}
			catch
			{
			}
		}
		return text;
	}

	private AppConfig CreateDefaults()
	{
		AppConfig appConfig = new AppConfig();
		if (!discoverDefaults)
		{
			return appConfig;
		}
		string text = Path.Combine(ApplicationDirectory(), "engine", "xray.exe");
		if (File.Exists(text))
		{
			appConfig.XrayPath = text;
		}
		string text2 = Path.Combine(ApplicationDirectory(), "engine", "mihomo.exe");
		if (File.Exists(text2))
		{
			appConfig.MihomoPath = text2;
		}
		string text3 = Path.Combine(ApplicationDirectory(), "engine", "ProxiFyre.exe");
		if (File.Exists(text3))
		{
			appConfig.ProxiFyrePath = text3;
		}
		string text4 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetProxy", "configs", "Russia.XRay");
		if (File.Exists(text4))
		{
			TunnelProfile tunnelProfile = new TunnelProfile();
			tunnelProfile.Name = "Россия";
			tunnelProfile.XrayConfigPath = text4;
			tunnelProfile.ConnectionType = "file";
			tunnelProfile.SocksPort = 10810;
			TunnelProfile tunnelProfile2 = tunnelProfile;
			tunnelProfile2.AppNames.Add("Communicator");
			appConfig.Profiles.Add(tunnelProfile2);
		}
		return appConfig;
	}

	private static bool IsNetProxyXray(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			string path2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NetProxy", "bin", "xray", "xray.exe");
			return string.Equals(Path.GetFullPath(path), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsPreviousTrelayEngine(string path, string expectedFileName)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedFileName))
		{
			return false;
		}
		try
		{
			string fullPath = Path.GetFullPath(path);
			if (!File.Exists(fullPath) || !Path.GetFileName(fullPath).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			DirectoryInfo parent = Directory.GetParent(fullPath);
			if (parent == null || !parent.Name.Equals("engine", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			DirectoryInfo parent2 = parent.Parent;
			if (parent2 == null || !File.Exists(Path.Combine(parent2.FullName, "TRELAY.exe")))
			{
				return false;
			}
			if (parent2.Name.StartsWith("TRELAY-", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string productName = FileVersionInfo.GetVersionInfo(Path.Combine(parent2.FullName, "TRELAY.exe")).ProductName;
			return string.Equals(productName, "TRELAY", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static string ApplicationDirectory()
	{
		return Path.GetDirectoryName(typeof(SettingsStore).Assembly.Location);
	}
}
