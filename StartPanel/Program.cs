using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using WolfControl.Options;
using WolfControl.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MachineOptions>(
    builder.Configuration.GetSection(MachineOptions.SectionName));

builder.Services.Configure<GpioButtonOptions>(
    builder.Configuration.GetSection(GpioButtonOptions.SectionName));

builder.Services
    .AddOptions<PowerControlOptions>()
    .Bind(builder.Configuration.GetSection(PowerControlOptions.SectionName))
    .Validate(
        options => Enum.IsDefined(options.StartupMethod),
        "PowerControl:StartupMethod musi mieć wartość WakeOnLan albo UsbHid.")
    .Validate(
        options => options.StartupMethod != PowerStartupMethod.UsbHid
            || !string.IsNullOrWhiteSpace(options.HidDevice),
        "PowerControl:HidDevice jest wymagane dla UsbHid.")
    .Validate(
        options => options.StartupMethod != PowerStartupMethod.UsbHid
            || options.HidKeyCode is >= 1 and <= 101,
        "PowerControl:HidKeyCode musi mieścić się w zakresie 1-101.")
    .Validate(
        options => options.StartupMethod != PowerStartupMethod.UsbHid
            || options.HidPressDurationMs is >= 0 and <= 5000,
        "PowerControl:HidPressDurationMs musi mieścić się w zakresie 0-5000 ms.")
    .Validate(
        options => options.StartupMethod != PowerStartupMethod.UsbHid
            || options.HidWriteTimeoutMs is >= 100 and <= 10000,
        "PowerControl:HidWriteTimeoutMs musi mieścić się w zakresie 100-10000 ms.")
    .ValidateOnStart();

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
builder.Services.AddSingleton<UsbHidPowerOnService>();
builder.Services.AddSingleton<IPowerOnService>(serviceProvider =>
{
    PowerControlOptions options = serviceProvider
        .GetRequiredService<IOptions<PowerControlOptions>>()
        .Value;

    return options.StartupMethod switch
    {
        PowerStartupMethod.WakeOnLan =>
            serviceProvider.GetRequiredService<WakeOnLanService>(),
        PowerStartupMethod.UsbHid =>
            serviceProvider.GetRequiredService<UsbHidPowerOnService>(),
        _ => throw new InvalidOperationException(
            $"Nieobsługiwana metoda uruchamiania: {options.StartupMethod}.")
    };
});
builder.Services.AddSingleton<MachineControlService>();
builder.Services.AddSingleton<BootSequenceService>();
builder.Services.AddHostedService<BootSequenceService>(serviceProvider =>
    serviceProvider.GetRequiredService<BootSequenceService>());
builder.Services.AddSingleton<MachineMetricsService>();
builder.Services.AddSingleton<GpioButtonService>();
builder.Services.AddHostedService<GpioButtonService>(serviceProvider =>
    serviceProvider.GetRequiredService<GpioButtonService>());

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

app.MapGet("/api/machine/boot-sequence", (
    BootSequenceService bootSequence) =>
{
    return Results.Ok(bootSequence.GetStatus());
});

app.MapGet("/api/gpio/buttons", (
    GpioButtonService gpioButtons) =>
{
    return Results.Ok(gpioButtons.GetStatus());
});

app.MapGet("/api/machine/graphical-interface", async (
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    GraphicalInterfaceStatus status =
        await machine.GetGraphicalInterfaceStatusAsync(cancellationToken);

    return Results.Ok(status);
});

app.MapPost("/api/machine/graphical-interface/toggle", async (
    MachineControlService machine,
    CancellationToken cancellationToken) =>
{
    MachineActionResult result =
        await machine.ToggleGraphicalInterfaceAsync(cancellationToken);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
})
.RequireRateLimiting("power");

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
    return result.Success
        ? Results.Accepted(value: result)
        : Results.BadRequest(result);
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

app.MapPost("/api/machine/systems/{systemId}/wake-boot", (
    string systemId,
    BootSequenceService bootSequence) =>
{
    MachineActionResult result = bootSequence.QueueBoot(systemId);

    return result.Success
        ? Results.Accepted(value: result)
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
