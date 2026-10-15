using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using PomocZdalna.App.Core;
using PomocZdalna.App.Host;
using PomocZdalna.Relay;
using PomocZdalna.Shared;

namespace PomocZdalna.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly HostService _host;
    private readonly RelayServer _relay = new();

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PomocZdalna", "logs");

    public MainWindow()
    {
        InitializeComponent();
        ServerBox.Text = _settings.ServerUrl;
        NameBox.Text = _settings.TechName;
        EmbeddedCheck.IsChecked = _settings.EmbeddedServer;
        PortBox.Text = _settings.ServerPort.ToString();

        _host = new HostService(_settings);
        _host.AskConsent = AskConsentAsync;
        _host.Status += s => Dispatcher.BeginInvoke(() => HostStatus.Text = s);
        _host.IdChanged += () => Dispatcher.BeginInvoke(RefreshHostFields);
        _host.SessionChanged += name => Dispatcher.BeginInvoke(() =>
        {
            Banner.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
            BannerText.Text = $"Trwa sesja zdalna: {name}";
            if (name is not null) { WindowState = WindowState.Normal; Activate(); }
        });
        RefreshHostFields();
        Loaded += async (_, _) => { if (_settings.EmbeddedServer) await StartRelayAsync(); else RefreshServerInfo(); };
    }

    // ------------------------------------------------------------ SERWER WBUDOWANY

    private async Task StartRelayAsync()
    {
        try
        {
            await _relay.StartAsync(_settings.ServerPort, LogDir);
        }
        catch (Exception ex)
        {
            ServerStatus.Text = $"Nie udało się uruchomić serwera na porcie {_settings.ServerPort}: {ex.Message}";
            AddrInfo.Text = "";
            return;
        }
        RefreshServerInfo();
    }

    private void RefreshServerInfo()
    {
        if (!_relay.IsRunning)
        {
            ServerStatus.Text = "Serwer wyłączony";
            AddrInfo.Text = "";
            return;
        }

        ServerStatus.Text = $"Serwer działa na porcie {_relay.Port}";
        var ips = new List<string>();
        try
        {
            ips = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => $"ws://{a}:{_relay.Port}/ws").ToList();
        }
        catch { }

        AddrInfo.Text =
            "Adres w sieci lokalnej (dla klientów w tej samej sieci): " + (ips.Count > 0 ? string.Join(", ", ips) : "—") +
            "\nZ internetu: przekieruj port TCP " + _relay.Port + " na routerze do tego laptopa i podaj klientom publiczne IP lub nazwę DDNS. " +
            "Przy pierwszym uruchomieniu zezwól w Zaporze Windows na dostęp w sieciach prywatnych i publicznych.";
    }

    private async void EmbeddedCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.EmbeddedServer = EmbeddedCheck.IsChecked == true;
        SaveSettings();
        if (_settings.EmbeddedServer) await StartRelayAsync();
        else { await _relay.StopAsync(); RefreshServerInfo(); }
    }

    private async void PortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1024 or > 65535)
        {
            PortBox.Text = _settings.ServerPort.ToString();
            return;
        }
        if (port == _settings.ServerPort) return;

        _settings.ServerPort = port;
        SaveSettings();
        if (_relay.IsRunning)
        {
            await _relay.StopAsync();
            await StartRelayAsync();
        }
    }

    // ------------------------------------------------------------ TRYB KLIENTA (host)

    private void RefreshHostFields()
    {
        IdText.Text = _host.Id is { Length: 9 } id ? $"{id[..3]} {id[3..6]} {id[6..]}" : "—";
        PassText.Text = _host.Running ? _host.Password : "—";
        HostToggle.Content = _host.Running ? "Wyłącz udostępnianie" : "Włącz udostępnianie";
    }

    private void HostToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_host.Running) _host.Stop(); else _host.Start();
        RefreshHostFields();
    }

    private void NewPass_Click(object sender, RoutedEventArgs e)
    {
        _host.RegeneratePassword();
        RefreshHostFields();
    }

    private async void EndSession_Click(object sender, RoutedEventArgs e) => await _host.EndSessionByUserAsync();

    private async Task<ConsentResult?> AskConsentAsync(string techName, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ConsentResult?>();
        await Dispatcher.InvokeAsync(() =>
        {
            var w = new ConsentWindow(techName);
            ct.Register(() => Dispatcher.BeginInvoke(() => { if (w.IsLoaded) w.Close(); }));
            w.Closed += (_, _) => tcs.TrySetResult(w.Result);
            w.Show();
            w.Activate();
        });
        return await tcs.Task;
    }

    // ------------------------------------------------------------ TRYB TECHNIKA

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var id = new string(ConnId.Text.Where(char.IsDigit).ToArray());
        if (id.Length != 9) { ConnStatus.Text = "ID powinno mieć 9 cyfr."; return; }
        if (string.IsNullOrWhiteSpace(ConnPass.Password)) { ConnStatus.Text = "Podaj hasło."; return; }

        ConnectBtn.IsEnabled = false;
        ConnStatus.Text = "Łączenie z serwerem...";

        var client = new RelayClient();
        var tcs = new TaskCompletionSource<Msg>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMsg(Msg m) { if (m.Type is MsgType.Accept or MsgType.Reject or MsgType.Error) tcs.TrySetResult(m); }
        client.JsonReceived += OnMsg;
        client.Closed += reason => tcs.TrySetResult(new Msg { Type = MsgType.Error, Reason = reason });

        try
        {
            await client.ConnectAsync(_settings.ServerUrl);
            await client.SendAsync(new Msg { Type = MsgType.Connect, Id = id, Pass = ConnPass.Password, Name = _settings.TechName });
            ConnStatus.Text = "Czekam na zgodę klienta...";

            var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(75));
            client.JsonReceived -= OnMsg;

            if (res.Type == MsgType.Accept)
            {
                new ViewerWindow(client, res.Control == true, res.Commands == true, id).Show();
                ConnStatus.Text = "Połączono.";
                ConnPass.Clear();
            }
            else
            {
                client.Dispose();
                ConnStatus.Text = res.Type == MsgType.Reject
                    ? res.Reason switch
                    {
                        Reasons.BadPassword => "Nieprawidłowe hasło.",
                        Reasons.Denied => "Klient odrzucił połączenie.",
                        Reasons.Busy => "Komputer klienta jest zajęty inną sesją.",
                        _ => "Połączenie odrzucone."
                    }
                    : res.Reason ?? "Błąd połączenia.";
            }
        }
        catch (Exception ex)
        {
            client.Dispose();
            ConnStatus.Text = "Błąd: " + ex.Message;
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------ USTAWIENIA

    private void Settings_LostFocus(object sender, RoutedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        var url = ServerBox.Text.Trim();
        if (url.StartsWith("ws://") || url.StartsWith("wss://")) _settings.ServerUrl = url;
        if (!string.IsNullOrWhiteSpace(NameBox.Text)) _settings.TechName = NameBox.Text.Trim();
        _settings.Save();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_host.SessionActive &&
            MessageBox.Show("Trwa sesja zdalna. Zamknięcie aplikacji ją zakończy. Zamknąć?", "Pomoc Zdalna",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _host.Stop();
        Task.Run(() => _relay.StopAsync()).Wait(4000);
    }
}
