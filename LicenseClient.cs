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

// Phase 9 DTOs — project-usage accounting + Telegram-prompt state. Metadata only.
public sealed class ProjectUsageDto
{
    public string? CycleId { get; set; }
    public string? PlanName { get; set; }
    public string? Status { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
    public bool Active { get; set; }
    // Phase 10: draft registrations + distinct occupied positions in the cycle.
    public int Drafts { get; set; }
    public int PositionsUsed { get; set; }
}

// Phase 10: result of registering a project draft (occupies one plan position).
public sealed class RegisterResultDto
{
    public bool Ok { get; set; }
    public bool Registered { get; set; }
    public bool AlreadyCounted { get; set; }
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
}

public sealed class DraftIdsDto
{
    public bool Ok { get; set; }
    public List<string> ProjectIds { get; set; } = new();
}

public sealed class AccountOverviewDto
{
    public bool Ok { get; set; }
    public bool TelegramPromptAcknowledged { get; set; }
    public ProjectUsageDto? Usage { get; set; }
}

public sealed class ProjectStatusDto
{
    public bool Ok { get; set; }
    public string? State { get; set; }   // never_counted | counted_in_current_cycle | counted_in_previous_cycle
    public bool CanRunFirstAnalysis { get; set; }
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
}

public sealed class ReserveResultDto
{
    public bool Ok { get; set; }
    public bool AlreadyCounted { get; set; }
    public string? ReservationId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
}

public sealed class FinalizeResultDto
{
    public bool Ok { get; set; }
    public bool Finalized { get; set; }
    public bool AlreadyCounted { get; set; }
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
}

public sealed class SimpleOkDto { public bool Ok { get; set; } }

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

    // ---- Phase 9: project-usage accounting + Telegram-prompt state ----

    public static Task<ApiResult<AccountOverviewDto>> GetAccountOverviewAsync(string token) =>
        GetAsync<AccountOverviewDto>("/api/account/overview", token);

    public static Task<ApiResult<ProjectStatusDto>> GetProjectStatusAsync(string token, string projectPublicId) =>
        GetAsync<ProjectStatusDto>($"/api/projects/{Uri.EscapeDataString(projectPublicId)}/status", token);

    // Phase 10 draft-registration lifecycle: a project draft occupies one plan
    // position from creation until it is deleted (released) or counted (converted).
    public static Task<ApiResult<RegisterResultDto>> RegisterProjectAsync(string token, string projectPublicId) =>
        PostAsync<RegisterResultDto>($"/api/projects/{Uri.EscapeDataString(projectPublicId)}/register", new { }, token);

    public static Task<ApiResult<SimpleOkDto>> ReleaseDraftAsync(string token, string projectPublicId) =>
        SendAsync<SimpleOkDto>(HttpMethod.Delete, $"/api/projects/{Uri.EscapeDataString(projectPublicId)}/draft", token);

    public static Task<ApiResult<DraftIdsDto>> GetDraftIdsAsync(string token) =>
        GetAsync<DraftIdsDto>("/api/projects/drafts", token);

    // Phase 9 reservation lifecycle.
    public static Task<ApiResult<ReserveResultDto>> ReserveProjectAsync(string token, string projectPublicId) =>
        PostAsync<ReserveResultDto>($"/api/projects/{Uri.EscapeDataString(projectPublicId)}/reserve", new { }, token);

    public static Task<ApiResult<FinalizeResultDto>> FinalizeProjectAsync(string token, string projectPublicId, string reservationId) =>
        PostAsync<FinalizeResultDto>($"/api/projects/{Uri.EscapeDataString(projectPublicId)}/reservations/{Uri.EscapeDataString(reservationId)}/finalize", new { }, token);

    public static Task<ApiResult<SimpleOkDto>> ReleaseProjectAsync(string token, string projectPublicId, string reservationId) =>
        SendAsync<SimpleOkDto>(HttpMethod.Delete, $"/api/projects/{Uri.EscapeDataString(projectPublicId)}/reservations/{Uri.EscapeDataString(reservationId)}", token);

    public static Task<ApiResult<SimpleOkDto>> MarkProjectDeletedAsync(string token, string projectPublicId) =>
        PostAsync<SimpleOkDto>($"/api/projects/{Uri.EscapeDataString(projectPublicId)}/deleted", new { }, token);

    public static Task<ApiResult<SimpleOkDto>> AckTelegramPromptAsync(string token) =>
        PostAsync<SimpleOkDto>("/api/account/telegram-prompt/ack", new { }, token);

    private static Task<ApiResult<T>> GetAsync<T>(string path, string bearer) where T : class =>
        SendAsync<T>(HttpMethod.Get, path, bearer);

    private static async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, string bearer) where T : class
    {
        if (!AppConfig.IsBackendConfigured)
            return ApiResult<T>.Fail("not_configured", "No license server is configured in this build.");
        try
        {
            using var req = new HttpRequestMessage(method, $"{AppConfig.BackendBaseUrl}{path}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await Http.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<T>(json, JsonOpts);
                return data is null ? ApiResult<T>.Fail("bad_response", "Unexpected server response.") : ApiResult<T>.Success(data);
            }
            try
            {
                var err = JsonSerializer.Deserialize<ErrorBody>(json, JsonOpts);
                if (!string.IsNullOrWhiteSpace(err?.Message)) return ApiResult<T>.Fail(err!.Error ?? "server_error", err.Message!);
            }
            catch { }
            return ApiResult<T>.Fail("server_error", $"The server rejected the request (HTTP {(int)resp.StatusCode}).");
        }
        catch { return ApiResult<T>.Fail("network", "Could not reach the OrbitLab server."); }
    }

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
