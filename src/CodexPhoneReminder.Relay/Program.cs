using CodexPhoneReminder.Relay;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var relayOptions = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
relayOptions.Validate();
builder.WebHost.ConfigureKestrel(options =>
{
    // JSON metadata adds a small overhead around the opaque encrypted envelope.
    options.Limits.MaxRequestBodySize = relayOptions.MaxEnvelopeBytes + 8_192;
});
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddSingleton<RelayStore>();
builder.Services.AddSingleton<RelayAuthorization>();

var app = builder.Build();

// This process is intended to listen only on a loopback/container port behind Caddy, nginx, or
// another TLS terminator. Do not expose the internal HTTP port directly to the Internet.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    protocol = "codex-phone-relay/v1",
    serverTime = DateTimeOffset.UtcNow
}));

app.MapPost("/api/v1/agents/register", (RegisterAgentRequest body, HttpRequest request, RelayAuthorization authorization, RelayStore store) =>
{
    if (!authorization.HasBootstrap(request)) return Unauthorized();
    try
    {
        return Results.Ok(store.RegisterAgent(body.AgentId));
    }
    catch (Exception ex) when (ex is RelayValidationException)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
});

app.MapPost("/api/v1/agents/{agentId}/devices/enroll", (string agentId, EnrollDeviceRequest body, HttpRequest request, RelayAuthorization authorization, RelayStore store) =>
{
    if (!authorization.TryGetAgent(request, agentId, out var agent) || agent is null) return Unauthorized();
    try
    {
        return Results.Ok(store.EnrollDevice(agent.AgentId, body.DeviceId));
    }
    catch (RelayNotFoundException ex)
    {
        return Error(StatusCodes.Status404NotFound, ex.Message);
    }
    catch (RelayValidationException ex)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
});

app.MapPost("/api/v1/agents/{agentId}/requests", (string agentId, SubmitRelayRequest body, HttpRequest request, RelayAuthorization authorization, RelayStore store) =>
{
    if (!authorization.TryGetDevice(request, agentId, out var device) || device is null) return Unauthorized();
    try
    {
        var submitted = store.SubmitRequest(device, body);
        return Results.Accepted($"/api/v1/agents/{agentId}/requests/{submitted.RequestId}/response", submitted);
    }
    catch (RelayConflictException ex)
    {
        return Error(StatusCodes.Status409Conflict, ex.Message);
    }
    catch (RelayQueueFullException ex)
    {
        return Error(StatusCodes.Status429TooManyRequests, ex.Message);
    }
    catch (RelayValidationException ex)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
});

app.MapGet("/api/v1/agents/{agentId}/requests", async (string agentId, int? waitSeconds, HttpRequest request, RelayAuthorization authorization, RelayStore store, IOptions<RelayOptions> options, CancellationToken cancellationToken) =>
{
    if (!authorization.TryGetAgent(request, agentId, out var agent) || agent is null) return Unauthorized();
    if (!TryGetWait(waitSeconds, options.Value, out var wait, out var waitError)) return Error(StatusCodes.Status400BadRequest, waitError!);
    var pending = await store.PollAgentAsync(agent.AgentId, wait, cancellationToken);
    return pending is null ? Results.NoContent() : Results.Ok(pending);
});

app.MapPost("/api/v1/agents/{agentId}/requests/{requestId}/response", (string agentId, string requestId, SubmitRelayResult body, HttpRequest request, RelayAuthorization authorization, RelayStore store) =>
{
    if (!authorization.TryGetAgent(request, agentId, out var agent) || agent is null) return Unauthorized();
    try
    {
        store.SubmitResponse(agent, requestId, body);
        return Results.NoContent();
    }
    catch (RelayNotFoundException ex)
    {
        return Error(StatusCodes.Status404NotFound, ex.Message);
    }
    catch (RelayConflictException ex)
    {
        return Error(StatusCodes.Status409Conflict, ex.Message);
    }
    catch (RelayValidationException ex)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
});

app.MapGet("/api/v1/agents/{agentId}/requests/{requestId}/response", async (string agentId, string requestId, int? waitSeconds, HttpRequest request, RelayAuthorization authorization, RelayStore store, IOptions<RelayOptions> options, CancellationToken cancellationToken) =>
{
    if (!authorization.TryGetDevice(request, agentId, out var device) || device is null) return Unauthorized();
    if (!TryGetWait(waitSeconds, options.Value, out var wait, out var waitError)) return Error(StatusCodes.Status400BadRequest, waitError!);
    try
    {
        var result = await store.PollResultAsync(device, requestId, wait, cancellationToken);
        return result is null
            ? Results.Accepted(value: new { status = "pending" })
            : Results.Ok(result);
    }
    catch (RelayNotFoundException ex)
    {
        return Error(StatusCodes.Status404NotFound, ex.Message);
    }
    catch (RelayValidationException ex)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
});

app.Run();

static bool TryGetWait(int? requestedSeconds, RelayOptions options, out TimeSpan wait, out string? error)
{
    var seconds = requestedSeconds ?? options.MaxLongPollSeconds;
    if (seconds is < 0 or > 25 || seconds > options.MaxLongPollSeconds)
    {
        wait = default;
        error = $"waitSeconds must be between 0 and {options.MaxLongPollSeconds}.";
        return false;
    }
    wait = TimeSpan.FromSeconds(seconds);
    error = null;
    return true;
}

static IResult Unauthorized() => Results.Json(new { error = "Unauthorized." }, statusCode: StatusCodes.Status401Unauthorized);
static IResult Error(int statusCode, string message) => Results.Json(new { error = message }, statusCode: statusCode);

public partial class Program { }
