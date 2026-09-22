using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TRELAY;

internal class RoundedButton : Control
{
	private bool hovered;

	private bool pressed;

	public int Radius { get; set; }

	public int IconSize { get; set; }

	public ButtonIconKind IconKind { get; set; }

	public RoundedButton()
	{
		Radius = 12;
		IconSize = 18;
		base.Padding = Padding.Empty;
		base.TabStop = true;
		base.AccessibleRole = AccessibleRole.PushButton;
		SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
	}

	protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
	{
		Radius = Math.Max(2, (int)Math.Round((float)Radius * factor.Width));
		IconSize = Math.Max(8, (int)Math.Round((float)IconSize * factor.Width));
		base.ScaleControl(factor, specified);
	}

	protected override void OnMouseEnter(EventArgs e)
	{
		hovered = true;
		Invalidate();
		base.OnMouseEnter(e);
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		hovered = false;
		pressed = false;
		Invalidate();
		base.OnMouseLeave(e);
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Left)
		{
			pressed = true;
		}
		Invalidate();
		base.OnMouseDown(e);
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		pressed = false;
		Invalidate();
		base.OnMouseUp(e);
	}

	protected override void OnGotFocus(EventArgs e)
	{
		Invalidate();
		base.OnGotFocus(e);
	}

	protected override void OnLostFocus(EventArgs e)
	{
		pressed = false;
		Invalidate();
		base.OnLostFocus(e);
	}

	protected override void OnPaintBackground(PaintEventArgs pevent)
	{
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Return)
		{
			pressed = true;
			Invalidate();
			e.Handled = true;
		}
		base.OnKeyDown(e);
	}

	protected override void OnKeyUp(KeyEventArgs e)
	{
		if (pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Return))
		{
			pressed = false;
			Invalidate();
			OnClick(EventArgs.Empty);
			e.Handled = true;
		}
		base.OnKeyUp(e);
	}

	public void PerformClick()
	{
		if (base.Enabled)
		{
			OnClick(EventArgs.Empty);
		}
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Color color = ((base.Parent == null) ? Theme.Current.Background : base.Parent.BackColor);
		e.Graphics.Clear(color);
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		Color color2 = (base.Enabled ? BackColor : Theme.Current.Hover);
		if (base.Enabled && hovered)
		{
			color2 = Blend(color2, Color.White, 0.075f);
		}
		if (base.Enabled && pressed)
		{
			color2 = Blend(color2, Color.Black, 0.055f);
		}
		using (GraphicsPath path = RoundedPanel.RoundedPath(new Rectangle(0, 0, base.Width, base.Height), Radius))
		{
			using SolidBrush brush = new SolidBrush(color2);
			e.Graphics.FillPath(brush, path);
		}
		Color color3 = (base.Enabled ? ForeColor : Color.FromArgb(125, Theme.Current.Muted));
		int num = ((IconKind != ButtonIconKind.None) ? IconSize : 0);
		int num2 = ((num > 0 && !string.IsNullOrWhiteSpace(Text)) ? Math.Max(6, IconSize / 3) : 0);
		Size size = (string.IsNullOrWhiteSpace(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding));
		int num3 = num + num2 + size.Width;
		int num4 = (base.Width - num3) / 2;
		if (num > 0)
		{
			DrawIcon(e.Graphics, new Rectangle(num4, (base.Height - num) / 2, num, num), color3);
			num4 += num + num2;
		}
		if (size.Width > 0)
		{
			TextRenderer.DrawText(bounds: new Rectangle(num4, 0, size.Width + 2, base.Height), dc: e.Graphics, text: Text, font: Font, foreColor: color3, flags: TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
		if (Focused && ShowFocusCues)
		{
			ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -6, -6), color3, color2);
		}
	}

	private static Color Blend(Color from, Color to, float amount)
	{
		return Color.FromArgb((int)((float)(int)from.A + (float)(to.A - from.A) * amount), (int)((float)(int)from.R + (float)(to.R - from.R) * amount), (int)((float)(int)from.G + (float)(to.G - from.G) * amount), (int)((float)(int)from.B + (float)(to.B - from.B) * amount));
	}

	private void DrawIcon(Graphics graphics, Rectangle box, Color color)
	{
		using Pen pen = new Pen(color, Math.Max(1.6f, (float)box.Width / 10f));
		pen.StartCap = LineCap.Round;
		pen.EndCap = LineCap.Round;
		float num = (float)box.Left + (float)box.Width / 2f;
		float num2 = (float)box.Top + (float)box.Height / 2f;
		if (IconKind == ButtonIconKind.Plus)
		{
			graphics.DrawLine(pen, num, box.Top + 2, num, box.Bottom - 2);
			graphics.DrawLine(pen, box.Left + 2, num2, box.Right - 2, num2);
			return;
		}
		if (IconKind == ButtonIconKind.Delete)
		{
			graphics.DrawLine(pen, box.Left + 3, box.Top + 3, box.Right - 3, box.Bottom - 3);
			graphics.DrawLine(pen, box.Right - 3, box.Top + 3, box.Left + 3, box.Bottom - 3);
			return;
		}
		if (IconKind == ButtonIconKind.Edit)
		{
			PointF[] array = new PointF[4]
			{
				new PointF((float)box.Left + (float)box.Width * 0.2f, (float)box.Bottom - (float)box.Height * 0.26f),
				new PointF((float)box.Left + (float)box.Width * 0.34f, (float)box.Bottom - (float)box.Height * 0.12f),
				new PointF((float)box.Right - (float)box.Width * 0.12f, (float)box.Top + (float)box.Height * 0.34f),
				new PointF((float)box.Right - (float)box.Width * 0.26f, (float)box.Top + (float)box.Height * 0.2f)
			};
			using (SolidBrush brush = new SolidBrush(color))
			{
				graphics.FillPolygon(brush, array);
			}
			PointF[] points = new PointF[3]
			{
				new PointF((float)box.Left + (float)box.Width * 0.1f, (float)box.Bottom - (float)box.Height * 0.08f),
				array[0],
				array[1]
			};
			using SolidBrush brush2 = new SolidBrush(color);
			graphics.FillPolygon(brush2, points);
			return;
		}
		if (IconKind == ButtonIconKind.Settings)
		{
			float[] array2 = new float[3]
			{
				(float)box.Top + (float)box.Height * 0.25f,
				num2,
				(float)box.Top + (float)box.Height * 0.75f
			};
			float[] array3 = new float[3]
			{
				(float)box.Left + (float)box.Width * 0.32f,
				(float)box.Left + (float)box.Width * 0.68f,
				(float)box.Left + (float)box.Width * 0.43f
			};
			for (int i = 0; i < 3; i++)
			{
				graphics.DrawLine(pen, box.Left + 2, array2[i], box.Right - 2, array2[i]);
				using SolidBrush brush3 = new SolidBrush(color);
				graphics.FillEllipse(brush3, array3[i] - pen.Width, array2[i] - pen.Width, pen.Width * 2f, pen.Width * 2f);
			}
			return;
		}
		if (IconKind == ButtonIconKind.More)
		{
			using (SolidBrush brush4 = new SolidBrush(color))
			{
				for (int j = -1; j <= 1; j++)
				{
					graphics.FillEllipse(brush4, num + (float)(j * box.Width) * 0.28f - 2f, num2 - 2f, 4f, 4f);
				}
				return;
			}
		}
		if (IconKind == ButtonIconKind.Folder)
		{
			RectangleF rectangleF = new RectangleF(box.Left + 2, (float)box.Top + (float)box.Height * 0.3f, box.Width - 4, (float)box.Height * 0.55f);
			graphics.DrawRectangle(pen, rectangleF.X, rectangleF.Y, rectangleF.Width, rectangleF.Height);
			graphics.DrawLine(pen, box.Left + 3, rectangleF.Top, (float)box.Left + (float)box.Width * 0.43f, rectangleF.Top);
			graphics.DrawLine(pen, box.Left + 3, rectangleF.Top, (float)box.Left + (float)box.Width * 0.2f, (float)box.Top + (float)box.Height * 0.18f);
			graphics.DrawLine(pen, (float)box.Left + (float)box.Width * 0.2f, (float)box.Top + (float)box.Height * 0.18f, (float)box.Left + (float)box.Width * 0.48f, (float)box.Top + (float)box.Height * 0.18f);
		}
	}
}
