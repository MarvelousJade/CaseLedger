namespace CaseLedger.Api.Domain;

internal static class DeliveryErrorCode
{
    public static bool IsSafe(string value) =>
        value.Length is > 0 and <= 80 &&
        value.All(character =>
            character is >= 'A' and <= 'Z' ||
            character is >= '0' and <= '9' ||
            character == '_');
}
