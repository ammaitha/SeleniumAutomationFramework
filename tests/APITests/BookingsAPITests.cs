using Framework.API;
using Framework.Reports;
using Newtonsoft.Json.Linq;

namespace APITests;

[Parallelizable(ParallelScope.Self)]
[TestRole("user")]
public class BookingsAPITests : APITestBase
{
    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    public async Task CreateLookupAndCancelBooking()
    {
        EnsurePositiveAuthContextOrInconclusive("Bookings");

        int eventId = 0;
        int bookingId = 0;

        try
        {
            var eventResponse = await EventsApi.CreateEventAsync(BuildPayload(ApiData.Bookings.SupportingEventPayload));
            APIClient.ValidateStatusCode(eventResponse.StatusCode, 201);
            
            // Validate event ID exists using Response Validation Framework
            ResponseValidator
                .FromContent(eventResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.SupportingEventIdJsonPath)
                .ValidateType(ApiData.Assertions.Bookings.SupportingEventIdJsonPath, typeof(int));
            
            eventId = ExtractRequiredInt(
                eventResponse.ResponseBody,
                ApiData.Assertions.Bookings.SupportingEventIdJsonPath,
                "Supporting event id was not found in response.");

            var bookingResponse = await BookingsApi.CreateBookingAsync(BuildPayload(
                ApiData.Bookings.CreatePayload,
                new Dictionary<string, JToken>
                {
                    ["eventId"] = JToken.FromObject(eventId)
                }));
            APIClient.ValidateStatusCode(bookingResponse.StatusCode, 201);

            // Validate booking ID and reference using Response Validation Framework
            ResponseValidator
                .FromContent(bookingResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.CreatedBookingIdJsonPath)
                .ValidateFieldExists(ApiData.Assertions.Bookings.BookingReferenceJsonPath)
                .ValidateType(ApiData.Assertions.Bookings.BookingReferenceJsonPath, typeof(string));

            bookingId = ExtractRequiredInt(
                bookingResponse.ResponseBody,
                ApiData.Assertions.Bookings.CreatedBookingIdJsonPath,
                "Created booking id was not found in response.");
            var bookingRef = ExtractRequiredString(
                bookingResponse.ResponseBody,
                ApiData.Assertions.Bookings.BookingReferenceJsonPath,
                "Booking reference was not found in response.");

            var byIdResponse = await BookingsApi.GetBookingByIdAsync(bookingId);
            APIClient.ValidateStatusCode(byIdResponse.StatusCode, 200);

            var byRefResponse = await BookingsApi.GetBookingByReferenceAsync(bookingRef);
            APIClient.ValidateStatusCode(byRefResponse.StatusCode, 200);
        }
        finally
        {
            if (bookingId > 0)
            {
                try
                {
                    var cancelResponse = await BookingsApi.CancelBookingAsync(bookingId);
                    Assert.That((int)cancelResponse.StatusCode, Is.AnyOf(200, 204, 404));
                }
                catch (Exception ex)
                {
                    Logger.Warning("[CLEANUP] Failed to cancel booking {BookingId}. Error: {Error}", bookingId, ex.Message);
                }
            }

            if (eventId > 0)
            {
                try
                {
                    var deleteEventResponse = await EventsApi.DeleteEventAsync(eventId);
                    Assert.That((int)deleteEventResponse.StatusCode, Is.AnyOf(200, 204, 404));
                }
                catch (Exception ex)
                {
                    Logger.Warning("[CLEANUP] Failed to delete event {EventId}. Error: {Error}", eventId, ex.Message);
                }
            }
        }
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    public async Task ListBookings_ShouldSupportPaginationStatusAndEventFilter()
    {
        EnsurePositiveAuthContextOrInconclusive("Bookings");

        int eventId = 0;
        int bookingId = 0;

        try
        {
            var eventResponse = await EventsApi.CreateEventAsync(BuildPayload(ApiData.Bookings.SupportingEventPayload));
            APIClient.ValidateStatusCode(eventResponse.StatusCode, 201);
            
            // Validate event ID exists using Response Validation Framework
            ResponseValidator
                .FromContent(eventResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.SupportingEventIdJsonPath)
                .ValidateType(ApiData.Assertions.Bookings.SupportingEventIdJsonPath, typeof(int));
            
            eventId = ExtractRequiredInt(
                eventResponse.ResponseBody,
                ApiData.Assertions.Bookings.SupportingEventIdJsonPath,
                "Supporting event id was not found in response.");

            var bookingResponse = await BookingsApi.CreateBookingAsync(BuildPayload(
                ApiData.Bookings.CreatePayload,
                new Dictionary<string, JToken>
                {
                    ["eventId"] = JToken.FromObject(eventId)
                }));
            APIClient.ValidateStatusCode(bookingResponse.StatusCode, 201);
            
            // Validate booking ID exists using Response Validation Framework
            ResponseValidator
                .FromContent(bookingResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.CreatedBookingIdJsonPath);
            
            bookingId = ExtractRequiredInt(
                bookingResponse.ResponseBody,
                ApiData.Assertions.Bookings.CreatedBookingIdJsonPath,
                "Created booking id was not found in response.");

            var listResponse = await BookingsApi.ListBookingsAsync(
                ApiData.Queries.Bookings.Page,
                ApiData.Queries.Bookings.Limit,
                ApiData.Queries.Bookings.Status,
                eventId);

            APIClient.ValidateStatusCode(listResponse.StatusCode, 200);
            
            // Validate pagination field exists using Response Validation Framework
            ResponseValidator
                .FromContent(listResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.PaginationField);
        }
        finally
        {
            if (bookingId > 0)
            {
                try
                {
                    await BookingsApi.CancelBookingAsync(bookingId);
                }
                catch (Exception ex)
                {
                    Logger.Warning("[CLEANUP] Failed to cancel booking {BookingId}. Error: {Error}", bookingId, ex.Message);
                }
            }

            if (eventId > 0)
            {
                try
                {
                    await EventsApi.DeleteEventAsync(eventId);
                }
                catch (Exception ex)
                {
                    Logger.Warning("[CLEANUP] Failed to delete event {EventId}. Error: {Error}", eventId, ex.Message);
                }
            }
        }
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    public async Task CreateBooking_WithInvalidPayload_ShouldReturnValidationError()
    {
        EnsurePositiveAuthContextOrInconclusive("Bookings");

        int eventId = 0;

        try
        {
            var eventResponse = await EventsApi.CreateEventAsync(BuildPayload(ApiData.Bookings.SupportingEventPayload));
            APIClient.ValidateStatusCode(eventResponse.StatusCode, 201);
            
            // Validate event ID exists using Response Validation Framework
            ResponseValidator
                .FromContent(eventResponse.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.SupportingEventIdJsonPath)
                .ValidateType(ApiData.Assertions.Bookings.SupportingEventIdJsonPath, typeof(int));
            
            eventId = ExtractRequiredInt(
                eventResponse.ResponseBody,
                ApiData.Assertions.Bookings.SupportingEventIdJsonPath,
                "Supporting event id was not found in response.");

            var invalidPayload = BuildPayload(
                ApiData.Bookings.InvalidCreatePayload,
                new Dictionary<string, JToken>
                {
                    ["eventId"] = JToken.FromObject(eventId)
                });

            var response = await BookingsApi.CreateBookingAsync(invalidPayload);
            APIClient.ValidateStatusCode(response.StatusCode, 400);
            
            // Validate error response using Response Validation Framework
            ResponseValidator
                .FromContent(response.ResponseBody)
                .ValidateFieldExists(ApiData.Assertions.Bookings.ValidationErrorField);
        }
        finally
        {
            if (eventId > 0)
            {
                try
                {
                    var deleteResponse = await EventsApi.DeleteEventAsync(eventId);
                    Assert.That((int)deleteResponse.StatusCode, Is.AnyOf(200, 204, 404));
                }
                catch (Exception ex)
                {
                    Logger.Warning("[CLEANUP] Failed to delete event {EventId}. Error: {Error}", eventId, ex.Message);
                }
            }
        }
    }
}
