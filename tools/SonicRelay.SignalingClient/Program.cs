using SonicRelay.SignalingClient;

try
{
    var options = ClientOptions.Parse(args);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await new SignalingClient(options.BaseUrl, Console.Out).RunAsync(timeout.Token);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}
