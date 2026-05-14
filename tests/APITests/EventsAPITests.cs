using Framework.API;
using Framework.Reports;

namespace APITests;

[Parallelizable(ParallelScope.Self)]
[TestRole("user")]
public class EventsAPITests : APITestBase
{
    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    public async Task ListEvents_ShouldSupportPaginationAndFilters()
    {
        EnsurePositiveAuthContextOrInconclusive("Events");

        var response = await EventsApi.ListEventsAsync(
            ApiData.Queries.Events.Page,
            ApiData.Queries.Events.Limit,
            ApiData.Queries.Events.Category,
            ApiData.Queries.Events.City,
            ApiData.Queries.Events.Search);

        APIClient.ValidateStatusCode(response.StatusCode, 200);

        // Validate pagination field exists using Response Validation Framework
        ResponseValidator
            .FromContent(response.ResponseBody)
            .ValidateFieldExists(ApiData.Assertions.Events.PaginationField);
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    public async Task CreateUpdateDeleteEvent()
    {
        EnsurePositiveAuthContextOrInconclusive("Events");

        int createdEventId = 0;

        try
        {
            var createPayload = BuildPayload(ApiData.Events.CreatePayload);
            var createResponse = await EventsApi.CreateEventAsync(createPayload);
            APIClient.ValidateStatusCode(createResponse.StatusCode, 201);

            // Validate event ID exists and extract it using Response Validation Framework
            ResponseValidator
                .FromContent(createResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Events.CreatedEventIdJsonPath);

            // Extract ID for further operations
            createdEventId = ExtractRequiredInt(
                createResponse.ResponseBody,
                ApiData.Assertions.Events.CreatedEventIdJsonPath,
                "Created event id was not found in response.");
            Assert.That(createdEventId, Is.GreaterThan(0));

            var getResponse = await EventsApi.GetEventByIdAsync(createdEventId);
            APIClient.ValidateStatusCode(getResponse.StatusCode, 200);

            var updatePayload = BuildPayload(ApiData.Events.UpdatePayload);
            var updateResponse = await EventsApi.UpdateEventAsync(createdEventId, updatePayload);
            APIClient.ValidateStatusCode(updateResponse.StatusCode, 200);
        }
        finally
        {
            if (createdEventId > 0)
            {
                try
                {
                    var deleteResponse = await EventsApi.DeleteEventAsync(createdEventId);
                    // Accept 200, 204 (deleted) or 404 (already deleted)
                    Assert.That((int)deleteResponse.StatusCode, Is.AnyOf(200, 204, 404));
                }
                catch (Exception ex)
                {
                    Logger.Warning("[CLEANUP] Failed to delete event {EventId}. Error: {Error}", createdEventId, ex.Message);
                }
            }
        }
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    public async Task CreateEvent_WithInvalidPayload_ShouldReturnValidationError()
    {
        EnsurePositiveAuthContextOrInconclusive("Events");

        var invalidPayload = BuildPayload(ApiData.Events.InvalidCreatePayload);

        var response = await EventsApi.CreateEventAsync(invalidPayload);
        APIClient.ValidateStatusCode(response.StatusCode, 400);

        // Validate error response using Response Validation Framework
        ResponseValidator
            .FromContent(response.ResponseBody)
            .ValidateFieldExists(ApiData.Assertions.Events.ValidationErrorField);
    }
}
