using System;
using System.Drawing;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class ConnectionChoiceDialog : Form
{
	public ConnectionChoice Choice { get; private set; }

	public ConnectionChoiceDialog()
	{
		Text = "Новый маршрут — TRELAY";
		base.ClientSize = new Size(620, 296);
		base.StartPosition = FormStartPosition.CenterParent;
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = false;
		base.MinimizeBox = false;
		BackColor = Theme.Current.Background;
		ForeColor = Theme.Current.Text;
		base.Icon = AppIcon.Current;
		base.AutoScaleMode = AutoScaleMode.None;
		Label label = Theme.Label("Как подключаемся?", 17f, strong: true, Theme.Current.Text);
		label.SetBounds(28, 22, 500, 32);
		base.Controls.Add(label);
		Label label2 = Theme.Label("Выберите готовый доступ или настройте собственный сервер.", 8.8f, strong: false, Theme.Current.Muted);
		label2.SetBounds(30, 57, 560, 24);
		base.Controls.Add(label2);
		RoundedPanel roundedPanel = ChoiceCard("Готовый ключ или файл", "VLESS, VMess, Trojan, Shadowsocks или Xray JSON", 28, 98);
		EventHandler value = delegate
		{
			Select(ConnectionChoice.Existing);
		};
		roundedPanel.Click += value;
		base.Controls.Add(roundedPanel);
		RoundedPanel roundedPanel2 = ChoiceCard("Мой VPS", "Подключимся по SSH и создадим отдельный TCP + UDP маршрут", 316, 98);
		roundedPanel2.Click += delegate
		{
			Select(ConnectionChoice.Vps);
		};
		base.Controls.Add(roundedPanel2);
		foreach (Control control3 in roundedPanel.Controls)
		{
			control3.Click += delegate
			{
				Select(ConnectionChoice.Existing);
			};
		}
		foreach (Control control4 in roundedPanel2.Controls)
		{
			control4.Click += delegate
			{
				Select(ConnectionChoice.Vps);
			};
		}
		RoundedButton roundedButton = Theme.Button("Отмена", accent: false);
		roundedButton.SetBounds(442, 238, 150, 40);
		roundedButton.Click += delegate
		{
			Close();
		};
		base.Controls.Add(roundedButton);
		DpiScaler.Apply(this);
	}

	private RoundedPanel ChoiceCard(string heading, string text, int x, int y)
	{
		RoundedPanel roundedPanel = new RoundedPanel();
		roundedPanel.BackColor = Theme.Current.Card;
		roundedPanel.Radius = 16;
		RoundedPanel roundedPanel2 = roundedPanel;
		roundedPanel2.SetBounds(x, y, 276, 116);
		Label label = Theme.Label(heading, 10.5f, strong: true, Theme.Current.Text);
		label.SetBounds(18, 19, 238, 25);
		roundedPanel2.Controls.Add(label);
		Label label2 = Theme.Label(text, 8.5f, strong: false, Theme.Current.Muted);
		label2.AutoSize = false;
		label2.SetBounds(18, 53, 238, 48);
		roundedPanel2.Controls.Add(label2);
		return roundedPanel2;
	}

	private void Select(ConnectionChoice choice)
	{
		Choice = choice;
		base.DialogResult = DialogResult.OK;
		Close();
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		WindowChrome.ApplyDark(base.Handle);
	}
}
