using System.Threading.RateLimiting;
using LateFeeBox.Web.Data;
using LateFeeBox.Web.Endpoints;
using LateFeeBox.Web.Options;
using LateFeeBox.Web.Security;
using LateFeeBox.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<BaleOptions>(builder.Configuration.GetSection("Bale"));
builder.Services.Configure<InitialAdminOptions>(builder.Configuration.GetSection("InitialAdmin"));
builder.Services.Configure<MoneyOptions>(builder.Configuration.GetSection("Money"));

var storagePath = Path.GetFullPath(builder.Configuration["Storage:Path"] ?? "data/state.json");
var dataDirectory = Path.GetDirectoryName(storagePath) ?? Path.GetFullPath("data");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("LateFeeBox");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "LateFeeBox.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect("/admin/index.html");
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

builder.Services.AddSingleton<JsonStore>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<MoneyService>();
builder.Services.AddSingleton<BaleApiClient>();
builder.Services.AddSingleton<BotUpdateHandler>();
builder.Services.AddHostedService<BaleLongPollingWorker>();

var app = builder.Build();

var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new
    {
        title = "خطای داخلی سامانه",
        status = 500,
        detail = "عملیات انجام نشد. لاگ سرویس را بررسی کنید."
    });
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/admin/index.html"));
app.MapHealthChecks("/health");
app.MapAdminEndpoints();

var initialAdmin = app.Services.GetRequiredService<IOptions<InitialAdminOptions>>().Value;
var store = app.Services.GetRequiredService<JsonStore>();
var passwordService = app.Services.GetRequiredService<PasswordService>();
await store.InitializeAsync(initialAdmin.Username, initialAdmin.Password, passwordService);

app.Run();
