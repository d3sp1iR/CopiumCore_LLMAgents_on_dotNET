var app = TestGraphApp.Build(args);
app.Run();

public static class TestGraphApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        // Demonstration fixture only: this endpoint has no authentication or access control.
        app.Logger.LogWarning("Test Graph API is an insecure demonstration service; do not expose real data.");

        app.MapGet("/objects/{id}", GetObject);

        return app;
    }

    private static IResult GetObject(string id) => id switch
        {
            "req-17" => Results.Ok(new
            {
                id = "req-17",
                type = "Application",
                version = "7",
                attributes = new { groupCode = (string?)null, projectId = "project-4", decisionId = "doc-9" }
            }),
            "doc-9" => Results.Ok(new
            {
                id = "doc-9",
                type = "Decision",
                version = "3",
                attributes = new { applicationId = "req-17", groupCode = "ИУ5-31Б", approved = true }
            }),
            _ => Results.NotFound()
        };
}
