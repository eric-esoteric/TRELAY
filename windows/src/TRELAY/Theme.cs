using System.Drawing;
using System.Windows.Forms;

namespace TRELAY;

internal static class Theme
{
	public static Palette Current = Palette.Soft();

	public static void Select(string name)
	{
		Current = Palette.Soft();
	}

	public static Font Font(float size, FontStyle style)
	{
		return new Font(Current.FontName, size, style, GraphicsUnit.Point);
	}

	public static Font SymbolFont(float size, FontStyle style)
	{
		return new Font("Segoe UI Symbol", size, style, GraphicsUnit.Point);
	}

	public static RoundedButton Button(string text, bool accent)
	{
		RoundedButton roundedButton = new RoundedButton();
		roundedButton.Radius = 10;
		RoundedButton roundedButton2 = roundedButton;
		roundedButton2.Text = text;
		roundedButton2.BackColor = (accent ? Current.Accent : Current.Input);
		roundedButton2.ForeColor = (accent ? Color.Black : Current.Text);
		roundedButton2.Font = Font(10.5f, FontStyle.Bold);
		roundedButton2.Height = 42;
		return roundedButton2;
	}

	public static Label Label(string text, float size, bool strong, Color color)
	{
		Label label = new Label();
		label.Text = text;
		label.ForeColor = color;
		label.BackColor = Color.Transparent;
		label.Font = Font(size, strong ? FontStyle.Bold : FontStyle.Regular);
		label.AutoSize = true;
		return label;
	}

	public static void StyleInput(Control control)
	{
		control.BackColor = Current.Input;
		control.ForeColor = Current.Text;
		control.Font = Font(10.5f, FontStyle.Regular);
	}
}
