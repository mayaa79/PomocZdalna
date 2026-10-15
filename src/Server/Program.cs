using PomocZdalna.Relay;

// Samodzielny serwer relay (opcjonalny) – ta sama logika co wbudowany w aplikację.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRelay(Path.Combine(AppContext.BaseDirectory, "logs"));

var app = builder.Build();
app.MapRelay();
app.Run();
