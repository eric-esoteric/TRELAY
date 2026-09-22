namespace TRELAY;

internal static partial class BuildSecrets
{
	public static string KeyBootstrapSecret
	{
		get
		{
			string value = "";
			ProvideKeyBootstrapSecret(ref value);
			return value;
		}
	}

	static partial void ProvideKeyBootstrapSecret(ref string value);
}
