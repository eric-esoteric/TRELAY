using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class ToggleSwitch : Control
{
	private sealed class SwitchAccessibleObject : ControlAccessibleObject
	{
		private readonly ToggleSwitch owner;

		public override AccessibleStates State => (AccessibleStates)((int)base.State | (owner.Checked ? 16 : 0));

		public override string DefaultAction => "Переключить";

		public SwitchAccessibleObject(ToggleSwitch owner)
			: base(owner)
		{
			this.owner = owner;
		}

		public override void DoDefaultAction()
		{
			if (owner.Enabled)
			{
				owner.OnClick(EventArgs.Empty);
			}
		}
	}

	private bool isChecked;

	private float position;

	private readonly Timer animationTimer;

	public bool Checked
	{
		get
		{
			return isChecked;
		}
		set
		{
			if (isChecked != value)
			{
				isChecked = value;
				if (!base.IsHandleCreated)
				{
					position = (isChecked ? 1f : 0f);
				}
				else
				{
					animationTimer.Start();
				}
				Invalidate();
				if (this.CheckedChanged != null)
				{
					this.CheckedChanged(this, EventArgs.Empty);
				}
			}
		}
	}

	public event EventHandler CheckedChanged;

	public ToggleSwitch()
	{
		base.Size = new Size(48, 28);
		base.TabStop = true;
		base.AccessibleRole = AccessibleRole.CheckButton;
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		animationTimer = new Timer
		{
			Interval = 15
		};
		Timer timer = animationTimer;
		EventHandler value = delegate
		{
			float num = (isChecked ? 1f : 0f);
			position += (num - position) * 0.28f;
			if (Math.Abs(num - position) < 0.012f)
			{
				position = num;
				animationTimer.Stop();
			}
			Invalidate();
		};
		timer.Tick += value;
	}

	protected override void OnClick(EventArgs e)
	{
		if (base.Enabled)
		{
			Checked = !Checked;
			base.OnClick(e);
		}
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Space)
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
		base.OnKeyDown(e);
	}

	protected override void OnKeyUp(KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Space && base.Enabled)
		{
			OnClick(EventArgs.Empty);
			e.Handled = true;
		}
		base.OnKeyUp(e);
	}

	protected override void OnGotFocus(EventArgs e)
	{
		Invalidate();
		base.OnGotFocus(e);
	}

	protected override void OnLostFocus(EventArgs e)
	{
		Invalidate();
		base.OnLostFocus(e);
	}

	protected override AccessibleObject CreateAccessibilityInstance()
	{
		return new SwitchAccessibleObject(this);
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		Rectangle rectangle = new Rectangle(0, 2, base.Width - 1, base.Height - 4);
		using (GraphicsPath path = RoundedPanel.RoundedPath(rectangle, rectangle.Height / 2))
		{
			using SolidBrush brush = new SolidBrush(Blend(Theme.Current.Input, Theme.Current.Success, position));
			e.Graphics.FillPath(brush, path);
		}
		int num = base.Height - 10;
		float num2 = 5f + (float)(base.Width - num - 10) * position;
		using (SolidBrush brush2 = new SolidBrush(Blend(Theme.Current.Muted, Color.FromArgb(24, 35, 32), position)))
		{
			e.Graphics.FillEllipse(brush2, num2, 5f, num, num);
		}
		if (Focused && ShowFocusCues)
		{
			ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -2, -2), Theme.Current.Text, BackColor);
		}
	}

	private static Color Blend(Color from, Color to, float amount)
	{
		return Color.FromArgb((int)((float)(int)from.A + (float)(to.A - from.A) * amount), (int)((float)(int)from.R + (float)(to.R - from.R) * amount), (int)((float)(int)from.G + (float)(to.G - from.G) * amount), (int)((float)(int)from.B + (float)(to.B - from.B) * amount));
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			animationTimer.Dispose();
		}
		base.Dispose(disposing);
	}
}
