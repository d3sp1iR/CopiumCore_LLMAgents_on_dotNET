var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/ready", () => Results.Json(
    new { status = "notReady", reason = "Runtime and dependencies are not implemented" },
    statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();
