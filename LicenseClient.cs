using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AIFlashcardMaker;

/// <summary>
/// Phase 7 (Checkpoint D): HTTP client for the OrbitLab license backend.
///
/// PRIVACY BY CONSTRUCTION: the only data this client ever sends is account and
/// license metadata — name/email/password (register/login), institution/role/
/// Telegram username (optional profile), activation code (redeem), a salted
/// device hash, and the app version. There is deliberately no method that could
/// transmit research projects, CSV/dataset content, participant rows, computed
/// results, reports, or exports — and none may ever be added here.
///
/// Every call is fail-safe: network errors become an <see cref="ApiError"/> with
/// code "network"; nothing here throws into the UI.
/// </summary>
public sealed record ApiError(string Code, string Message);

public sealed class ApiResult<T> where T : class
{
    public T? Data { get; init; }
    public ApiError? Error { get; init; }
    public bool Ok => Error is null && Data is not null;
    public static ApiResult<T> Success(T data) => new() { Data = data };
    public static ApiResult<T> Fail(string code, string message) => new() { Error = new ApiError(code, message) };
}

public sealed class SubscriptionDto
{
    public int Id { get; set; }
    public string? PlanCode { get; set; }
    public string? PlanName { get; set; }
    public string? Status { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public int GraceDays { get; set; }
    public JsonElement Entitlements { get; set; }
}

public sealed class RegisterResponse
{
    public bool Ok { get; set; }
    public int UserId { get; set; }
    public bool EmailVerified { get; set; }
    public string? Message { get; set; }
    public SubscriptionDto? Subscription { get; set; }
}

public sealed class LoginUserDto
{
    public int Id { get; set; }
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public bool EmailVerified { get; set; }
}

public sealed class LoginResponse
{
    public bool Ok { get; set; }
    public string? Token { get; set; }
    public string? TokenType { get; set; }
    public LoginUserDto? User { get; set; }
    public SubscriptionDto? Subscription { get; set; }
}

public sealed class ActivateDeviceResponse
{
    public bool Ok { get; set; }
    public int DeviceActivationId { get; set; }
    public string? Message { get; set; }
}

public sealed class HeartbeatResponse
{
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public bool Revoked { get; set; }
    public bool Expired { get; set; }
    public bool DeviceLinked { get; set; }
    public int GraceDays { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public JsonElement Entitlements { get; set; }
    public DateTime ServerTimeUtc { get; set; }
}

public static class LicenseApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class ErrorBody { public string? Error { get; set; } public string? Message { get; set; } }

    public static Task<ApiResult<RegisterResponse>> RegisterAsync(
        string fullName, string email, string password, string institution, string roleTitle,
        string telegramUsername, bool telegramChannelAcknowledged, string activationCode) =>
        PostAsync<RegisterResponse>("/api/register", new
        {
            fullName,
            email,
            password,
            institution,
            roleTitle,
            telegramUsername,
            telegramChannelAcknowledged,
            activationCode,
        });

    public static Task<ApiResult<LoginResponse>> LoginAsync(string email, string password) =>
        PostAsync<LoginResponse>("/api/login", new { email, password });

    public static Task<ApiResult<ActivateDeviceResponse>> ActivateDeviceAsync(
        string token, string deviceHash, string deviceName, string osInfo) =>
        PostAsync<ActivateDeviceResponse>("/api/license/activate-device",
            new { deviceHash, deviceName, osInfo }, token);

    public static Task<ApiResult<HeartbeatResponse>> HeartbeatAsync(
        string token, int? subscriptionId, string deviceHash) =>
        PostAsync<HeartbeatResponse>("/api/license/heartbeat",
            new { subscriptionId, deviceHash, appVersion = AppConfig.CurrentVersionLabel }, token);

    private static async Task<ApiResult<T>> PostAsync<T>(string path, object body, string? bearer = null)
        where T : class
    {
        if (!AppConfig.IsBackendConfigured)
            return ApiResult<T>.Fail("not_configured", "No license server is configured in this build.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.BackendBaseUrl}{path}")
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            if (bearer is not null)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

            using var resp = await Http.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<T>(json, JsonOpts);
                return data is null
                    ? ApiResult<T>.Fail("bad_response", "The server returned an unexpected response.")
                    : ApiResult<T>.Success(data);
            }

            // Non-2xx: the backend returns { error, message } — surface it verbatim.
            try
            {
                var err = JsonSerializer.Deserialize<ErrorBody>(json, JsonOpts);
                if (!string.IsNullOrWhiteSpace(err?.Message))
                    return ApiResult<T>.Fail(err!.Error ?? "server_error", err.Message!);
            }
            catch { /* fall through to generic */ }
            return ApiResult<T>.Fail("server_error", $"The server rejected the request (HTTP {(int)resp.StatusCode}).");
        }
        catch
        {
            return ApiResult<T>.Fail("network", "Could not reach the OrbitLab server. Check your internet connection and try again.");
        }
    }
}
