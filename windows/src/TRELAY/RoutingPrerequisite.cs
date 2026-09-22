using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace TRELAY;

internal static class RoutingPrerequisite
{
	private const string DriverFileName = "ndisrd.sys";

	private const string InstallerFileName = "TRELAY-Routing-Setup.exe";

	private const string InstallerSha256 = "78947cc358375121092c72d5c19b01224e3114f1519f78271f60cdce265907d2";

	public static bool IsInstalled()
	{
		try
		{
			return File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "ndisrd.sys"));
		}
		catch
		{
			return false;
		}
	}

	public static bool EnsureInstalled(IWin32Window owner)
	{
		if (IsInstalled())
		{
			return true;
		}
		string text = Path.Combine(Path.GetDirectoryName(typeof(RoutingPrerequisite).Assembly.Location), "engine", "TRELAY-Routing-Setup.exe");
		if (!File.Exists(text))
		{
			MessageBox.Show(owner, "В комплекте не найден системный компонент маршрутизации. Распакуй ZIP целиком и запусти TRELAY снова.", "Маршрут не запущен", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		if (!VerifyInstaller(text))
		{
			MessageBox.Show(owner, "Системный компонент маршрутизации повреждён или был заменён. Скачай TRELAY заново.", "Маршрут не запущен", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		DialogResult dialogResult = MessageBox.Show(owner, "Для выбора отдельных приложений нужен системный компонент маршрутизации. Он уже входит в TRELAY. Windows запросит права администратора для разовой установки.", "Первая настройка TRELAY", MessageBoxButtons.OKCancel, MessageBoxIcon.Asterisk);
		if (dialogResult != DialogResult.OK)
		{
			return false;
		}
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = text;
			processStartInfo.WorkingDirectory = Path.GetDirectoryName(text);
			processStartInfo.UseShellExecute = true;
			processStartInfo.Verb = "runas";
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				throw new InvalidOperationException();
			}
			process.WaitForExit();
			if (process.ExitCode != 0)
			{
				return false;
			}
		}
		catch (Win32Exception ex)
		{
			if (ex.NativeErrorCode != 1223)
			{
				MessageBox.Show(owner, "Не удалось установить системный компонент маршрутизации.", "Маршрут не запущен", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
			return false;
		}
		catch
		{
			MessageBox.Show(owner, "Не удалось установить системный компонент маршрутизации.", "Маршрут не запущен", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		if (IsInstalled())
		{
			return true;
		}
		MessageBox.Show(owner, "Установка завершилась, но драйвер ещё не запущен. Перезагрузи Windows и снова нажми «Подключить».", "Нужна перезагрузка", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		return false;
	}

	internal static bool VerifyInstaller(string path)
	{
		try
		{
			byte[] array;
			using (FileStream inputStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				using SHA256 sHA = SHA256.Create();
				array = sHA.ComputeHash(inputStream);
			}
			string a = BitConverter.ToString(array).Replace("-", "").ToLowerInvariant();
			Array.Clear(array, 0, array.Length);
			return string.Equals(a, "78947cc358375121092c72d5c19b01224e3114f1519f78271f60cdce265907d2", StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}
}
