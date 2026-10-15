using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PomocZdalna.Relay;

/// <summary>Podłączenie relay do ASP.NET Core (używane przez serwer samodzielny i wbudowany).</summary>
public static class RelayEndpoints
{
    public static void AddRelay(this IServiceCollection services, string logDir)
    {
        services.AddSingleton(new SessionLog(logDir));
        services.AddSingleton<RelayHub>();
    }

    public static void MapRelay(this WebApplication app)
    {
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
    }
}

/// <summary>Serwer relay uruchamiany z poziomu aplikacji (Start/Stop) – bez osobnego procesu.</summary>
public sealed class RelayServer
{
    private WebApplication? _app;

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }

    /// <exception cref="Exception">np. gdy port jest zajęty.</exception>
    public async Task StartAsync(int port, string logDir)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddRelay(logDir);

        var app = builder.Build();
        app.MapRelay();
        await app.StartAsync();

        _app = app;
        Port = port;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;
        try { await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); } catch { }
        await app.DisposeAsync();
    }
}
