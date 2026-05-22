using Framework.Contracts;

namespace Framework.API.Clients;

public sealed class BookingsApiClient(
    APIClient apiClient,
    Serilog.ILogger logger,
    EndpointData.BookingEndpointData endpoints)
    : BaseApiClient(apiClient, logger)
{
    private readonly EndpointData.BookingEndpointData _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints), "Booking endpoint data cannot be null.");

    public Task<ApiCallResult> ListBookingsAsync(
        int page,
        int limit,
        string? status,
        int? eventId,
        CancellationToken cancellationToken = default)
    {
        if (page < 0)
        {
            throw new ArgumentException("Page number cannot be negative.", nameof(page));
        }

        if (limit <= 0)
        {
            throw new ArgumentException("Limit must be greater than zero.", nameof(limit));
        }

        if (eventId.HasValue && eventId.Value <= 0)
        {
            throw new ArgumentException("Event ID must be greater than zero.", nameof(eventId));
        }

        if (string.IsNullOrWhiteSpace(_endpoints.List))
        {
            throw new InvalidOperationException("List bookings endpoint is not configured.");
        }

        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["limit"] = limit.ToString(),
            ["status"] = status,
            ["eventId"] = eventId?.ToString()
        };

        return SendAsync(HttpMethod.Get, _endpoints.List, queryParams: query, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> CreateBookingAsync(object payload, CancellationToken cancellationToken = default)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload), "Booking payload cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(_endpoints.Create))
        {
            throw new InvalidOperationException("Create booking endpoint is not configured.");
        }

        return SendAsync(HttpMethod.Post, _endpoints.Create, payload, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> GetBookingByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Booking ID must be greater than zero.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(_endpoints.GetById))
        {
            throw new InvalidOperationException("Get booking by ID endpoint is not configured.");
        }

        var endpoint = _endpoints.GetById.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == _endpoints.GetById)
        {
            throw new InvalidOperationException($"Failed to construct endpoint for getting booking with ID {id}. Endpoint template may not contain '{{id}}' placeholder.");
        }

        return SendAsync(HttpMethod.Get, endpoint, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> GetBookingByReferenceAsync(string referenceCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(referenceCode))
        {
            throw new ArgumentException("Booking reference code cannot be null or empty.", nameof(referenceCode));
        }

        if (string.IsNullOrWhiteSpace(_endpoints.GetByReference))
        {
            throw new InvalidOperationException("Get booking by reference endpoint is not configured.");
        }

        var endpoint = _endpoints.GetByReference.Replace("{ref}", referenceCode, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == _endpoints.GetByReference)
        {
            throw new InvalidOperationException($"Failed to construct endpoint for getting booking with reference '{referenceCode}'. Endpoint template may not contain '{{ref}}' placeholder.");
        }

        return SendAsync(HttpMethod.Get, endpoint, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> CancelBookingAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Booking ID must be greater than zero.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(_endpoints.CancelById))
        {
            throw new InvalidOperationException("Cancel booking endpoint is not configured.");
        }

        var endpoint = _endpoints.CancelById.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == _endpoints.CancelById)
        {
            throw new InvalidOperationException($"Failed to construct endpoint for cancelling booking with ID {id}. Endpoint template may not contain '{{id}}' placeholder.");
        }

        return SendAsync(HttpMethod.Delete, endpoint, requiresAuth: true, cancellationToken: cancellationToken);
    }
}
