namespace Framework.Contracts;

/// <summary>
/// Central contract for API endpoint paths loaded from configuration/test data,
/// so API clients can consume strongly typed endpoint groups (Auth, Events, Bookings)
/// instead of hardcoded route strings.
/// </summary>
public sealed class EndpointData
{
    public AuthEndpointData Auth { get; set; } = new();
    public EventEndpointData Events { get; set; } = new();
    public BookingEndpointData Bookings { get; set; } = new();

    public sealed class AuthEndpointData
    {
        public string Login { get; set; } = string.Empty;
        public string Me { get; set; } = string.Empty;
    }

    public sealed class EventEndpointData
    {
        public string List { get; set; } = string.Empty;
        public string Create { get; set; } = string.Empty;
        public string GetById { get; set; } = string.Empty;
        public string UpdateById { get; set; } = string.Empty;
        public string DeleteById { get; set; } = string.Empty;
    }

    public sealed class BookingEndpointData
    {
        public string List { get; set; } = string.Empty;
        public string Create { get; set; } = string.Empty;
        public string GetById { get; set; } = string.Empty;
        public string GetByReference { get; set; } = string.Empty;
        public string CancelById { get; set; } = string.Empty;
    }
}
