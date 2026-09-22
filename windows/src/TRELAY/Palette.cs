using System.Drawing;

namespace TRELAY;

internal sealed class Palette
{
	public Color Background;

	public Color Card;

	public Color Input;

	public Color Hover;

	public Color Text;

	public Color Muted;

	public Color Accent;

	public Color Secondary;

	public Color Success;

	public Color Danger;

	public Color Stroke;

	public string FontName;

	public static Palette Soft()
	{
		Palette palette = new Palette();
		palette.Background = Color.FromArgb(23, 25, 32);
		palette.Card = Color.FromArgb(32, 35, 43);
		palette.Input = Color.FromArgb(43, 47, 57);
		palette.Hover = Color.FromArgb(55, 60, 72);
		palette.Text = Color.FromArgb(242, 244, 247);
		palette.Muted = Color.FromArgb(157, 167, 181);
		palette.Accent = Color.FromArgb(228, 137, 80);
		palette.Secondary = Color.FromArgb(239, 180, 130);
		palette.Success = Color.FromArgb(124, 204, 178);
		palette.Danger = Color.FromArgb(240, 143, 143);
		palette.Stroke = Color.FromArgb(65, 71, 84);
		palette.FontName = "Segoe UI";
		return palette;
	}
}
