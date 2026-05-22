using Framework.Contracts;

namespace Framework.API.Clients;

public sealed class EventsApiClient(
    APIClient apiClient,
    Serilog.ILogger logger,
    EndpointData.EventEndpointData endpoints)
    : BaseApiClient(apiClient, logger)
{
    private readonly EndpointData.EventEndpointData _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints), "Event endpoint data cannot be null.");

    public Task<ApiCallResult> ListEventsAsync(
        int page,
        int limit,
        string? category,
        string? city,
        string? search,
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

        if (string.IsNullOrWhiteSpace(_endpoints.List))
        {
            throw new InvalidOperationException("List events endpoint is not configured.");
        }

        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["limit"] = limit.ToString(),
            ["category"] = category,
            ["city"] = city,
            ["search"] = search
        };

        return SendAsync(HttpMethod.Get, _endpoints.List, queryParams: query, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> GetEventByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Event ID must be greater than zero.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(_endpoints.GetById))
        {
            throw new InvalidOperationException("Get event by ID endpoint is not configured.");
        }

        var endpoint = _endpoints.GetById.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == _endpoints.GetById)
        {
            throw new InvalidOperationException($"Failed to construct endpoint for getting event with ID {id}. Endpoint template may not contain '{{id}}' placeholder.");
        }

        return SendAsync(HttpMethod.Get, endpoint, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> CreateEventAsync(object payload, CancellationToken cancellationToken = default)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload), "Event payload cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(_endpoints.Create))
        {
            throw new InvalidOperationException("Create event endpoint is not configured.");
        }

        return SendAsync(HttpMethod.Post, _endpoints.Create, payload, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> UpdateEventAsync(int id, object payload, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Event ID must be greater than zero.", nameof(id));
        }

        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload), "Event payload cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(_endpoints.UpdateById))
        {
            throw new InvalidOperationException("Update event endpoint is not configured.");
        }

        var endpoint = _endpoints.UpdateById.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == _endpoints.UpdateById)
        {
            throw new InvalidOperationException($"Failed to construct endpoint for updating event with ID {id}. Endpoint template may not contain '{{id}}' placeholder.");
        }

        return SendAsync(HttpMethod.Put, endpoint, payload, requiresAuth: true, cancellationToken: cancellationToken);
    }

    public Task<ApiCallResult> DeleteEventAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Event ID must be greater than zero.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(_endpoints.DeleteById))
        {
            throw new InvalidOperationException("Delete event endpoint is not configured.");
        }

        var endpoint = _endpoints.DeleteById.Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == _endpoints.DeleteById)
        {
            throw new InvalidOperationException($"Failed to construct endpoint for deleting event with ID {id}. Endpoint template may not contain '{{id}}' placeholder.");
        }

        return SendAsync(HttpMethod.Delete, endpoint, requiresAuth: true, cancellationToken: cancellationToken);
    }
}
