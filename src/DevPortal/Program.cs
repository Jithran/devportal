using System.Net;
using System.Text.Json.Serialization;
using DevPortal;
using DevPortal.Discovery;
using DevPortal.Security;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default,
});

builder.Host.UseWindowsService(service => service.ServiceName = "DevPortal");

var portal = builder.Configuration.GetSection(PortalOptions.Section).Get<PortalOptions>() ?? new PortalOptions();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Listen(IPAddress.Loopback, portal.Port);
    kestrel.Listen(IPAddress.IPv6Loopback, portal.Port);
});

builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProcessInspector>();
builder.Services.AddSingleton<GitResolver>();
builder.Services.AddSingleton<DockerClient>();
builder.Services.AddSingleton<HttpProber>();
builder.Services.AddSingleton<InstanceCatalog>();

var app = builder.Build();

app.UseMiddleware<LocalOnlyGuard>(portal.Port);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/instances", async (InstanceCatalog catalog, bool? all, CancellationToken cancellationToken) =>
    Results.Ok(await catalog.GetAsync(all ?? false, cancellationToken)));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
