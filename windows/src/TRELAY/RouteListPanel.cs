using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class RouteListPanel : FlowLayoutPanel
{
	private int offset;

	private int contentHeight;

	private bool arranging;

	private bool dragging;

	private int dragY;

	private int dragOffset;

	private int Limit => Math.Max(0, contentHeight - base.ClientSize.Height);

	private int RailWidth => Math.Max(10, (int)(10f * DpiScaler.Factor(FindForm())));

	private Rectangle Thumb
	{
		get
		{
			int num = Math.Min(base.Height, Math.Max(28, (contentHeight == 0) ? base.Height : (base.Height * base.Height / contentHeight)));
			return new Rectangle(base.Width - RailWidth + RailWidth / 3, (Limit != 0) ? (offset * (base.Height - num) / Limit) : 0, Math.Max(3, RailWidth / 3), num);
		}
	}

	public RouteListPanel()
	{
		DoubleBuffered = true;
		AutoScroll = false;
	}

	protected override void OnLayout(LayoutEventArgs e)
	{
		if (arranging)
		{
			return;
		}
		arranging = true;
		try
		{
			base.OnLayout(e);
			contentHeight = base.Controls.Cast<Control>().Sum((Control c) => c.Height + c.Margin.Vertical);
			offset = Math.Max(0, Math.Min(offset, Limit));
			int num = -offset;
			foreach (Control control in base.Controls)
			{
				control.SetBounds(0, num + control.Margin.Top, Math.Max(1, base.ClientSize.Width - RailWidth - 6), control.Height);
				num += control.Height + control.Margin.Vertical;
			}
		}
		finally
		{
			arranging = false;
		}
		Invalidate();
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		if (Limit > 0)
		{
			using (SolidBrush brush = new SolidBrush(dragging ? Theme.Current.Secondary : Theme.Current.Muted))
			{
				e.Graphics.FillRectangle(brush, Thumb);
			}
		}
	}

	protected override void OnMouseWheel(MouseEventArgs e)
	{
		offset = Math.Max(0, Math.Min(Limit, offset - e.Delta * Math.Max(1, RailWidth / 5)));
		PerformLayout();
		if (e is HandledMouseEventArgs e2)
		{
			e2.Handled = true;
		}
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		if (e.Button == MouseButtons.Left && Limit != 0 && e.X >= base.Width - RailWidth)
		{
			if (!Thumb.Contains(e.Location))
			{
				offset = Math.Max(0, Math.Min(Limit, offset + ((e.Y < Thumb.Top) ? (-base.Height) : base.Height)));
				PerformLayout();
			}
			dragging = true;
			dragY = e.Y;
			dragOffset = offset;
			base.Capture = true;
		}
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (dragging)
		{
			offset = Math.Max(0, Math.Min(Limit, dragOffset + (e.Y - dragY) * Limit / Math.Max(1, base.Height - Thumb.Height)));
			PerformLayout();
		}
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		base.OnMouseUp(e);
		dragging = false;
		base.Capture = false;
		Invalidate();
	}
}
