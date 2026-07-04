namespace SonicRelay.SignalingClient;

public sealed record ClientOptions(Uri BaseUrl)
{
    public static ClientOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new ClientOptions(new Uri("http://localhost:8080"));

        if (args.Length != 2 || !string.Equals(args[0], "--base-url", StringComparison.Ordinal))
            throw new ArgumentException("Usage: --base-url <absolute HTTP(S) URL>.");

        if (!Uri.TryCreate(args[1], UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme is not ("http" or "https"))
            throw new ArgumentException("Base URL must be an absolute HTTP(S) URL.");

        return new ClientOptions(baseUrl);
    }
}
