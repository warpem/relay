using Autofac;
using Autofac.Extensions.DependencyInjection;
using Blazored.LocalStorage;
using CommandLine;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Components.Tooltip;
using Microsoft.IdentityModel.Logging;
using Refund.Configuration;
using Refund.Configuration.Constants;
using Refund.DataModel;
using Refund.Jobs;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Relay;
using Relay.Screens.Main.View;
using Relay.Services;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using AuthenticationService = Refund.Services.AuthenticationService;
using Serilog;
using System.Runtime.InteropServices;

// Program.cs is the entry point for the Relay application and sets up the application
// with all necessary services, configuration, and middleware.

[DllImport("libc", SetLastError = true)]
static extern uint umask(uint mask);

// Set umask on Unix systems to set files to ug+rwx by default
if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    umask(0x007);

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog for file logging with automatic routing by source context
var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.Map("SourceContext", (sourceContext, wt) => 
        wt.File($"logs/{timestamp}/{sourceContext}.log", 
            rollingInterval: RollingInterval.Infinite,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1)))
    .CreateLogger();

// Use Serilog for all logging
builder.Host.UseSerilog();

// Enable PII logging in development to help with debugging authentication issues
if (builder.Environment.IsDevelopment())
{
    IdentityModelEventSource.ShowPII = true;
}

// Set up configuration based on environment
builder.Configuration.SetBasePath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!)
       .AddJsonFile(builder.Environment.IsDevelopment() ? "appsettings.Development.json" : "appsettings.json")
       .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "relay.json"), optional: true, reloadOnChange: true)
       .Build();

// Parse command-line arguments using CommandLineParser
Cli cli = new();
Parser.Default.ParseArguments<Cli>(args).WithParsed(x => cli = x);

// Register core ASP.NET services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();
builder.Services.AddControllersWithViews().AddRazorRuntimeCompilation();

// Add file service for secure file access
builder.Services.AddSingleton<FileService>();

// Register application configuration
var relayOptions = new RelayConfiguration();
builder.Configuration.GetSection(RelayConfiguration.Relay).Bind(relayOptions);
builder.Services.AddSingleton(relayOptions);

// Initialize job type registry
Job.PopulateStatic();

// Register core data services
// DataManager is the central repository for all application data
builder.Services.AddSingleton<DataManager>(new DataManager(relayOptions));

// SecurityTokenService manages security tokens for user registration
builder.Services.AddSingleton<SecurityTokenService>();
builder.Services.AddHostedService<SecurityTokenService>();

// PersonalAccessTokenService stores PATs used to authenticate agents over MCP.
// Register once and reuse the same instance as the hosted service so there is a single store.
builder.Services.AddSingleton<PersonalAccessTokenService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersonalAccessTokenService>());

// Register utility services
builder.Services.AddScoped<HttpClient>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(typeof(UniqueIdGeneratorService<>));
builder.Services.AddBlazoredLocalStorage();

// Configure Blazor options with increased limits for development
#if DEBUG
builder.Services.AddServerSideBlazor()
       .AddHubOptions(options => options.MaximumReceiveMessageSize = 64 * 1024)
       .AddCircuitOptions(options => { options.DetailedErrors = true; });
#endif

// Configure SignalR options for Blazor Server
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents()
       .AddHubOptions(options =>
       {
           options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
           options.EnableDetailedErrors = true;
           options.HandshakeTimeout = TimeSpan.FromSeconds(15);
           options.KeepAliveInterval = TimeSpan.FromSeconds(15);
           options.MaximumParallelInvocationsPerClient = 1;
           options.MaximumReceiveMessageSize = 64 * 1024;
           options.StreamBufferCapacity = 10;
       });

// Add user secrets for development
builder.Configuration.AddUserSecrets<Program>();

// Configure Autofac for dependency injection
builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder => containerBuilder.RegisterConfigurations());
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());

// Load authentication configuration
var authConfig = builder.Configuration.GetSection(CommonConstants.Authentication)
                        .LoadAuthenticationConfiguration();

// Add FluentUI components
builder.Services.AddFluentUIComponents();
builder.Services.AddScoped<ITooltipService, TooltipService>();

// Register application services

// RelaySession maintains the current user's navigation state
builder.Services.AddScoped<RelaySession>();

// Authentication service for login/logout functionality
builder.Services.AddScoped<AuthenticationService>();

// Add data protection and secure storage.
// Keys are persisted to disk so auth cookies survive restarts and redeployments.
// The path is configurable via relay.json ("Relay": { "DataProtectionKeysPath": "..." });
// it defaults to a "keys" directory next to the other relay data files.
builder.Services.AddDataProtection()
       .PersistKeysToFileSystem(new DirectoryInfo(relayOptions.DataProtectionKeysPath))
       .SetApplicationName("relay");
builder.Services.AddSingleton<MemorySecureStorage>();

// UI state management services
builder.Services.AddScoped<CardSelectionService>();        // Manages selection state in list screens
builder.Services.AddScoped<JobEditorService>();            // Manages job parameter editing
builder.Services.AddScoped<FactoryEditorService>();        // Manages factory instance parameter editing
builder.Services.AddScoped<ExpandedJobViewService>();      // Manages expanded job view state
builder.Services.AddScoped<JobStatusToaster>();            // Displays job status notifications
builder.Services.AddScoped<MenuActionService>();           // Manages context-sensitive actions
builder.Services.AddScoped<ScatterHighlightService>();     // Manages scatter plot highlight state
builder.Services.AddScoped<JobSortingService>();            // Manages job sort state in ViewScreen
builder.Services.AddScoped<ViewDragDropService>();          // Manages drag-and-drop state in ViewScreen
builder.Services.AddScoped<GlobalTooltipService>();        // Manages Relay's own tooltips (to get functionality unavailable in FluentUI's tooltips)
builder.Services.AddScoped<DiagramViewService>();          // Manages diagram view mode and zoom/pan state

// Configure cookie authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
       .AddCookie(options =>
       {
           options.ExpireTimeSpan = TimeSpan.FromDays(30);
           options.SlidingExpiration = true;
       })
       .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Relay.Services.PatAuthenticationHandler>(
           Relay.Services.PatAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization();

// In-process MCP server (read-only tools), authenticated by the Pat scheme.
builder.Services.AddMcpServer()
       .WithHttpTransport(o => o.Stateless = true)
       .WithTools<Relay.Services.RelayMcpTools>();

// Build the application
var app = builder.Build();

// Configure the HTTP request pipeline

// Add error handling for production
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Configure middleware pipeline
app.UseRouting();                // Set up routing
app.UseAntiforgery();            // Prevent CSRF attacks

// Add authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

// Redirect unauthenticated requests to /login before Blazor renders.
// Without this, deep URLs (e.g. /P1/S2/V3) show a blank page because the
// auth check in MainLayout.OnAfterRenderAsync fires too late.
//
// MapStaticAssets() is endpoint-routed and runs after this middleware, so any
// static-asset path not explicitly allowed would be 302'd. Rather than
// maintaining a directory allowlist, any path with a file extension is let
// through: app routes in this codebase never carry extensions, so this cleanly
// separates static assets (*.css, *.js, *.woff2, *.razor.js, ...) from pages.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;

    if (!isAuthenticated
        && !path.StartsWithSegments("/login")
        && !path.StartsWithSegments("/process-login")
        && !path.StartsWithSegments("/register")
        && !path.StartsWithSegments("/start-sso")
        && !path.StartsWithSegments("/sso-callback")
        && !path.StartsWithSegments("/process-logout")
        && !path.StartsWithSegments("/_blazor")
        && !path.StartsWithSegments("/_framework")
        && !path.StartsWithSegments("/api")
        && !Path.HasExtension(path.Value))
    {
        context.Response.Redirect("/login");
        return;
    }

    await next();
});

// Map endpoints
app.MapStaticAssets();           // Serve static files with compression and fingerprinting (needed for FluentUI JS initializers in published builds)
app.MapRazorPages();
app.MapControllers();

// MCP endpoint: requires a valid personal access token (Pat scheme).
var patPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
        Relay.Services.PatAuthenticationHandler.SchemeName)
    .RequireAuthenticatedUser()
    .Build();
app.MapMcp("/api/mcp").RequireAuthorization(patPolicy);
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

// Log all registered endpoints (helpful for debugging)
var dataSources = app.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
foreach (var dataSource in dataSources)
{
    foreach (var endpoint in dataSource.Endpoints)
    {
        Log.Debug("Endpoint: {EndpointDisplayName}", endpoint.DisplayName);
    }
}

// Log application startup completion
Log.Information("=== APPLICATION STARTUP COMPLETED ===");
Log.Information("Application Name: {ApplicationName}, Environment: {Environment}", 
    builder.Environment.ApplicationName, builder.Environment.EnvironmentName);
Log.Information("Process ID: {ProcessId}, Machine: {MachineName}, CLR: {CLRVersion}",
    Environment.ProcessId, Environment.MachineName, Environment.Version);

// Start the application
try
{
    Log.Information("Starting Relay application...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.Information("=== APPLICATION SHUTDOWN COMPLETED ===");
    Log.CloseAndFlush();
}