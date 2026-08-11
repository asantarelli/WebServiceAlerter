namespace WebServiceAlerter.Alerting;

public enum AlertKind
{
    Down,
    Recovered,
    StillDown,
    ServiceStarted,
    Test,
}

public sealed record AlertMessage
{
    public required AlertKind Kind { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
}

public interface IAlertSender
{
    Task<bool> SendAsync(AlertMessage message, CancellationToken cancellationToken);
}
