using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class MainForm : Form
{
	private sealed class Monogram : Control
	{
		protected override void OnPaint(PaintEventArgs e)
		{
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(Theme.Current.Accent, 2.5f))
			{
				e.Graphics.DrawEllipse(pen, 3, 3, base.Width - 7, base.Height - 7);
			}
			using SolidBrush brush = new SolidBrush(Theme.Current.Text);
			using Font font = Theme.Font(12f, FontStyle.Bold);
			SizeF sizeF = e.Graphics.MeasureString("CH", font);
			e.Graphics.DrawString("CH", font, brush, ((float)base.Width - sizeF.Width) / 2f, ((float)base.Height - sizeF.Height) / 2f);
		}
	}

	private readonly SettingsStore store;

	private readonly SecretStore secrets;

	private readonly TunnelEngine engine;

	private readonly ProviderCatalog providerCatalog;

	private readonly bool enableTray;

	private AppConfig config;

	private NotifyIcon tray;

	private Label stateLabel;

	private Label stateDetail;

	private Label validationLabel;

	private RoundedButton powerButton;

	private FlowLayoutPanel routes;

	private Label footerLabel;

	private Label versionLabel;

	private bool reallyExit;

	private bool trayTransition;

	private bool startHidden;

	private bool statusPending;

	private readonly Timer trayRelease = new Timer();

	private readonly Dictionary<string, Label> runtimeLabels = new Dictionary<string, Label>(StringComparer.OrdinalIgnoreCase);

	private readonly ToolTip hints = new ToolTip
	{
		InitialDelay = 350,
		ReshowDelay = 100,
		AutoPopDelay = 5000
	};

	public MainForm(SettingsStore store, TunnelEngine engine, AppConfig config, bool enableTray)
	{
		this.store = store;
		secrets = new SecretStore(store);
		this.engine = engine;
		providerCatalog = ProviderCatalog.Load(store);
		this.config = config;
		this.enableTray = enableTray;
		trayRelease.Interval = Math.Max(120, SystemInformation.DoubleClickTime / 2);
		Timer timer = trayRelease;
		EventHandler value = delegate
		{
			trayRelease.Stop();
			trayTransition = false;
		};
		timer.Tick += value;
		Theme.Select(config.Theme);
		BuildWindow();
		if (enableTray)
		{
			BuildTray();
		}
		engine.StateChanged += OnEngineStateChanged;
		engine.RouteSwitched += OnRouteSwitched;
		engine.Alert += OnEngineAlert;
		DpiScaler.Apply(this);
		RefreshAll();
		DpiScaler.Center(this);
	}

	private void BuildWindow()
	{
		Text = "TRELAY";
		base.Icon = AppIcon.Current;
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		base.AutoScaleMode = AutoScaleMode.None;
		base.ClientSize = new Size(820, 500);
		MinimumSize = new Size(780, 0);
		base.MaximizeBox = false;
		base.FormBorderStyle = FormBorderStyle.FixedSingle;
		base.StartPosition = FormStartPosition.CenterScreen;
		BackColor = Theme.Current.Background;
		DoubleBuffered = true;
		ForeColor = Theme.Current.Text;
		Font = Theme.Font(10f, FontStyle.Regular);
		Label label = Theme.Label("TRELAY", 18f, strong: true, Theme.Current.Text);
		label.SetBounds(22, 18, 390, 31);
		base.Controls.Add(label);
		Label label2 = Theme.Label("Маршруты для телефонии и нужных приложений", 9.2f, strong: false, Theme.Current.Muted);
		label2.SetBounds(24, 49, 460, 22);
		base.Controls.Add(label2);
		RoundedButton roundedButton = IconButton("", accent: false);
		roundedButton.SetBounds(756, 23, 40, 40);
		roundedButton.IconKind = ButtonIconKind.Settings;
		roundedButton.IconSize = 20;
		roundedButton.Click += delegate
		{
			OpenSettings();
		};
		hints.SetToolTip(roundedButton, "Настройки");
		roundedButton.AccessibleName = "Настройки";
		base.Controls.Add(roundedButton);
		RoundedPanel roundedPanel = new RoundedPanel();
		roundedPanel.BackColor = Theme.Current.Card;
		roundedPanel.Radius = 20;
		RoundedPanel roundedPanel2 = roundedPanel;
		roundedPanel2.SetBounds(24, 84, 772, 112);
		base.Controls.Add(roundedPanel2);
		stateLabel = Theme.Label("Готово к подключению", 14.5f, strong: true, Theme.Current.Text);
		stateLabel.SetBounds(28, 16, 430, 30);
		roundedPanel2.Controls.Add(stateLabel);
		stateDetail = Theme.Label("Маршрутизация не запущена", 9.2f, strong: false, Theme.Current.Muted);
		stateDetail.AutoSize = false;
		stateDetail.AutoEllipsis = true;
		stateDetail.SetBounds(28, 49, 510, 22);
		roundedPanel2.Controls.Add(stateDetail);
		validationLabel = Theme.Label("", 8.7f, strong: false, Theme.Current.Muted);
		validationLabel.AutoSize = false;
		validationLabel.AutoEllipsis = true;
		validationLabel.SetBounds(28, 78, 520, 22);
		roundedPanel2.Controls.Add(validationLabel);
		powerButton = AccentButton("Подключить");
		powerButton.SetBounds(572, 34, 176, 44);
		powerButton.Font = Theme.Font(10f, FontStyle.Bold);
		powerButton.Click += delegate
		{
			ToggleEngine();
		};
		roundedPanel2.Controls.Add(powerButton);
		Label label3 = Theme.Label("Маршруты", 12.5f, strong: true, Theme.Current.Text);
		label3.SetBounds(24, 219, 250, 28);
		base.Controls.Add(label3);
		Label label4 = Theme.Label("Приоритет сверху вниз · меняйте стрелками", 8.7f, strong: false, Theme.Current.Muted);
		label4.SetBounds(130, 224, 390, 22);
		base.Controls.Add(label4);
		RoundedButton roundedButton2 = IconButton("Добавить", accent: true);
		roundedButton2.IconKind = ButtonIconKind.Plus;
		roundedButton2.IconSize = 16;
		roundedButton2.SetBounds(620, 212, 176, 40);
		roundedButton2.Font = Theme.Font(9.5f, FontStyle.Bold);
		roundedButton2.Click += delegate
		{
			AddProfile();
		};
		hints.SetToolTip(roundedButton2, "Добавить новый маршрут");
		base.Controls.Add(roundedButton2);
		routes = new RouteListPanel();
		routes.SetBounds(24, 268, 772, 216);
		routes.BackColor = Theme.Current.Background;
		routes.FlowDirection = FlowDirection.TopDown;
		routes.WrapContents = false;
		routes.AutoScroll = false;
		routes.Padding = new Padding(0, 0, 0, 0);
		base.Controls.Add(routes);
		footerLabel = Theme.Label("Ключи защищены вашей учётной записью Windows", 8.3f, strong: false, Theme.Current.Muted);
		footerLabel.SetBounds(26, 522, 570, 22);
		base.Controls.Add(footerLabel);
		versionLabel = Theme.Label(AppInfo.DisplayVersion, 8.1f, strong: false, Theme.Current.Muted);
		versionLabel.TextAlign = ContentAlignment.MiddleRight;
		versionLabel.AutoSize = false;
		versionLabel.SetBounds(620, 522, 174, 22);
		base.Controls.Add(versionLabel);
		base.FormClosing += OnFormClosing;
	}

	public void PrepareTrayStart()
	{
		startHidden = enableTray;
	}

	protected override void SetVisibleCore(bool value)
	{
		base.SetVisibleCore(value && !startHidden);
	}

	public void RunFirstLaunch()
	{
		if (!enableTray)
		{
			return;
		}
		string issue;
		TunnelProfile broken;
		if (config.Profiles.Count == 0)
		{
			BeginInvoke((Action)delegate
			{
				MessageBox.Show(this, "Добавь готовый ключ или подключи свой VPS, затем выбери приложения. Пустой маршрут не сохранится.", "Первый запуск TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				AddProfile();
			});
		}
		else if (!ValidateProfiles(out issue, out broken) && broken != null)
		{
			BeginInvoke((Action)delegate
			{
				EditProfile(broken);
			});
		}
	}

	public void ConnectConfiguredRoutes()
	{
		if (!base.IsDisposed)
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(new Action(ConnectConfiguredRoutes));
			}
			else if (engine.State == EngineState.Off)
			{
				TryStart();
			}
		}
	}

	private void BuildTray()
	{
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		contextMenuStrip.Items.Add("Открыть", null, delegate
		{
			ShowFromTray();
		});
		contextMenuStrip.Items.Add("Включить", null, delegate
		{
			TryStart();
		});
		contextMenuStrip.Items.Add("Отключить", null, delegate
		{
			engine.Stop();
		});
		contextMenuStrip.Items.Add(new ToolStripSeparator());
		contextMenuStrip.Items.Add("Выйти и отключить", null, delegate
		{
			ExitApplication();
		});
		tray = new NotifyIcon
		{
			Icon = AppIcon.Current,
			Text = "TRELAY",
			ContextMenuStrip = contextMenuStrip,
			Visible = true
		};
		tray.MouseClick += delegate(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				ToggleTrayWindow();
			}
		};
	}

	private void RefreshAll()
	{
		RefreshRoutes();
		RefreshStatus();
	}

	private void RefreshRoutes(bool resizeWindow = true)
	{
		routes.SuspendLayout();
		Control[] array = routes.Controls.Cast<Control>().ToArray();
		foreach (Control control in array)
		{
			control.Dispose();
		}
		runtimeLabels.Clear();
		if (config.Profiles.Count == 0)
		{
			RoundedPanel roundedPanel = new RoundedPanel();
			roundedPanel.BackColor = Theme.Current.Card;
			roundedPanel.Radius = 16;
			roundedPanel.Width = 772;
			roundedPanel.Height = 96;
			roundedPanel.Margin = new Padding(0, 0, 0, 10);
			RoundedPanel roundedPanel2 = roundedPanel;
			Label label = Theme.Label("Маршрутов пока нет", 11f, strong: true, Theme.Current.Text);
			label.SetBounds(28, 19, 430, 25);
			roundedPanel2.Controls.Add(label);
			Label label2 = Theme.Label("Добавьте сервер, затем выберите приложения для него.", 9f, strong: false, Theme.Current.Muted);
			label2.SetBounds(28, 50, 600, 23);
			roundedPanel2.Controls.Add(label2);
			DpiScaler.ScaleNewControl(this, roundedPanel2);
			routes.Controls.Add(roundedPanel2);
		}
		foreach (TunnelProfile profile in config.Profiles)
		{
			routes.Controls.Add(BuildRouteCard(profile));
		}
		routes.AutoScroll = false;
		if (resizeWindow)
		{
			int num = DpiScaler.Px(this, (config.Profiles.Count == 0) ? 96 : Math.Min(230, config.Profiles.Count * 84 - 8));
			routes.Height = num;
			footerLabel.Top = routes.Bottom + DpiScaler.Px(this, 16);
			versionLabel.Top = footerLabel.Top;
			base.ClientSize = new Size(DpiScaler.Px(this, 820), footerLabel.Bottom + DpiScaler.Px(this, 14));
		}
		routes.ResumeLayout();
	}

	private Control BuildRouteCard(TunnelProfile profile)
	{
		RoundedPanel roundedPanel = new RoundedPanel();
		roundedPanel.BackColor = Theme.Current.Card;
		roundedPanel.Radius = 16;
		roundedPanel.Width = 772;
		roundedPanel.Height = 76;
		roundedPanel.Margin = new Padding(0, 0, 0, 8);
		RoundedPanel roundedPanel2 = roundedPanel;
		Label label = Theme.Label(profile.Name, 10.5f, strong: true, Theme.Current.Text);
		label.AutoSize = false;
		label.AutoEllipsis = true;
		label.SetBounds(62, 12, 218, 25);
		roundedPanel2.Controls.Add(label);
		hints.SetToolTip(label, profile.Name);
		Label label2 = Theme.Label(string.Join(", ", profile.AppNames ?? new List<string>()), 8.7f, strong: false, Theme.Current.Muted);
		if (string.IsNullOrEmpty(label2.Text))
		{
			label2.Text = "Выберите приложения, чтобы завершить настройку";
		}
		label2.AutoSize = false;
		label2.AutoEllipsis = true;
		label2.SetBounds(62, 40, 300, 22);
		roundedPanel2.Controls.Add(label2);
		hints.SetToolTip(label2, label2.Text);
		Label label3 = Theme.Label("", 8.3f, strong: true, Theme.Current.Muted);
		label3.AutoSize = false;
		label3.AutoEllipsis = true;
		label3.TextAlign = ContentAlignment.MiddleRight;
		label3.SetBounds(368, 40, 196, 22);
		roundedPanel2.Controls.Add(label3);
		runtimeLabels[profile.Id] = label3;
		ProviderDefinition providerDefinition = providerCatalog.Resolve(profile, ProviderTrafficObserver.ReadHosts(store, profile));
		string text = ((providerDefinition == null) ? "АВТО" : providerDefinition.DisplayName.ToUpperInvariant());
		string text2 = text + " · " + (string.IsNullOrWhiteSpace(profile.Country) ? "АВТО" : profile.Country.ToUpperInvariant()) + " · " + ((profile.ConnectionType == "key") ? profile.KeyKind.ToUpperInvariant() : "XRAY");
		if (profile.TelephonyOptimized)
		{
			text2 += " · UDP";
		}
		HashSet<string> profileApps = new HashSet<string>((profile.AppNames ?? new List<string>()).Select(NormalizeAppName), StringComparer.OrdinalIgnoreCase);
		List<TunnelProfile> list = config.Profiles.Where((TunnelProfile p) => p.Enabled && (p.AppNames ?? new List<string>()).Select(NormalizeAppName).Any(profileApps.Contains)).ToList();
		if (profile.Enabled && list.Count > 1)
		{
			int num = list.IndexOf(profile) + 1;
			text2 = ((num == 1) ? "ОСНОВНОЙ" : ("РЕЗЕРВ " + (num - 1))) + " · " + text2;
		}
		Label label4 = Theme.Label(text2.Replace(" · ", "  ·  "), 8f, strong: false, Theme.Current.Secondary);
		label4.AutoSize = false;
		label4.AutoEllipsis = true;
		label4.SetBounds(294, 16, 258, 21);
		roundedPanel2.Controls.Add(label4);
		hints.SetToolTip(label4, text2);
		ToggleSwitch toggle = new ToggleSwitch
		{
			Checked = profile.Enabled
		};
		toggle.SetBounds(578, 24, 48, 28);
		toggle.CheckedChanged += delegate
		{
			SetProfileEnabled(profile, toggle.Checked);
		};
		hints.SetToolTip(toggle, profile.Enabled ? "Выключить маршрут" : "Включить маршрут");
		toggle.AccessibleName = "Маршрут " + profile.Name;
		roundedPanel2.Controls.Add(toggle);
		RoundedButton roundedButton = IconButton("", accent: false);
		roundedButton.IconKind = ButtonIconKind.Edit;
		roundedButton.IconSize = 16;
		roundedButton.SetBounds(642, 19, 40, 38);
		hints.SetToolTip(roundedButton, "Изменить маршрут");
		roundedButton.AccessibleName = "Изменить маршрут " + profile.Name;
		roundedButton.Click += delegate
		{
			EditProfile(profile);
		};
		roundedPanel2.Controls.Add(roundedButton);
		RoundedButton roundedButton2 = IconButton("", accent: false);
		roundedButton2.IconKind = ButtonIconKind.Delete;
		roundedButton2.IconSize = 15;
		roundedButton2.SetBounds(696, 19, 40, 38);
		hints.SetToolTip(roundedButton2, "Удалить маршрут");
		roundedButton2.AccessibleName = "Удалить маршрут " + profile.Name;
		roundedButton2.Click += delegate
		{
			RemoveProfile(profile);
		};
		roundedPanel2.Controls.Add(roundedButton2);
		int num2 = config.Profiles.IndexOf(profile);
		RoundedButton roundedButton3 = IconButton("↑", accent: false);
		roundedButton3.SetBounds(12, 9, 32, 27);
		roundedButton3.Enabled = num2 > 0;
		roundedButton3.AccessibleName = "Повысить приоритет " + profile.Name;
		hints.SetToolTip(roundedButton3, "Выше — использовать раньше");
		roundedButton3.Click += delegate
		{
			MoveProfile(profile, -1);
		};
		roundedPanel2.Controls.Add(roundedButton3);
		RoundedButton roundedButton4 = IconButton("↓", accent: false);
		roundedButton4.SetBounds(12, 40, 32, 27);
		roundedButton4.Enabled = num2 < config.Profiles.Count - 1;
		roundedButton4.AccessibleName = "Понизить приоритет " + profile.Name;
		hints.SetToolTip(roundedButton4, "Ниже — использовать как резерв");
		roundedButton4.Click += delegate
		{
			MoveProfile(profile, 1);
		};
		roundedPanel2.Controls.Add(roundedButton4);
		DpiScaler.ScaleNewControl(this, roundedPanel2);
		if (config.Profiles.Count > 2)
		{
			roundedPanel2.Width -= SystemInformation.VerticalScrollBarWidth + DpiScaler.Px(this, 4);
		}
		return roundedPanel2;
	}

	private RoundedButton AccentButton(string text)
	{
		RoundedButton roundedButton = new RoundedButton();
		roundedButton.Text = text;
		roundedButton.Radius = 12;
		roundedButton.BackColor = Theme.Current.Accent;
		roundedButton.ForeColor = Color.Black;
		roundedButton.Font = Theme.Font(10f, FontStyle.Bold);
		return roundedButton;
	}

	private void MoveProfile(TunnelProfile profile, int direction)
	{
		int num = config.Profiles.IndexOf(profile);
		int num2 = num + direction;
		if (num >= 0 && num2 >= 0 && num2 < config.Profiles.Count)
		{
			config.Profiles.RemoveAt(num);
			config.Profiles.Insert(num2, profile);
			PersistAndRestart(resizeWindow: false);
		}
	}

	private RoundedButton IconButton(string text, bool accent)
	{
		RoundedButton roundedButton = new RoundedButton();
		roundedButton.Text = text;
		roundedButton.Radius = 11;
		roundedButton.BackColor = (accent ? Theme.Current.Accent : Theme.Current.Input);
		roundedButton.ForeColor = (accent ? Color.Black : Theme.Current.Text);
		roundedButton.Font = Theme.Font(10f, FontStyle.Bold);
		return roundedButton;
	}

	private void ToggleEngine()
	{
		if (engine.State != EngineState.Stopping)
		{
			if (engine.State != EngineState.Off)
			{
				engine.Stop();
			}
			else
			{
				TryStart();
			}
		}
	}

	private void SetProfileEnabled(TunnelProfile profile, bool enabled)
	{
		if (profile.Enabled == enabled)
		{
			return;
		}
		profile.Enabled = enabled;
		try
		{
			store.Save(config);
		}
		catch
		{
			profile.Enabled = !enabled;
			BeginInvoke((Action)delegate
			{
				if (!base.IsDisposed)
				{
					RefreshAll();
				}
			});
			throw;
		}
		bool flag = config.Profiles.Any((TunnelProfile p) => p.Enabled);
		if (!EngineRunning())
		{
			RefreshStatus();
		}
		else if (!flag)
		{
			engine.Stop();
		}
		else
		{
			engine.Restart(config);
		}
	}

	private void TryStart()
	{
		if (!RoutingPrerequisite.EnsureInstalled(this))
		{
			return;
		}
		if (!ValidateProfiles(out var issue, out var broken))
		{
			RefreshStatus();
			MessageBox.Show(this, issue, "Маршрут не запущен", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			if (broken != null)
			{
				EditProfile(broken);
			}
			else if (config.Profiles.Count == 0)
			{
				AddProfile();
			}
		}
		else
		{
			engine.Start(config);
		}
	}

	private bool ValidateProfiles(out string issue, out TunnelProfile broken)
	{
		broken = null;
		List<TunnelProfile> list = config.Profiles.Where((TunnelProfile p) => p.Enabled).ToList();
		if (list.Count == 0)
		{
			issue = "Нет ни одного включённого маршрута.";
			return false;
		}
		List<TunnelProfile> list2 = new List<TunnelProfile>();
		List<Tuple<TunnelProfile, string>> list3 = new List<Tuple<TunnelProfile, string>>();
		foreach (TunnelProfile item in list)
		{
			string text = null;
			if (string.IsNullOrWhiteSpace(item.Name))
			{
				text = "У маршрута нет названия.";
			}
			else if (item.AppNames == null || item.AppNames.Count == 0)
			{
				text = "В маршруте «" + item.Name + "» не выбрано ни одного приложения.";
			}
			if (text == null)
			{
				foreach (string item2 in item.AppPaths ?? new List<string>())
				{
					if (!File.Exists(item2))
					{
						text = "Приложение удалено или перемещено: " + item2;
						break;
					}
				}
			}
			if (text == null && (item.SocksPort < 1 || item.SocksPort > 65535))
			{
				text = "В маршруте «" + item.Name + "» указан некорректный SOCKS-порт.";
			}
			if (text == null && !SocksHealthProbe.ValidateEndpoint(item.HealthCheckEndpoint, out var message))
			{
				text = message;
			}
			if (text == null && item.ConnectionType == "key")
			{
				if (!secrets.Exists(item.Id))
				{
					text = "Для маршрута «" + item.Name + "» не сохранён ключ.";
				}
			}
			else if (text == null)
			{
				if (!ConnectionValidator.ValidateFile(item.XrayConfigPath, out var kind, out var message2))
				{
					text = "Маршрут «" + item.Name + "»: " + message2;
				}
				else
				{
					item.KeyKind = kind;
				}
			}
			if (text == null)
			{
				list2.Add(item);
			}
			else
			{
				list3.Add(Tuple.Create(item, text));
			}
		}
		if (list2.Any((TunnelProfile p) => string.Equals(p.KeyKind, "mihomo", StringComparison.OrdinalIgnoreCase)) && !File.Exists(config.MihomoPath))
		{
			issue = "Не найден встроенный mihomo.exe. Переустанови полную сборку TRELAY.";
			return false;
		}
		if (list2.Any((TunnelProfile p) => !string.Equals(p.KeyKind, "mihomo", StringComparison.OrdinalIgnoreCase)) && !File.Exists(config.XrayPath))
		{
			issue = "Не найден xray.exe. Укажи его через ⚙.";
			return false;
		}
		if (!File.Exists(config.ProxiFyrePath))
		{
			issue = "Не найден изолированный ProxiFyre.exe. Укажи его через ⚙.";
			return false;
		}
		if (engine.HasForeignProxiFyre(config.ProxiFyrePath))
		{
			issue = "Работает ProxiFyre из другого VPN-клиента. Отключи тот VPN перед запуском.";
			return false;
		}
		HashSet<string> coveredApps = new HashSet<string>(list2.SelectMany((TunnelProfile p) => p.AppNames ?? new List<string>()).Select(NormalizeAppName), StringComparer.OrdinalIgnoreCase);
		Tuple<TunnelProfile, string> tuple = list3.FirstOrDefault((Tuple<TunnelProfile, string> x) => x.Item1.AppNames == null || x.Item1.AppNames.Count == 0 || x.Item1.AppNames.Select(NormalizeAppName).Any((string app) => !coveredApps.Contains(app)));
		if (tuple != null)
		{
			broken = tuple.Item1;
			issue = tuple.Item2;
			return false;
		}
		issue = ((list3.Count == 0) ? "Маршруты готовы; повторы приложений используются как резерв" : ("Маршруты готовы; нерабочих профилей будет пропущено: " + list3.Count));
		return true;
	}

	private static string NormalizeAppName(string app)
	{
		app = (app ?? "").Trim();
		if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			return app;
		}
		return app.Substring(0, app.Length - 4);
	}

	private void RefreshStatus()
	{
		TunnelProfile broken = null;
		string issue = "";
		bool flag = engine.State == EngineState.On || engine.State == EngineState.TunnelReady || engine.State == EngineState.Starting || ValidateProfiles(out issue, out broken);
		if (engine.State == EngineState.On)
		{
			issue = "Подключение приложения подтверждено";
		}
		else if (engine.State == EngineState.TunnelReady)
		{
			issue = "Туннель работает; состояние приложения ещё не подтверждено";
		}
		else if (engine.State == EngineState.Starting)
		{
			issue = "Запускаем маршрут";
		}
		validationLabel.Text = (flag ? "✓ " : "! ") + issue;
		hints.SetToolTip(validationLabel, issue);
		validationLabel.ForeColor = (flag ? Theme.Current.Success : Theme.Current.Danger);
		powerButton.Enabled = flag || engine.State != EngineState.Off;
		if (engine.State == EngineState.Stopping)
		{
			powerButton.Enabled = false;
		}
		powerButton.BackColor = (powerButton.Enabled ? Theme.Current.Accent : Theme.Current.Hover);
		stateDetail.Text = engine.StatusText;
		hints.SetToolTip(stateDetail, engine.StatusText);
		validationLabel.Visible = engine.State != EngineState.Error;
		if (engine.State == EngineState.On)
		{
			stateLabel.Text = "Приложение подключено";
			stateLabel.ForeColor = Theme.Current.Success;
			powerButton.Text = "Отключить";
		}
		else if (engine.State == EngineState.TunnelReady)
		{
			stateLabel.Text = "Туннель готов";
			stateLabel.ForeColor = Theme.Current.Secondary;
			powerButton.Text = "Отключить";
		}
		else if (engine.State == EngineState.Starting)
		{
			stateLabel.Text = "Подключаем…";
			stateLabel.ForeColor = Theme.Current.Secondary;
			powerButton.Text = "Отключить";
		}
		else if (engine.State == EngineState.Stopping)
		{
			stateLabel.Text = "Отключаем…";
			stateLabel.ForeColor = Theme.Current.Secondary;
			powerButton.Text = "Отключаем…";
		}
		else if (engine.State == EngineState.Error)
		{
			stateLabel.Text = "Нет рабочего маршрута";
			stateLabel.ForeColor = Theme.Current.Danger;
			powerButton.Text = "Отключить";
		}
		else
		{
			stateLabel.Text = (flag ? "Готово к подключению" : "Нужна настройка");
			stateLabel.ForeColor = (flag ? Theme.Current.Text : Theme.Current.Secondary);
			powerButton.Text = "Подключить";
		}
		if (tray != null)
		{
			string text = "TRELAY: " + engine.StatusText;
			tray.Text = text.Substring(0, Math.Min(63, text.Length));
		}
		RefreshRuntimeLabels();
	}

	private bool EngineRunning()
	{
		if (engine.State != EngineState.Off)
		{
			return engine.State != EngineState.Stopping;
		}
		return false;
	}

	private void RefreshRuntimeLabels()
	{
		Dictionary<string, ProfileRuntimeInfo> dictionary = (EngineRunning() ? engine.GetRuntimeInfo() : new Dictionary<string, ProfileRuntimeInfo>());
		foreach (KeyValuePair<string, Label> runtimeLabel in runtimeLabels)
		{
			Label value = runtimeLabel.Value;
			if (value.IsDisposed)
			{
				continue;
			}
			if (!dictionary.TryGetValue(runtimeLabel.Key, out var value2))
			{
				value.Text = "";
				hints.SetToolTip(value, "");
				continue;
			}
			string text;
			Color foreColor;
			switch (value2.Status)
			{
			case ProfileRuntimeStatus.Active:
				text = "● работает";
				foreColor = Theme.Current.Success;
				break;
			case ProfileRuntimeStatus.Standby:
				text = "● резерв готов";
				foreColor = Theme.Current.Success;
				break;
			case ProfileRuntimeStatus.Starting:
				text = "● проверка…";
				foreColor = Theme.Current.Secondary;
				break;
			case ProfileRuntimeStatus.Unhealthy:
				text = "● не отвечает";
				foreColor = Theme.Current.Danger;
				break;
			case ProfileRuntimeStatus.Skipped:
				text = "● пропущен";
				foreColor = Theme.Current.Danger;
				break;
			default:
				text = "● перезапуск";
				foreColor = Theme.Current.Danger;
				break;
			}
			value.Text = text;
			value.ForeColor = foreColor;
			hints.SetToolTip(value, value2.Detail ?? "");
		}
	}

	private void OnEngineStateChanged()
	{
		if (base.IsDisposed || base.Disposing || !base.IsHandleCreated)
		{
			return;
		}
		try
		{
			BeginInvoke((Action)delegate
			{
				if (!base.IsDisposed && !base.Disposing)
				{
					if (!base.Visible)
					{
						statusPending = true;
						if (tray != null)
						{
							string text = "TRELAY: " + engine.StatusText;
							tray.Text = text.Substring(0, Math.Min(63, text.Length));
						}
					}
					else
					{
						RefreshStatus();
					}
				}
			});
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void OnRouteSwitched(RouteSwitchInfo info)
	{
		if (base.IsDisposed || base.Disposing || !base.IsHandleCreated)
		{
			return;
		}
		try
		{
			BeginInvoke((Action)delegate
			{
				if (!base.IsDisposed && !base.Disposing)
				{
					if (base.Visible)
					{
						RefreshStatus();
					}
					else
					{
						statusPending = true;
					}
				}
			});
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void OnEngineAlert(EngineAlert alert)
	{
		if (base.IsDisposed || base.Disposing || !base.IsHandleCreated)
		{
			return;
		}
		try
		{
			BeginInvoke((Action)delegate
			{
				if (!base.IsDisposed && !base.Disposing)
				{
					if (config.SoundAlerts && alert.Critical)
					{
						AlertSound.Play(critical: true, config.AlertOutputDevice);
					}
					if (tray != null)
					{
						string text = alert.Message ?? "";
						if (text.Length > 250)
						{
							text = text.Substring(0, 247) + "…";
						}
						tray.ShowBalloonTip(alert.Critical ? 10000 : 5000, alert.Title ?? "TRELAY", text, (!alert.Critical) ? ToolTipIcon.Info : ToolTipIcon.Warning);
					}
					if (alert.Critical && !base.Visible)
					{
						statusPending = true;
					}
					if (base.Visible)
					{
						RefreshStatus();
					}
				}
			});
		}
		catch (InvalidOperationException)
		{
		}
	}

	private int NextPort()
	{
		int num = 10810;
		for (List<int> list = config.Profiles.Select((TunnelProfile p) => p.SocksPort).ToList(); list.Contains(num); num++)
		{
		}
		return num;
	}

	private void AddProfile()
	{
		using ConnectionChoiceDialog connectionChoiceDialog = new ConnectionChoiceDialog();
		if (connectionChoiceDialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		if (connectionChoiceDialog.Choice == ConnectionChoice.Existing)
		{
			using (ProfileDialog profileDialog = new ProfileDialog(null, NextPort(), secrets))
			{
				if (profileDialog.ShowDialog(this) == DialogResult.OK)
				{
					AddProfiles(profileDialog.Results);
				}
				return;
			}
		}
		using VpsSetupDialog vpsSetupDialog = new VpsSetupDialog(config, store);
		if (vpsSetupDialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		using ProfileDialog profileDialog2 = new ProfileDialog(null, NextPort(), secrets, vpsSetupDialog.ProvisionedKey, vpsSetupDialog.ProfileName, vpsSetupDialog.Country, initialTelephony: true);
		if (profileDialog2.ShowDialog(this) == DialogResult.OK)
		{
			if (AddProfiles(profileDialog2.Results) && !string.IsNullOrEmpty(vpsSetupDialog.RecoverySecretId))
			{
				secrets.Delete(vpsSetupDialog.RecoverySecretId);
			}
			return;
		}
		ConnectionValidator.ValidateKey(vpsSetupDialog.ProvisionedKey, out var kind, out var _);
		config.Profiles.Add(new TunnelProfile
		{
			Id = vpsSetupDialog.RecoverySecretId,
			Name = vpsSetupDialog.ProfileName,
			Country = vpsSetupDialog.Country,
			ConnectionType = "key",
			KeyKind = kind,
			TelephonyOptimized = true,
			SocksPort = NextPort(),
			Enabled = false
		});
		PersistAndRestart();
	}

	private void EditProfile(TunnelProfile profile)
	{
		using ProfileDialog profileDialog = new ProfileDialog(profile, profile.SocksPort, secrets);
		if (profileDialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		int num = config.Profiles.FindIndex((TunnelProfile p) => p.Id == profile.Id);
		List<TunnelProfile> list = profileDialog.Results;
		if (list == null)
		{
			List<TunnelProfile> list2 = new List<TunnelProfile>();
			list2.Add(profileDialog.Result);
			list = list2;
		}
		List<TunnelProfile> list3 = list;
		if (num >= 0)
		{
			config.Profiles.RemoveAt(num);
			config.Profiles.InsertRange(num, list3);
		}
		if (PersistAndRestart())
		{
			return;
		}
		foreach (TunnelProfile item in list3.Where((TunnelProfile x) => x.Id != profile.Id))
		{
			secrets.Delete(item.Id);
		}
	}

	private bool AddProfiles(List<TunnelProfile> profiles)
	{
		profiles = profiles ?? new List<TunnelProfile>();
		if (profiles.Count == 0)
		{
			return false;
		}
		config.Profiles.AddRange(profiles);
		if (PersistAndRestart())
		{
			return true;
		}
		foreach (TunnelProfile profile in profiles)
		{
			secrets.Delete(profile.Id);
		}
		return false;
	}

	private void RemoveProfile(TunnelProfile profile)
	{
		if (MessageBox.Show(this, "Удалить маршрут «" + profile.Name + "»?", "TRELAY", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
		{
			config.Profiles.RemoveAll((TunnelProfile p) => p.Id == profile.Id);
			if (PersistAndRestart(resizeWindow: true, config.Profiles.Count == 0))
			{
				secrets.Delete(profile.Id);
			}
		}
	}

	private void OpenSettings()
	{
		using SettingsDialog settingsDialog = new SettingsDialog(config, store);
		if (settingsDialog.ShowDialog(this) == DialogResult.OK)
		{
			MessageBox.Show(this, "Настройки сохранены.", "TRELAY");
			if (EngineRunning())
			{
				engine.Restart(config);
			}
			RefreshStatus();
		}
	}

	private bool PersistAndRestart(bool resizeWindow = true, bool allowRemovingLastProfile = false)
	{
		try
		{
			store.Save(config, allowRemovingLastProfile);
		}
		catch
		{
			config = store.Load();
			RefreshAll();
			MessageBox.Show(this, "Изменения не сохранены. Проверьте свободное место и доступ к папке данных. Предыдущие маршруты восстановлены.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		if (resizeWindow)
		{
			RefreshAll();
		}
		else
		{
			RefreshRoutes(resizeWindow: false);
			RefreshStatus();
		}
		if (EngineRunning())
		{
			if (config.Profiles.Any((TunnelProfile p) => p.Enabled))
			{
				engine.Restart(config);
			}
			else
			{
				engine.Stop();
			}
		}
		return true;
	}

	public void CaptureToggleSnapshot(string path)
	{
		if (config.Profiles.Count == 0)
		{
			throw new InvalidOperationException("Toggle snapshot requires a route.");
		}
		Size clientSize = base.ClientSize;
		config.Profiles[0].Enabled = !config.Profiles[0].Enabled;
		RefreshRoutes(resizeWindow: false);
		RefreshStatus();
		if (base.ClientSize != clientSize)
		{
			throw new InvalidOperationException("ON/OFF changed the window size.");
		}
		CaptureSnapshot(path);
	}

	private void ToggleTrayWindow()
	{
		if (trayTransition || base.IsDisposed)
		{
			return;
		}
		trayTransition = true;
		try
		{
			Form form = base.OwnedForms.FirstOrDefault((Form form2) => form2.Visible);
			if (form != null)
			{
				ShowFromTray();
				WindowChrome.RestoreToForeground(form);
			}
			else if (base.Visible && base.WindowState != FormWindowState.Minimized)
			{
				HideToTray();
			}
			else
			{
				ShowFromTray();
			}
		}
		finally
		{
			if (!base.IsDisposed)
			{
				trayRelease.Start();
			}
		}
	}

	private void HideToTray()
	{
		if (tray != null)
		{
			Hide();
			if (base.WindowState != FormWindowState.Normal)
			{
				base.WindowState = FormWindowState.Normal;
			}
		}
	}

	public void ShowFromTray()
	{
		if (base.IsDisposed)
		{
			return;
		}
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action(ShowFromTray));
			return;
		}
		startHidden = false;
		base.WindowState = FormWindowState.Normal;
		if (statusPending)
		{
			statusPending = false;
			RefreshStatus();
		}
		if (!base.Visible)
		{
			Show();
		}
		WindowChrome.RestoreToForeground(this);
	}

	private void OnFormClosing(object sender, FormClosingEventArgs e)
	{
		if (!reallyExit && tray != null && e.CloseReason == CloseReason.UserClosing)
		{
			e.Cancel = true;
			HideToTray();
		}
	}

	private void ExitApplication()
	{
		reallyExit = true;
		engine.Dispose();
		if (tray != null)
		{
			tray.Visible = false;
			tray.Dispose();
		}
		Close();
	}

	public void CaptureSnapshot(string path)
	{
		Show();
		Refresh();
		Application.DoEvents();
		using (Bitmap bitmap = new Bitmap(base.Width, base.Height))
		{
			DrawToBitmap(bitmap, new Rectangle(0, 0, base.Width, base.Height));
			bitmap.Save(path, ImageFormat.Png);
		}
		Hide();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			engine.StateChanged -= OnEngineStateChanged;
			engine.RouteSwitched -= OnRouteSwitched;
			engine.Alert -= OnEngineAlert;
			trayRelease.Dispose();
			hints.Dispose();
			if (tray != null)
			{
				tray.Visible = false;
				if (tray.ContextMenuStrip != null)
				{
					tray.ContextMenuStrip.Dispose();
				}
				tray.Dispose();
			}
			engine.Dispose();
		}
		base.Dispose(disposing);
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		WindowChrome.ApplyDark(base.Handle);
	}
}
