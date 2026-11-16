namespace PaymentServices.EventArchive.UnitTests.TestSupport;

/// <summary>A clock that always reports the same instant.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
