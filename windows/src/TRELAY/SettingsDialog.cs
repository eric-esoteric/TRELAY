using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class SettingsDialog : Form
{
	private sealed class AlertOutputOption
	{
		public string Value;

		public string Label;

		public override string ToString()
		{
			return Label;
		}
	}

	private readonly AppConfig config;

	private readonly SettingsStore store;

	private readonly TextBox xray = new PaddedTextBox();

	private readonly TextBox proxifyre = new PaddedTextBox();

	private readonly ToggleSwitch autostart = new ToggleSwitch();

	private readonly ToggleSwitch minimized = new ToggleSwitch();

	private readonly ToggleSwitch sound = new ToggleSwitch();

	private readonly ComboBox alertOutput = new ComboBox();

	public SettingsDialog(AppConfig config, SettingsStore store)
	{
		this.config = config;
		this.store = store;
		Text = "Настройки TRELAY";
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		base.AutoScaleMode = AutoScaleMode.Dpi;
		base.ClientSize = new Size(620, 540);
		base.StartPosition = FormStartPosition.CenterParent;
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = false;
		base.MinimizeBox = false;
		base.Icon = AppIcon.Current;
		BackColor = Theme.Current.Background;
		ForeColor = Theme.Current.Text;
		base.AutoScaleMode = AutoScaleMode.None;
		AddLabel("Настройки", 24, 18, 16f, Theme.Current.Text);
		RoundedPanel roundedPanel = new RoundedPanel
		{
			BackColor = Theme.Current.Card,
			Radius = 14
		};
		roundedPanel.SetBounds(24, 64, 572, 174);
		base.Controls.Add(roundedPanel);
		Label label = Theme.Label("Компоненты маршрутизации", 10f, strong: true, Theme.Current.Text);
		label.SetBounds(20, 13, 300, 24);
		roundedPanel.Controls.Add(label);
		Label label2 = Theme.Label("Xray.exe", 8.7f, strong: false, Theme.Current.Muted);
		label2.SetBounds(20, 43, 200, 22);
		roundedPanel.Controls.Add(label2);
		Input(roundedPanel, xray, config.XrayPath, 20, 65, 482);
		RoundedButton roundedButton = SmallButton();
		roundedButton.IconKind = ButtonIconKind.Folder;
		roundedButton.IconSize = 16;
		roundedButton.SetBounds(510, 65, 42, 34);
		EventHandler value = delegate
		{
			Browse(xray, "xray.exe");
		};
		roundedButton.Click += value;
		roundedPanel.Controls.Add(roundedButton);
		roundedButton.AccessibleName = "Выбрать Xray.exe";
		xray.AccessibleName = "Путь к Xray.exe";
		Label label3 = Theme.Label("ProxiFyre.exe", 8.7f, strong: false, Theme.Current.Muted);
		label3.SetBounds(20, 105, 200, 22);
		roundedPanel.Controls.Add(label3);
		Input(roundedPanel, proxifyre, config.ProxiFyrePath, 20, 127, 482);
		RoundedButton roundedButton2 = SmallButton();
		roundedButton2.IconKind = ButtonIconKind.Folder;
		roundedButton2.IconSize = 16;
		roundedButton2.SetBounds(510, 127, 42, 34);
		roundedButton2.Click += delegate
		{
			Browse(proxifyre, "ProxiFyre.exe");
		};
		roundedPanel.Controls.Add(roundedButton2);
		roundedButton2.AccessibleName = "Выбрать ProxiFyre.exe";
		proxifyre.AccessibleName = "Путь к ProxiFyre.exe";
		RoundedPanel roundedPanel2 = new RoundedPanel
		{
			BackColor = Theme.Current.Card,
			Radius = 14
		};
		roundedPanel2.SetBounds(24, 250, 572, 78);
		base.Controls.Add(roundedPanel2);
		Label label4 = Theme.Label("Запуск", 10f, strong: true, Theme.Current.Text);
		label4.SetBounds(20, 10, 160, 24);
		roundedPanel2.Controls.Add(label4);
		AddToggle(roundedPanel2, autostart, "Вместе с Windows", config.StartWithWindows, 20, 39);
		AddToggle(roundedPanel2, minimized, "Сразу в трей", config.StartMinimized, 290, 39);
		RoundedPanel roundedPanel3 = new RoundedPanel
		{
			BackColor = Theme.Current.Card,
			Radius = 14
		};
		roundedPanel3.SetBounds(24, 340, 572, 134);
		base.Controls.Add(roundedPanel3);
		Label label5 = Theme.Label("Оповещения", 10f, strong: true, Theme.Current.Text);
		label5.SetBounds(20, 10, 200, 24);
		roundedPanel3.Controls.Add(label5);
		AddToggle(roundedPanel3, sound, "Звук при обрыве связи", config.SoundAlerts, 20, 39);
		Label label6 = Theme.Label("Выход звука", 9.2f, strong: false, Theme.Current.Text);
		label6.SetBounds(20, 75, 145, 26);
		roundedPanel3.Controls.Add(label6);
		alertOutput.DropDownStyle = ComboBoxStyle.DropDownList;
		alertOutput.SetBounds(155, 72, 397, 32);
		Theme.StyleInput(alertOutput);
		alertOutput.AccessibleName = "Устройство для звуковых оповещений";
		alertOutput.Items.Add(new AlertOutputOption
		{
			Value = "speakers",
			Label = "Динамики (автоматически)"
		});
		alertOutput.Items.Add(new AlertOutputOption
		{
			Value = "default",
			Label = "Системный выход (включая наушники)"
		});
		foreach (AlertSound.OutputDevice outputDevice in AlertSound.GetOutputDevices())
		{
			alertOutput.Items.Add(new AlertOutputOption
			{
				Value = outputDevice.Name,
				Label = outputDevice.Name
			});
		}
		string text = config.AlertOutputDevice ?? "speakers";
		for (int num = 0; num < alertOutput.Items.Count; num++)
		{
			if (string.Equals(((AlertOutputOption)alertOutput.Items[num]).Value, text, StringComparison.OrdinalIgnoreCase))
			{
				alertOutput.SelectedIndex = num;
				break;
			}
		}
		if (alertOutput.SelectedIndex < 0)
		{
			alertOutput.Items.Add(new AlertOutputOption
			{
				Value = text,
				Label = text + " (не подключено)"
			});
			alertOutput.SelectedIndex = alertOutput.Items.Count - 1;
		}
		roundedPanel3.Controls.Add(alertOutput);
		RoundedButton roundedButton3 = Theme.Button("Проверить звук", accent: false);
		roundedButton3.SetBounds(400, 30, 152, 36);
		roundedButton3.Click += delegate
		{
			string value2 = ((AlertOutputOption)alertOutput.SelectedItem).Value;
			if (!AlertSound.IsOutputAvailable(value2))
			{
				MessageBox.Show(this, "Выбранный выход сейчас недоступен. Подключи динамики или выбери другой выход.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			}
			else
			{
				AlertSound.Play(critical: true, value2, force: true);
			}
		};
		roundedButton3.AccessibleName = "Проверить звук оповещения";
		roundedPanel3.Controls.Add(roundedButton3);
		Label label7 = Theme.Label("Ключи защищены Windows DPAPI", 8.5f, strong: false, Theme.Current.Muted);
		label7.SetBounds(24, 500, 330, 24);
		base.Controls.Add(label7);
		RoundedButton save = Theme.Button("Сохранить", accent: true);
		save.SetBounds(456, 490, 140, 38);
		save.Click += delegate
		{
			Save();
		};
		base.Controls.Add(save);
		base.Shown += delegate
		{
			save.Focus();
		};
		DpiScaler.Apply(this);
	}

	private void AddLabel(string text, int x, int y, float size, Color color)
	{
		Label label = Theme.Label(text, size, size >= 11f, color);
		label.SetBounds(x, y, 600, 27);
		base.Controls.Add(label);
	}

	private void Input(Control parent, TextBox box, string text, int x, int y, int width)
	{
		box.Text = text ?? "";
		box.BorderStyle = BorderStyle.FixedSingle;
		box.SetBounds(x, y, width, 34);
		Theme.StyleInput(box);
		parent.Controls.Add(box);
	}

	private RoundedButton SmallButton()
	{
		return Theme.Button("", accent: false);
	}

	private void AddToggle(Control parent, ToggleSwitch toggle, string text, bool value, int x, int y)
	{
		toggle.Checked = value;
		toggle.SetBounds(x, y, 44, 26);
		parent.Controls.Add(toggle);
		toggle.AccessibleName = text;
		Label label = Theme.Label(text, 9.2f, strong: false, Theme.Current.Text);
		label.SetBounds(x + 56, y + 2, 220, 24);
		label.Click += delegate
		{
			toggle.Checked = !toggle.Checked;
		};
		parent.Controls.Add(label);
	}

	private void Browse(TextBox target, string file)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog();
		openFileDialog.Filter = file + "|" + file + "|Applications|*.exe";
		openFileDialog.CheckFileExists = true;
		using OpenFileDialog openFileDialog2 = openFileDialog;
		if (openFileDialog2.ShowDialog(this) == DialogResult.OK)
		{
			target.Text = openFileDialog2.FileName;
		}
	}

	private void Save()
	{
		string xrayPath = config.XrayPath;
		string proxiFyrePath = config.ProxiFyrePath;
		bool startWithWindows = config.StartWithWindows;
		bool startMinimized = config.StartMinimized;
		bool soundAlerts = config.SoundAlerts;
		string alertOutputDevice = config.AlertOutputDevice;
		config.XrayPath = xray.Text.Trim();
		config.ProxiFyrePath = proxifyre.Text.Trim();
		config.StartWithWindows = autostart.Checked;
		config.StartMinimized = minimized.Checked;
		config.SoundAlerts = sound.Checked;
		config.AlertOutputDevice = ((AlertOutputOption)alertOutput.SelectedItem).Value;
		config.Theme = "Soft";
		try
		{
			if (startWithWindows != config.StartWithWindows)
			{
				store.SetAutostart(config.StartWithWindows);
			}
			store.Save(config);
			base.DialogResult = DialogResult.OK;
			Close();
		}
		catch (Exception)
		{
			if (startWithWindows != config.StartWithWindows)
			{
				try
				{
					store.SetAutostart(startWithWindows);
				}
				catch
				{
					MessageBox.Show(this, "Не удалось восстановить автозапуск. Проверьте его в настройках Windows.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				}
			}
			config.XrayPath = xrayPath;
			config.ProxiFyrePath = proxiFyrePath;
			config.StartWithWindows = startWithWindows;
			config.StartMinimized = startMinimized;
			config.SoundAlerts = soundAlerts;
			config.AlertOutputDevice = alertOutputDevice;
			MessageBox.Show(this, "Настройки не сохранены. Проверьте доступ к папке данных и свободное место.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
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
