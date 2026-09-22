using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class VpsSetupDialog : Form
{
	private readonly AppConfig config;

	private readonly SettingsStore store;

	private readonly VpsProvisioner provisioner = new VpsProvisioner();

	private readonly TextBox nameBox = new PaddedTextBox();

	private readonly TextBox countryBox = new PaddedTextBox();

	private readonly TextBox hostBox = new PaddedTextBox();

	private readonly TextBox sshPortBox = new PaddedTextBox();

	private readonly TextBox userBox = new PaddedTextBox();

	private readonly TextBox passwordBox = new PaddedTextBox();

	private readonly TextBox tunnelPortBox = new PaddedTextBox();

	private readonly Label status = new Label();

	private readonly ToggleSwitch trustBox = new ToggleSwitch();

	private readonly Label trustLabel = new Label();

	private readonly RoundedButton auditButton;

	private readonly RoundedButton configureButton;

	private string auditedFingerprint;

	private string auditedHost;

	private string auditedUser;

	private bool busy;

	private bool provisioning;

	private bool auditedReady;

	private bool closing;

	private int operation;

	public string ProvisionedKey { get; private set; }

	public string RecoverySecretId { get; private set; }

	public string ProfileName => nameBox.Text.Trim();

	public string Country => countryBox.Text.Trim();

	public VpsSetupDialog(AppConfig config, SettingsStore store)
	{
		this.config = config;
		this.store = store;
		Text = "Подключить свой VPS — TRELAY";
		base.ClientSize = new Size(720, 548);
		base.StartPosition = FormStartPosition.CenterParent;
		BackColor = Theme.Current.Background;
		ForeColor = Theme.Current.Text;
		Font = Theme.Font(10f, FontStyle.Regular);
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = false;
		base.MinimizeBox = false;
		base.Icon = AppIcon.Current;
		base.AutoScaleMode = AutoScaleMode.None;
		Label label = Theme.Label("Свой VPS для телефонии", 17f, strong: true, Theme.Current.Text);
		label.SetBounds(28, 20, 560, 32);
		base.Controls.Add(label);
		Label label2 = Theme.Label("Создаст отдельный TCP + UDP маршрут. Amnezia и другие контейнеры не трогает.", 9.1f, strong: false, Theme.Current.Muted);
		label2.SetBounds(30, 54, 650, 23);
		base.Controls.Add(label2);
		RoundedPanel roundedPanel = new RoundedPanel
		{
			BackColor = Theme.Current.Card,
			Radius = 17
		};
		roundedPanel.SetBounds(28, 90, 664, 338);
		base.Controls.Add(roundedPanel);
		AddLabel(roundedPanel, "Название", 20, 18);
		Style(nameBox, 20, 42, 300);
		nameBox.Text = "Телефония";
		roundedPanel.Controls.Add(nameBox);
		AddLabel(roundedPanel, "Страна выхода", 340, 18);
		countryBox.SetBounds(340, 42, 300, 32);
		countryBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
		countryBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
		countryBox.AutoCompleteCustomSource.AddRange(new string[6] { "Россия", "США", "Германия", "Нидерланды", "Армения", "Другая" });
		countryBox.Text = "Россия";
		Theme.StyleInput(countryBox);
		roundedPanel.Controls.Add(countryBox);
		AddLabel(roundedPanel, "IP или домен VPS", 20, 88);
		Style(hostBox, 20, 112, 300);
		roundedPanel.Controls.Add(hostBox);
		AddLabel(roundedPanel, "SSH-порт", 340, 88);
		Style(sshPortBox, 340, 112, 140);
		sshPortBox.Text = "22";
		roundedPanel.Controls.Add(sshPortBox);
		AddLabel(roundedPanel, "Порт туннеля", 500, 88);
		Style(tunnelPortBox, 500, 112, 140);
		tunnelPortBox.Text = "24443";
		roundedPanel.Controls.Add(tunnelPortBox);
		AddLabel(roundedPanel, "SSH-логин", 20, 158);
		Style(userBox, 20, 182, 300);
		userBox.Text = "root";
		roundedPanel.Controls.Add(userBox);
		AddLabel(roundedPanel, "Пароль — только на время настройки", 340, 158);
		Style(passwordBox, 340, 182, 300);
		passwordBox.UseSystemPasswordChar = true;
		roundedPanel.Controls.Add(passwordBox);
		auditButton = Theme.Button("1  Проверить VPS", accent: false);
		auditButton.SetBounds(20, 236, 190, 42);
		RoundedButton roundedButton = auditButton;
		EventHandler value = delegate
		{
			StartAudit();
		};
		roundedButton.Click += value;
		roundedPanel.Controls.Add(auditButton);
		trustBox.SetBounds(226, 242, 44, 26);
		trustBox.Enabled = false;
		trustBox.CheckedChanged += delegate
		{
			RefreshButtons();
			if (trustBox.Checked && !busy && !auditedReady && !string.IsNullOrWhiteSpace(auditedFingerprint))
			{
				StartAudit();
			}
		};
		roundedPanel.Controls.Add(trustBox);
		trustLabel.Text = "Сверил отпечаток SSH-сервера и доверяю ему";
		trustLabel.AutoSize = false;
		trustLabel.SetBounds(282, 238, 358, 34);
		trustLabel.Font = Theme.Font(8.7f, FontStyle.Regular);
		trustLabel.ForeColor = Theme.Current.Muted;
		trustLabel.BackColor = Color.Transparent;
		trustLabel.Click += delegate
		{
			if (trustBox.Enabled)
			{
				trustBox.Checked = !trustBox.Checked;
			}
		};
		roundedPanel.Controls.Add(trustLabel);
		status.AutoSize = false;
		status.SetBounds(20, 288, 620, 38);
		status.Font = Theme.Font(8.7f, FontStyle.Regular);
		status.ForeColor = Theme.Current.Muted;
		status.BackColor = Color.Transparent;
		status.Text = "Сначала будет только проверка. Изменения начнутся после подтверждения отпечатка.";
		roundedPanel.Controls.Add(status);
		Label label3 = Theme.Label("Пароль не сохраняется. Ключ маршрута хранится через Windows DPAPI.\nКонфиг VPS резервируется перед заменой.", 8.5f, strong: false, Theme.Current.Muted);
		label3.AutoSize = false;
		label3.SetBounds(30, 442, 650, 38);
		base.Controls.Add(label3);
		RoundedButton roundedButton2 = Theme.Button("Отмена", accent: false);
		roundedButton2.SetBounds(350, 490, 130, 42);
		roundedButton2.Click += delegate
		{
			Close();
		};
		base.Controls.Add(roundedButton2);
		configureButton = Theme.Button("2  Настроить", accent: true);
		configureButton.SetBounds(492, 490, 200, 42);
		configureButton.Click += delegate
		{
			StartProvision();
		};
		base.Controls.Add(configureButton);
		hostBox.TextChanged += delegate
		{
			InvalidateAudit();
		};
		sshPortBox.TextChanged += delegate
		{
			InvalidateAudit();
		};
		userBox.TextChanged += delegate
		{
			InvalidateAudit();
		};
		passwordBox.TextChanged += delegate
		{
			if (!busy)
			{
				InvalidateAudit();
			}
		};
		trustBox.AccessibleName = "Доверять показанному отпечатку SSH-сервера";
		RefreshButtons();
		DpiScaler.Apply(this);
	}

	private void StartAudit()
	{
		if (busy)
		{
			return;
		}
		if (!TryReadPort(sshPortBox, 1, out var sshPort))
		{
			SetStatus("! Укажи корректный SSH-порт.", error: true);
			return;
		}
		string host = hostBox.Text.Trim();
		string user = userBox.Text.Trim();
		string password = passwordBox.Text;
		config.TrustedSshHosts.TryGetValue(HostId(host, sshPort), out var pinned);
		if (trustBox.Checked && auditedHost == HostId(host, sshPort) && auditedUser == user)
		{
			pinned = auditedFingerprint;
		}
		busy = true;
		auditedReady = false;
		int ticket = ++operation;
		SetStatus(string.IsNullOrWhiteSpace(pinned) ? "Получаю отпечаток SSH. Пароль пока не отправляется…" : "Проверяю SSH, Docker и Xray…", error: false);
		RefreshButtons();
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				if (string.IsNullOrWhiteSpace(pinned))
				{
					string discovered = provisioner.DiscoverFingerprint(host, sshPort, user);
					PostResult(ticket, delegate
					{
						auditedFingerprint = discovered;
						auditedHost = HostId(host, sshPort);
						auditedUser = user;
						trustLabel.Text = "Доверяю SSH: " + discovered;
						busy = false;
						trustBox.Enabled = true;
						trustBox.Checked = false;
						SetStatus("Сверь отпечаток с панелью VPS. После подтверждения проверю сервер с паролем.", error: false);
						RefreshButtons();
					});
				}
				else
				{
					VpsAudit result = provisioner.Audit(host, sshPort, user, password, pinned);
					PostResult(ticket, delegate
					{
						auditedFingerprint = result.Fingerprint;
						auditedHost = HostId(host, sshPort);
						auditedUser = user;
						auditedReady = true;
						trustBox.Enabled = true;
						trustBox.Checked = true;
						trustLabel.Text = "Доверяю SSH: " + result.Fingerprint;
						SetStatus("✓ VPS готов к настройке. " + result.Summary.Replace(Environment.NewLine, " · "), error: false);
						busy = false;
						RefreshButtons();
					});
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				PostResult(ticket, delegate
				{
					SetStatus("! " + ex2.Message, error: true);
					busy = false;
					RefreshButtons();
				});
			}
		});
	}

	private void StartProvision()
	{
		if (busy)
		{
			return;
		}
		if (!string.IsNullOrWhiteSpace(ProvisionedKey))
		{
			FinishProvision();
		}
		else
		{
			if (!auditedReady || string.IsNullOrWhiteSpace(auditedFingerprint) || !trustBox.Checked)
			{
				return;
			}
			if (string.IsNullOrWhiteSpace(ProfileName) || string.IsNullOrWhiteSpace(Country))
			{
				SetStatus("! Заполни название и страну выхода.", error: true);
				return;
			}
			if (!TryReadPort(sshPortBox, 1, out var sshPort) || !TryReadPort(tunnelPortBox, 1025, out var tunnelPort))
			{
				SetStatus("! Проверь SSH-порт и порт туннеля.", error: true);
				return;
			}
			busy = true;
			provisioning = true;
			SetStatus("Настраиваю VPS. Дождись завершения или подтверждённого отката…", error: false);
			RefreshButtons();
			string host = hostBox.Text.Trim();
			string user = userBox.Text.Trim();
			string password = passwordBox.Text;
			string fingerprint = auditedFingerprint;
			RecoverySecretId = "vps-recovery-" + Guid.NewGuid().ToString("N");
			string recoveryId = RecoverySecretId;
			int ticket = ++operation;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					VpsProvisionResult result = provisioner.ProvisionWithRecovery(host, sshPort, user, password, tunnelPort, fingerprint, delegate(string key)
					{
						new SecretStore(store).Save(recoveryId, key);
					});
					PostResult(ticket, delegate
					{
						ProvisionedKey = result.ShareKey;
						config.TrustedSshHosts[HostId(host, sshPort)] = result.Fingerprint;
						busy = false;
						provisioning = false;
						FinishProvision();
					});
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					PostResult(ticket, delegate
					{
						passwordBox.Clear();
						SetStatus("! " + ex2.Message, error: true);
						busy = false;
						provisioning = false;
						auditedReady = false;
						auditedFingerprint = null;
						trustBox.Checked = false;
						trustBox.Enabled = false;
						RefreshButtons();
					});
				}
			});
		}
	}

	private void RefreshButtons()
	{
		auditButton.Enabled = !busy;
		configureButton.Enabled = !busy && (!string.IsNullOrWhiteSpace(ProvisionedKey) || (auditedReady && !string.IsNullOrWhiteSpace(auditedFingerprint) && trustBox.Checked));
		configureButton.BackColor = (configureButton.Enabled ? Theme.Current.Accent : Theme.Current.Hover);
		Control[] array = new Control[7] { nameBox, countryBox, hostBox, sshPortBox, userBox, passwordBox, tunnelPortBox };
		foreach (Control control in array)
		{
			control.Enabled = !busy && string.IsNullOrWhiteSpace(ProvisionedKey);
		}
		trustBox.Enabled = !busy && !string.IsNullOrWhiteSpace(auditedFingerprint) && string.IsNullOrWhiteSpace(ProvisionedKey);
	}

	private void FinishProvision()
	{
		passwordBox.Clear();
		try
		{
			store.Save(config);
		}
		catch (Exception ex)
		{
			configureButton.Text = "Сохранить результат";
			SetStatus("VPS настроен, ключ защищён локально. Не удалось сохранить настройки: " + ex.Message, error: true);
			RefreshButtons();
			return;
		}
		base.DialogResult = DialogResult.OK;
		Close();
	}

	private void InvalidateAudit()
	{
		if (!busy)
		{
			auditedReady = false;
			auditedFingerprint = null;
			auditedHost = null;
			auditedUser = null;
			trustBox.Checked = false;
			trustBox.Enabled = false;
			RefreshButtons();
		}
	}

	private void PostResult(int ticket, Action callback)
	{
		if (closing || base.IsDisposed || !base.IsHandleCreated)
		{
			return;
		}
		try
		{
			BeginInvoke((Action)delegate
			{
				if (!closing && !base.IsDisposed && ticket == operation)
				{
					callback();
				}
			});
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void SetStatus(string text, bool error)
	{
		status.Text = text;
		status.ForeColor = (error ? Theme.Current.Danger : Theme.Current.Muted);
	}

	private static string HostId(string host, int port)
	{
		return (host ?? "").Trim().ToLowerInvariant() + ":" + port;
	}

	private static bool TryReadPort(TextBox box, int minimum, out int port)
	{
		if (int.TryParse(box.Text, out port) && port >= minimum)
		{
			return port <= 65535;
		}
		return false;
	}

	private static void Style(TextBox box, int x, int y, int width)
	{
		box.BorderStyle = BorderStyle.FixedSingle;
		box.SetBounds(x, y, width, 32);
		Theme.StyleInput(box);
	}

	private static void AddLabel(Control parent, string text, int x, int y)
	{
		Label label = Theme.Label(text, 8.7f, strong: false, Theme.Current.Muted);
		label.SetBounds(x, y, 300, 22);
		parent.Controls.Add(label);
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		if (busy && provisioning && e.CloseReason == CloseReason.UserClosing)
		{
			e.Cancel = true;
			SetStatus("VPS ещё настраивается. Закрытие станет доступно после результата или отката.", error: false);
		}
		base.OnFormClosing(e);
		if (!e.Cancel)
		{
			closing = true;
			operation++;
		}
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		passwordBox.Clear();
		base.OnFormClosed(e);
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		WindowChrome.ApplyDark(base.Handle);
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
}
