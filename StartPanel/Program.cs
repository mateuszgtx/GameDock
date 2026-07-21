using System.Threading.RateLimiting;
using WolfControl.Options;
using WolfControl.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MachineOptions>(
    builder.Configuration.GetSection(MachineOptions.SectionName));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("power", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddHttpClient("GameDockAgent");
builder.Services.AddSingleton<WakeOnLanService>();
builder.Services.AddSingleton<MachineControlService>();
builder.Services.AddSingleton<MachineMetricsService>();

var app = builder.Build();

app.UseExceptionHandler("/api/error");
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/api/machine/status", async (
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    MachineStatus status = await machine.GetStatusAsync(cancellationToken);
    return Results.Ok(status);
});

app.MapGet("/api/machine/metrics", async (
    MachineMetricsService metrics,
    CancellationToken cancellationToken) =>
{
    MachineMetricsStatus status = await metrics.GetAsync(cancellationToken);
    return Results.Ok(status);
});

app.MapPost("/api/machine/wake", async (
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    MachineActionResult result = await machine.WakeAsync(cancellationToken);
    return Results.Accepted(value: result);
})
.RequireRateLimiting("power");

app.MapPost("/api/machine/restart", async (
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    MachineActionResult result = await machine.RestartAsync(cancellationToken);
    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
})
.RequireRateLimiting("power");

app.MapPost("/api/machine/shutdown", async (
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    MachineActionResult result = await machine.ShutdownAsync(cancellationToken);
    return Results.Ok(result);
})
.RequireRateLimiting("power");

app.MapPost("/api/machine/systems/{systemId}/boot", async (
    string systemId,
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    MachineActionResult result = await machine.BootSystemAsync(
        systemId,
        cancellationToken);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
})
.RequireRateLimiting("power");

app.MapMethods(
    "/api/error",
    new[] { "GET", "POST" },
    () => Results.Problem(
        title: "Błąd serwera",
        detail: "Nie udało się wykonać operacji. Sprawdź log aplikacji i konfigurację SSH.",
        statusCode: StatusCodes.Status500InternalServerError));

app.MapFallbackToFile("index.html");

app.Run();
