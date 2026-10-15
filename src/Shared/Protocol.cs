using System.Text.Json;
using System.Text.Json.Serialization;

namespace PomocZdalna.Shared;

/// <summary>Typy komunikatów JSON (pole "t"). Klatki ekranu to osobne wiadomości binarne (JPEG).</summary>
public static class MsgType
{
    // host -> serwer
    public const string Register = "register";       // host zgłasza się do serwera (opcjonalnie z poprzednim ID)
    public const string Registered = "registered";   // serwer -> host: przydzielone ID
    // technik -> serwer -> host
    public const string Connect = "connect";         // technik prosi o połączenie (id + hasło)
    public const string ConnectReq = "connect_req";  // serwer -> host: ktoś chce się połączyć
    // host -> serwer -> technik
    public const string Accept = "accept";           // host zgodził się (z uprawnieniami)
    public const string Reject = "reject";           // host odmówił / złe hasło
    // wspólne
    public const string Error = "error";             // serwer -> klient: błąd
    public const string Ended = "ended";             // sesja zakończona przez drugą stronę / serwer
    public const string Bye = "bye";                 // strona kończy sesję
    // sterowanie (technik -> host)
    public const string Mouse = "mouse";
    public const string Key = "key";
    public const string Cmd = "cmd";
    public const string CmdResult = "cmd_result";    // host -> technik
}

public static class Reasons
{
    public const string BadPassword = "badpass";
    public const string Denied = "denied";
    public const string Busy = "busy";
}

/// <summary>Jedna płaska struktura dla wszystkich komunikatów – pola puste nie są serializowane.</summary>
public sealed class Msg
{
    [JsonPropertyName("t")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("pass")] public string? Pass { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    // uprawnienia nadane przez klienta
    [JsonPropertyName("control")] public bool? Control { get; set; }
    [JsonPropertyName("commands")] public bool? Commands { get; set; }

    // mysz: kind = move|down|up|wheel, x/y znormalizowane 0..1, btn 0=lewy 1=prawy 2=środkowy
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("btn")] public int? Btn { get; set; }
    [JsonPropertyName("delta")] public int? Delta { get; set; }

    // klawiatura: kod klawisza wirtualnego Windows
    [JsonPropertyName("vk")] public int? Vk { get; set; }
    [JsonPropertyName("down")] public bool? Down { get; set; }

    // polecenia: shell = ps|cmd
    [JsonPropertyName("shell")] public string? Shell { get; set; }
    [JsonPropertyName("cmd")] public string? Cmd { get; set; }
    [JsonPropertyName("out")] public string? Output { get; set; }
    [JsonPropertyName("exit")] public int? Exit { get; set; }
}

public static class Proto
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(Msg m) => JsonSerializer.Serialize(m, Opts);

    public static Msg? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Msg>(json, Opts); }
        catch (JsonException) { return null; }
    }
}
