namespace PomocZdalna.Relay;

/// <summary>Prosty dziennik audytowy sesji (plik dzienny w katalogu logs/).</summary>
public sealed class SessionLog
{
    private readonly object _gate = new();
    private readonly string _dir;

    public SessionLog(string dir) => _dir = dir;

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_dir);
                var file = Path.Combine(_dir, $"sessions-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.UtcNow:O} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // log nie może przerywać pracy serwera
        }
    }
}
