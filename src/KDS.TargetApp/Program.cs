var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-App-Version"] = builder.Configuration["APP_VERSION"] ?? "local";
    context.Response.Headers["X-Pod-Name"] = builder.Configuration["POD_NAME"] ?? Environment.MachineName;
    await next(context);
});

app.MapGet("/", () => "Hello World!");
app.MapGet("/health/ready", () => Results.Ok());

app.Run();
