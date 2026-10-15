using PomocZdalna.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SessionLog>();
builder.Services.AddSingleton<RelayHub>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => "Pomoc Zdalna – serwer relay działa.");

app.Map("/ws", async (HttpContext ctx, RelayHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    await hub.HandleAsync(ws, ip, ctx.RequestAborted);
});

app.Run();
