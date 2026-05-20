using UITests;
using Framework.API;
using Framework.API.Clients;
using Framework.Core.Configuration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HybridTests;

public abstract class HybridBaseTest : BaseTest
{
    protected override string ExecutionTestType => "Hybrid";

    protected APIClient HybridApiClient = null!;
    protected AuthClient HybridAuthClient = null!;
    protected EventsApiClient HybridEventsApiClient = null!;
    protected string HybridLoginEndpoint = string.Empty;
    protected string HybridMeEndpoint = string.Empty;

    [SetUp]
    public void SetUpHybridContext()
    {
        var apiBaseUrl = ConfigManager.GetString("Api:BaseUrl");
        HybridApiClient = new APIClient(new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        });
        HybridApiClient.ShowBearerToken = ConfigManager.GetBool("Api:ShowBearerToken");

        var rawLoginData = LoadTestData<JObject>("loginData.json")!;
        var tokenJsonPath = rawLoginData["apiAuth"]!["assertions"]!["tokenJsonPath"]!.Value<string>()!;
        HybridLoginEndpoint = rawLoginData["apiAuth"]!["endpoints"]!["login"]!.Value<string>()!;
        HybridMeEndpoint = rawLoginData["apiAuth"]!["endpoints"]!["me"]!.Value<string>()!;

        HybridAuthClient = new AuthClient(HybridApiClient, Logger, HybridLoginEndpoint, tokenJsonPath);
    }

    protected void InitializeEventApiClientFromTestData()
    {
        var eventData = LoadTestData<JObject>("eventData.json")!;
        var eventEndpoints = new Framework.Contracts.EndpointData.EventEndpointData
        {
            List = eventData["endpoints"]!["list"]!.Value<string>()!,
            Create = eventData["endpoints"]!["create"]!.Value<string>()!,
            GetById = eventData["endpoints"]!["getById"]!.Value<string>()!,
            UpdateById = eventData["endpoints"]!["updateById"]!.Value<string>()!,
            DeleteById = eventData["endpoints"]!["deleteById"]!.Value<string>()!
        };

        HybridEventsApiClient = new EventsApiClient(HybridApiClient, Logger, eventEndpoints);
    }

    [TearDown]
    public void TearDownHybridContext()
    {
        HybridApiClient?.Dispose();
    }
}
