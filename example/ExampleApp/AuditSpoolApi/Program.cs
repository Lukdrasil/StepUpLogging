using System.Text.Json;

using AuditSpoolApi;

using Lukdrasil.StepUpLogging;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

var builder = WebApplication.CreateBuilder(args);

builder.AddStepUpLogging();

var spoolRoot = Path.Combine(Path.GetTempPath(), "AuditSpoolApi");
builder.Services.AddSingleton<DemoReceiver>();
builder.Services.AddSingleton<DemoKeyRing>();
builder.Services.AddSingleton<IAuditPayloadEncryptor>(sp => sp.GetRequiredService<DemoKeyRing>());
builder.AddEncryptedSpoolAuditSink(o =>
{
    o.SpoolDirectory = Path.Combine(spoolRoot, "spool");
    o.EndpointBaseUrl = builder.Configuration["AuditDemo:EndpointBaseUrl"]!;
    o.ModuleName = "AuditSpoolApi";
    o.Version = "1.0.0";
    o.DrainInterval = TimeSpan.FromSeconds(1);
});

if (builder.Configuration.GetValue<bool>("AuditDemo:RegisterRejectionObserver"))
{
    builder.Services.AddSingleton<RefreshKeyOnUnknownKey>();
    builder.Services.AddSingleton<IAuditRejectionObserver>(sp => sp.GetRequiredService<RefreshKeyOnUnknownKey>());
}

var app = builder.Build();

app.UseStepUpRequestLogging();

app.MapPost("/orders/{id}", async (string id, IAuditLogger<Program> audit) =>
{
    await audit.AuditAsync(new AuditEvent
    {
        Action = "order.approved",
        ActorId = "user-1",
        ActorType = "user",
        Outcome = AuditOutcome.Success,
        TargetType = "order",
        TargetId = id,
    });
    return Results.Accepted();
});

app.MapPost("/receiver/audit", (JsonElement envelope, DemoReceiver receiver) => receiver.Receive(envelope));

app.MapPost("/receiver/forget-key", (DemoReceiver receiver, DemoKeyRing keyRing) =>
{
    receiver.Forget(keyRing.CurrentKeyId);
    return Results.Ok(new { forgotten = keyRing.CurrentKeyId });
});

app.MapGet("/receiver/state", (DemoReceiver receiver) => Results.Ok(new
{
    knownKeyIds = receiver.KnownKeyIds,
    stored = receiver.Stored,
    deadLettered = Directory.Exists(Path.Combine(spoolRoot, "dead-letter"))
        ? Directory.GetFiles(Path.Combine(spoolRoot, "dead-letter")).Length
        : 0,
}));

app.MapGet("/observer/state", (DemoKeyRing keyRing, IServiceProvider sp) => Results.Ok(new
{
    currentKeyId = keyRing.CurrentKeyId,
    rejections = sp.GetService<RefreshKeyOnUnknownKey>()?.Seen,
}));

app.MapGet("/demo/error", (ILogger<Program> logger) =>
{
    logger.LogInformation("Before the error, held in the pre-error buffer");
    logger.LogError("Demo error, steps the level up");
    return Results.Ok();
});

app.Run();
