namespace TRELAY;

internal sealed class SubscriptionIdentityRequiredException : ShareLinkResolutionException
{
	public string Host { get; private set; }

	public SubscriptionIdentityRequiredException(string host)
		: base("Провайдер подписки требует постоянный идентификатор устройства.")
	{
		Host = host;
	}
}
