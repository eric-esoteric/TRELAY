using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TRELAY;

internal static class Program
{
	private static MainForm mainForm;

	[STAThread]
	private static void Main(string[] args)
	{
		try
		{
			Run(args);
		}
		catch (Exception ex)
		{
			MessageBox.Show("Не удалось запустить TRELAY.\nПроверьте доступ к папке данных и резервную копию config.json.bak.\nКод: " + ex.GetType().Name, "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private static void WriteDiagnostic(string path, string category, Exception exception)
	{
		try
		{
			if (File.Exists(path) && new FileInfo(path).Length > 1048576)
			{
				File.Copy(path, path + ".previous", overwrite: true);
				File.WriteAllText(path, "");
			}
			File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + category + " " + ((exception == null) ? "" : exception.GetType().Name) + Environment.NewLine);
		}
		catch
		{
		}
	}

	private static void Run(string[] args)
	{
		string text = args.FirstOrDefault((string a) => a.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase));
		string text2 = args.FirstOrDefault((string a) => a.StartsWith("--snapshot-add=", StringComparison.OrdinalIgnoreCase));
		string text3 = args.FirstOrDefault((string a) => a.StartsWith("--snapshot-vps=", StringComparison.OrdinalIgnoreCase));
		string text4 = args.FirstOrDefault((string a) => a.StartsWith("--snapshot-toggle=", StringComparison.OrdinalIgnoreCase));
		string text5 = args.FirstOrDefault((string a) => a.StartsWith("--snapshot-settings=", StringComparison.OrdinalIgnoreCase));
		bool connectOnStart = args.Any((string a) => a.Equals("--connect", StringComparison.OrdinalIgnoreCase));
		string text6 = args.FirstOrDefault((string a) => a.StartsWith("--qa=", StringComparison.OrdinalIgnoreCase));
		string text7 = text?.Substring("--snapshot=".Length).Trim('"');
		if (text2 != null)
		{
			text7 = text2.Substring("--snapshot-add=".Length).Trim('"');
		}
		if (text3 != null)
		{
			text7 = text3.Substring("--snapshot-vps=".Length).Trim('"');
		}
		if (text4 != null)
		{
			text7 = text4.Substring("--snapshot-toggle=".Length).Trim('"');
		}
		if (text5 != null)
		{
			text7 = text5.Substring("--snapshot-settings=".Length).Trim('"');
		}
		if (text7 != null)
		{
			text7 = Path.GetFullPath(text7);
		}
		bool isolated = text7 != null || text6 != null;
		string text8 = ((text6 != null) ? Path.GetFullPath(text6.Substring(5).Trim('"')) : ((text7 == null) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TRELAY") : Path.Combine(Path.GetDirectoryName(text7), "snapshot-state")));
		Directory.CreateDirectory(text8);
		string startupLog = Path.Combine(text8, "startup.log");
		Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
		{
			WriteDiagnostic(startupLog, "UI_ERROR", e.Exception);
			MessageBox.Show("Операция не завершена. Проверьте доступ к файлам и повторите действие.\nКод: " + e.Exception.GetType().Name, "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		};
		AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
		{
			WriteDiagnostic(startupLog, "FATAL", e.ExceptionObject as Exception);
		};
		string text9 = "Local\\TRELAY" + (isolated ? (".QA." + StableId(text8)) : "");
		EventWaitHandle showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, text9 + ".Show");
		try
		{
			ManualResetEvent quitEvent = new ManualResetEvent(initialState: false);
			try
			{
				bool createdNew;
				using (new Mutex(initiallyOwned: true, text9, out createdNew))
				{
					if (!createdNew)
					{
						showEvent.Set();
						return;
					}
					Application.EnableVisualStyles();
					Application.SetCompatibleTextRenderingDefault(defaultValue: false);
					SettingsStore store = (isolated ? new SettingsStore(text8) : new SettingsStore());
					AppConfig appConfig = store.Load();
					if (text2 != null)
					{
						Theme.Select(appConfig.Theme);
						using ProfileDialog profileDialog = new ProfileDialog(null, 10810, new SecretStore(store));
						profileDialog.CaptureSnapshot(text7);
						return;
					}
					if (text3 != null)
					{
						Theme.Select(appConfig.Theme);
						using VpsSetupDialog vpsSetupDialog = new VpsSetupDialog(appConfig, store);
						vpsSetupDialog.CaptureSnapshot(text7);
						return;
					}
					if (text5 != null)
					{
						Theme.Select(appConfig.Theme);
						using SettingsDialog settingsDialog = new SettingsDialog(appConfig, store);
						settingsDialog.CaptureSnapshot(text7);
						return;
					}
					using TunnelEngine engine = new TunnelEngine(store);
					using (mainForm = new MainForm(store, engine, appConfig, text7 == null))
					{
						if (text7 != null)
						{
							if (text4 != null)
							{
								mainForm.CaptureToggleSnapshot(text7);
							}
							else
							{
								mainForm.CaptureSnapshot(text7);
							}
							return;
						}
						bool trayStart = args.Any((string a) => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)) || appConfig.StartMinimized;
						WriteDiagnostic(startupLog, "START tray=" + trayStart + " dpi=" + DpiScaler.Factor(mainForm).ToString("0.00", CultureInfo.InvariantCulture), null);
						if (trayStart)
						{
							mainForm.PrepareTrayStart();
						}
						Thread thread = new Thread((ThreadStart)delegate
						{
							try
							{
								while (WaitHandle.WaitAny(new WaitHandle[2] { showEvent, quitEvent }) == 0 && mainForm != null && !mainForm.IsDisposed)
								{
									try
									{
										mainForm.BeginInvoke(new Action(mainForm.ShowFromTray));
									}
									catch
									{
										break;
									}
								}
							}
							catch (ObjectDisposedException)
							{
							}
						});
						thread.IsBackground = true;
						thread.Name = "TRELAYActivation";
						thread.Start();
						mainForm.BeginInvoke((Action)delegate
						{
							if (!trayStart && !isolated)
							{
								mainForm.RunFirstLaunch();
							}
							if (!string.IsNullOrEmpty(store.RecoveryMessage))
							{
								mainForm.ShowFromTray();
								MessageBox.Show(mainForm, store.RecoveryMessage, "Восстановление настроек", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							}
							if (connectOnStart && !isolated)
							{
								mainForm.ConnectConfiguredRoutes();
							}
						});
						try
						{
							Application.Run(mainForm);
						}
						finally
						{
							quitEvent.Set();
							thread.Join(1000);
						}
					}
				}
			}
			finally
			{
				if (quitEvent != null)
				{
					((IDisposable)quitEvent).Dispose();
				}
			}
		}
		finally
		{
			if (showEvent != null)
			{
				((IDisposable)showEvent).Dispose();
			}
		}
	}

	private static string StableId(string value)
	{
		using SHA256 sHA = SHA256.Create();
		return BitConverter.ToString(sHA.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
	}
}
