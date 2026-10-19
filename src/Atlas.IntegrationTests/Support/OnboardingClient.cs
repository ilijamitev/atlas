using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Onboarding.Applications;

namespace Atlas.IntegrationTests.Support;

/// <summary>The mobile app's side of the Onboarding API.</summary>
internal sealed class OnboardingClient(HttpClient http)
{
    public HttpClient Http => http;

    public Task<HttpResponseMessage> CreateAsync(string market, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/markets/{market}/applications") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return http.SendAsync(request);
    }

    public async Task<ApplicationCreatedResponse> CreateOkAsync(string market, object body, string? idempotencyKey = null)
    {
        var response = await CreateAsync(market, body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ApplicationCreatedResponse>();
    }

    public Task<HttpResponseMessage> GetAsync(string market, Guid id, string? token) =>
        http.SendAsync(Request(HttpMethod.Get, $"/v1/markets/{market}/applications/{id}", token));

    public async Task<ApplicationDetailsResponse> GetOkAsync(string market, Guid id, string token)
    {
        var response = await GetAsync(market, id, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<ApplicationDetailsResponse>();
    }

    public Task<HttpResponseMessage> PatchAsync(string market, Guid id, string token, object body)
    {
        var request = Request(HttpMethod.Patch, $"/v1/markets/{market}/applications/{id}", token);
        request.Content = JsonContent.Create(body);
        return http.SendAsync(request);
    }

    public Task<HttpResponseMessage> SubmitAsync(string market, Guid id, string token) =>
        http.SendAsync(Request(HttpMethod.Post, $"/v1/markets/{market}/applications/{id}/submit", token));

    public Task<HttpResponseMessage> EraseAsync(string market, Guid id, string token) =>
        http.SendAsync(Request(HttpMethod.Post, $"/v1/markets/{market}/applications/{id}/erasure", token));

    /// <summary>Polls GET until the status arrives, as the app would after a 202.</summary>
    public async Task<ApplicationDetailsResponse> WaitForStatusAsync(string market, Guid id, string token, string status, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var current = await GetOkAsync(market, id, token);
            if (current.Status == status || DateTime.UtcNow > deadline)
            {
                Assert.Equal(status, current.Status);
                return current;
            }

            await Task.Delay(100);
        }
    }

    public static HttpRequestMessage Request(HttpMethod method, string path, string? applicationToken = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (applicationToken is not null)
        {
            request.Headers.Add("X-Application-Token", applicationToken);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }
}

internal static class HttpResponseExtensions
{
    // The helpers read the buffered body as a string, so a test can read the same response more than once.
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(await BodyAsync(response), JsonSerializerOptions.Web)!;

    /// <summary>The stable <c>code</c> of a problem+json response.</summary>
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonSerializer.Deserialize<JsonElement>(await BodyAsync(response)).GetProperty("code").GetString();
    }

    public static async Task<List<string>> FieldCodesAsync(this HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await BodyAsync(response)).GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString()!)
            .ToList();

    public static async Task<string> BodyAsync(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return await response.Content.ReadAsStringAsync();
    }
}
