using System.Net;

namespace FrameShift.Core;

public sealed class RefreshSchedule
{
    private int failures;
    public int IntervalMinutes { get; private set; }
    public DateTimeOffset? NextDueUtc { get; private set; }
    public bool RetryPending { get; private set; }

    public RefreshSchedule(int intervalMinutes) => IntervalMinutes = Settings.NormalizeInterval(intervalMinutes);

    public void Configure(int intervalMinutes, DateTimeOffset now)
    {
        IntervalMinutes = Settings.NormalizeInterval(intervalMinutes);
        RecordSuccess(now);
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        failures = 0;
        RetryPending = false;
        NextDueUtc = RegularDue(now);
    }

    public void RecordFailure(Exception error, DateTimeOffset now)
    {
        var delay = RetryDelay(error, failures + 1);
        failures = delay is null ? 0 : Math.Min(failures + 1, 4);
        RetryPending = IntervalMinutes != 0 && delay is not null;
        NextDueUtc = RetryPending ? now + delay!.Value : RegularDue(now);
    }

    private DateTimeOffset? RegularDue(DateTimeOffset now) => IntervalMinutes == 0 ? null : now.AddMinutes(IntervalMinutes);

    public static TimeSpan? RetryDelay(Exception error, int failureCount)
    {
        var transient = error switch
        {
            PhotoSourceException source => source.IsTransient,
            HttpRequestException http => http.StatusCode is null || http.StatusCode == HttpStatusCode.RequestTimeout ||
                http.StatusCode == HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
            OperationCanceledException => true, // Application shutdown is filtered before scheduling.
            _ => false
        };
        if (!transient) return null;
        var minutes = new[] { 1, 2, 5, 15 }[Math.Clamp(failureCount - 1, 0, 3)];
        var delay = TimeSpan.FromMinutes(minutes);
        if (error is PhotoSourceException { RetryAfter: { } minimum } && minimum > delay) delay = minimum;
        // A CDN can also respond with 429, without an API Retry-After value.
        if (error is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } && delay < TimeSpan.FromHours(1))
            delay = TimeSpan.FromHours(1);
        return delay;
    }
}
