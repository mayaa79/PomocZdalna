using System.Windows;
using PomocZdalna.App.Core;
using PomocZdalna.App.Host;
using PomocZdalna.Shared;

namespace PomocZdalna.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly HostService _host;

    public MainWindow()
    {
        InitializeComponent();
        ServerBox.Text = _settings.ServerUrl;
        NameBox.Text = _settings.TechName;

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
    }
}
