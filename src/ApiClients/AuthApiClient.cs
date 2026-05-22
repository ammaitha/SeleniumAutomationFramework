using Framework.Contracts;

namespace Framework.API.Clients;

public sealed class AuthApiClient(
    APIClient apiClient,
    Serilog.ILogger logger,
    EndpointData.AuthEndpointData endpoints)
    : BaseApiClient(apiClient, logger)
{
    private readonly EndpointData.AuthEndpointData _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints), "Auth endpoint data cannot be null.");

    public Task<ApiCallResult> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoints.Me))
        {
            throw new InvalidOperationException("Get current user endpoint is not configured.");
        }

        return SendAsync(HttpMethod.Get, _endpoints.Me, requiresAuth: true, cancellationToken: cancellationToken);
    }
}
