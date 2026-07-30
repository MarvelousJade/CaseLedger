using System.Globalization;

namespace CaseLedger.Api.Domain;

internal static class UtcTimestamp
{
    public static DateTime Now(TimeProvider timeProvider) =>
        Normalize(timeProvider.GetUtcNow().UtcDateTime);

    public static DateTime Normalize(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            DateTimeKind.Utc);
    }

    public static string Format(DateTime value) =>
        Normalize(value).ToString("O", CultureInfo.InvariantCulture);
}
