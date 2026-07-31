using Microsoft.EntityFrameworkCore;
using CodingAgents.Server;
using CodingAgents.Server.Data;
using CodingAgents.Server.Hubs;
using CodingAgents.Server.Services;
using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

// Configure settings
builder.Services.Configure<AppSettings>(builder.Configuration);

// Add Database Context
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add services based on config
builder.Services.AddSingleton(sp =>
{
    var config = builder.Configuration.GetSection("WhatsApp").Get<WhatsAppConfig>();
    return new WhatsAppService(config ?? new WhatsAppConfig());
});

builder.Services.AddSingleton(sp =>
{
    var config = builder.Configuration.GetSection("Email").Get<EmailConfig>();
    return new EmailService(config ?? new EmailConfig());
});

// Add SignalR. Raise the receive-message cap well above the 32 KB default so the worker
// can upload screenshots/images to be shown in the chat. The auth filter gates every hub
// method so an unauthenticated caller cannot drive the local agent worker.
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 20 * 1024 * 1024; // 20 MB
    Microsoft.AspNetCore.SignalR.HubOptionsExtensions.AddFilter(options, new CodingAgents.Server.Hubs.AuthHubFilter());
});

// Track connected local PC workers
builder.Services.AddSingleton<WorkerRegistry>();

// App access password (hashed, single credential) and session tokens
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<TokenStore>();

// Add Workflow Manager
builder.Services.AddSingleton<WorkflowManager>();

// Add Background monitoring service
builder.Services.AddHostedService<RateLimitService>();

// CORS configuration. Only the configured client origins may call the API with credentials;
// previously any origin was allowed, which let any web page drive the hub from a browser.
// Set "AllowedOrigins" in configuration (comma-separated) to add your client URLs.
var allowedOrigins = (builder.Configuration["AllowedOrigins"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();

        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }
        else
        {
            // Safe default: local development clients only.
            policy.SetIsOriginAllowed(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
        }
    });
});

var app = builder.Build();

// Automatically ensure DB is created
try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.Database.EnsureCreated();

        // Safe SQL Schema Patch to add the new settings columns if they don't exist
        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ModelConfigurations' and xtype='U')
                BEGIN
                    CREATE TABLE ModelConfigurations (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        Name nvarchar(max) NOT NULL,
                        Provider nvarchar(max) NOT NULL,
                        ModelName nvarchar(max) NOT NULL,
                        BaseUrl nvarchar(max) NULL,
                        ApiKey nvarchar(max) NULL
                    );
                END

                IF COL_LENGTH('Settings', 'MaxReviewIterations') IS NULL
                BEGIN
                    ALTER TABLE Settings ADD MaxReviewIterations int NOT NULL DEFAULT 3;
                    ALTER TABLE Settings ADD AnalystModel nvarchar(max) NOT NULL DEFAULT 'Ollama:llama3.2:latest';
                    ALTER TABLE Settings ADD EngineerModel nvarchar(max) NOT NULL DEFAULT 'Ollama:llama3.2:latest';
                    ALTER TABLE Settings ADD ExecutorModel nvarchar(max) NOT NULL DEFAULT 'Ollama:llama3.2:latest';
                    ALTER TABLE Settings ADD DotNetReviewerModel nvarchar(max) NOT NULL DEFAULT 'Ollama:llama3.2:latest';
                    ALTER TABLE Settings ADD ArchitectReviewerModel nvarchar(max) NOT NULL DEFAULT 'Ollama:llama3.2:latest';
                    ALTER TABLE Settings ADD OpenAIApiKey nvarchar(max) NOT NULL DEFAULT '';
                    ALTER TABLE Settings ADD OpenAIBaseUrl nvarchar(max) NOT NULL DEFAULT '';
                    ALTER TABLE Settings ADD AnthropicApiKey nvarchar(max) NOT NULL DEFAULT '';
                    ALTER TABLE Settings ADD AnthropicBaseUrl nvarchar(max) NOT NULL DEFAULT '';
                END

                IF COL_LENGTH('Settings', 'ChatModel') IS NULL
                BEGIN
                    ALTER TABLE Settings ADD ChatModel nvarchar(max) NOT NULL DEFAULT 'Ollama:llama3.2:latest';
                END

                IF COL_LENGTH('Workflows', 'WorkspacePath') IS NULL
                BEGIN
                    ALTER TABLE Workflows ADD WorkspacePath nvarchar(max) NOT NULL DEFAULT '';
                END

                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='AppCredentials' and xtype='U')
                BEGIN
                    -- Id must be IDENTITY to match EF's convention for int primary keys,
                    -- otherwise inserts fail depending on which path created the table.
                    CREATE TABLE AppCredentials (
                        Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        PasswordHash nvarchar(max) NOT NULL,
                        PasswordSalt nvarchar(max) NOT NULL
                    );
                END
            ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Schema Patch Error] Failed to update Settings table: {ex.Message}");
        }

        // Seed default settings if empty
        if (!db.Settings.Any())
        {
            db.Settings.Add(new CodingAgents.Shared.SystemSettings
            {
                DefaultExecutor = "Antigravity",
                EnableWhatsApp = true,
                EnableEmail = false
            });
            db.SaveChanges();
        }

        // Reconcile workflows left in a non-terminal state by a previous run or crash,
        // otherwise they stay stuck (e.g. "Executing") forever with no worker to finish them.
        try
        {
            var settledStatuses = new[] { "Completed", "Failed", "Stalemate", "Queued" };
            var orphaned = db.Workflows.Where(w => !settledStatuses.Contains(w.Status)).ToList();
            foreach (var wf in orphaned)
            {
                if (wf.Status == "Pending")
                {
                    // Never got relayed to a worker; safe to re-queue and run later.
                    wf.Status = "Queued";
                }
                else
                {
                    // Was mid-execution; the executor may have partially applied changes,
                    // so fail it rather than silently restarting a non-idempotent pipeline.
                    wf.Status = "Failed";
                    db.WorkflowLogs.Add(new CodingAgents.Shared.WorkflowLog
                    {
                        WorkflowId = wf.Id,
                        Stage = "System",
                        Message = "Server restarted while this workflow was in progress. It was interrupted and marked as failed. Please resubmit if needed.",
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
            if (orphaned.Count > 0)
            {
                db.SaveChanges();
                Console.WriteLine($"[Startup Reconcile] Reconciled {orphaned.Count} interrupted workflow(s).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup Reconcile Error] Failed to reconcile workflows: {ex.Message}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Database Setup Error] Failed to initialize database: {ex.Message}");
}

// Warn loudly if the deployment is still using shipped default secrets.
if (string.IsNullOrEmpty(builder.Configuration["WorkerKey"]))
{
    Console.WriteLine("[SECURITY WARNING] 'WorkerKey' is not configured; using the built-in default. Set a unique WorkerKey on both the server and the worker.");
}
if (allowedOrigins.Length == 0)
{
    Console.WriteLine("[SECURITY] No 'AllowedOrigins' configured; only loopback origins are permitted. Set AllowedOrigins to expose the app to other hosts.");
}

app.UseStaticFiles();

// Serve agent-generated artifacts (screenshots/images) at /artifacts. Uses an explicit
// provider rooted at ContentRoot/artifacts so it doesn't depend on a wwwroot folder.
var artifactsRoot = Path.Combine(app.Environment.ContentRootPath, "artifacts");
Directory.CreateDirectory(artifactsRoot);

// Gate /artifacts behind a capability token. Screenshots and uploaded files are private, and
// browsers can't attach hub credentials to an <img> request, so the client appends a
// short-lived token issued after login. This middleware must run before the static handler.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/artifacts"))
    {
        var tokens = context.RequestServices.GetRequiredService<TokenStore>();
        var token = context.Request.Query["t"].FirstOrDefault()
                    ?? context.Request.Cookies["artifact_token"];

        if (!tokens.ValidateArtifactToken(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        // Don't leak the token via the Referer header, and keep these out of shared caches.
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Cache-Control"] = "private, max-age=300";
    }
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(artifactsRoot),
    RequestPath = "/artifacts"
});

app.UseCors();

app.MapGet("/", () => "CodingAgents ASP.NET Core SignalR Server is running.");

app.MapHub<ChatHub>("/chathub");

app.Run();
