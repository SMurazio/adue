using Mmo.Client.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("MMO_WEB_URL") ?? "http://127.0.0.1:5080");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var query = context.Request.Query;
    var options = new BridgeOptions(
        query["host"].FirstOrDefault() ?? "127.0.0.1",
        int.TryParse(query["port"].FirstOrDefault(), out var port) ? port : 7777,
        query["key"].FirstOrDefault() ?? "local-dev",
        query["name"].FirstOrDefault() ?? $"WebPlayer{Random.Shared.Next(1000, 9999)}");

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var bridge = new WebBridgeSession(socket, options);
    await bridge.RunAsync(context.RequestAborted);
});

app.Run();
