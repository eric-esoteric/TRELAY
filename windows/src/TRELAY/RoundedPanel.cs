using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TRELAY;

internal class RoundedPanel : Panel
{
	public int Radius { get; set; }

	public RoundedPanel()
	{
		DoubleBuffered = true;
		Radius = 18;
		EventHandler value = delegate
		{
			ApplyRegion();
		};
		base.Resize += value;
	}

	private void ApplyRegion()
	{
		if (base.Width >= 2 && base.Height >= 2)
		{
			Region region = base.Region;
			using (GraphicsPath path = RoundedPath(base.ClientRectangle, Radius))
			{
				base.Region = new Region(path);
			}
			region?.Dispose();
		}
	}

	protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
	{
		Radius = Math.Max(2, (int)Math.Round((float)Radius * factor.Width));
		base.ScaleControl(factor, specified);
	}

	internal static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
	{
		int num = Math.Max(2, radius * 2);
		GraphicsPath graphicsPath = new GraphicsPath();
		graphicsPath.AddArc(rectangle.Left, rectangle.Top, num, num, 180f, 90f);
		graphicsPath.AddArc(rectangle.Right - num - 1, rectangle.Top, num, num, 270f, 90f);
		graphicsPath.AddArc(rectangle.Right - num - 1, rectangle.Bottom - num - 1, num, num, 0f, 90f);
		graphicsPath.AddArc(rectangle.Left, rectangle.Bottom - num - 1, num, num, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}
}
