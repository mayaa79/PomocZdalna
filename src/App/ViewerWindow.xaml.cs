using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PomocZdalna.App.Core;
using PomocZdalna.Shared;

namespace PomocZdalna.App;

/// <summary>Okno technika: podgląd pulpitu klienta, mysz/klawiatura i konsola poleceń.</summary>
public partial class ViewerWindow : Window
{
    private readonly RelayClient _client;
    private readonly bool _canControl;
    private byte[]? _latest;
    private int _renderPending;
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();
    private bool _ended;

    public ViewerWindow(RelayClient client, bool canControl, bool canCommands, string id)
    {
        InitializeComponent();
        _client = client;
        _canControl = canControl;

        Title = $"Pomoc zdalna — {id[..3]} {id[3..6]} {id[6..]}";
        ControlCheck.IsEnabled = canControl;
        ControlCheck.IsChecked = canControl;
        if (!canControl) ControlCheck.Content = "Sterowanie niedozwolone przez klienta";
        CmdExpander.Visibility = canCommands ? Visibility.Visible : Visibility.Collapsed;

        client.BinaryReceived += OnFrame;
        client.JsonReceived += OnJson;
        client.Closed += reason => Dispatcher.BeginInvoke(() => OnEnded(reason));
    }

    private bool Controlling => _canControl && !_ended && ControlCheck.IsChecked == true;

    private void Window_Loaded(object sender, RoutedEventArgs e) => Screen.Focus();

    // ------------------------------------------------------------ obraz

    private void OnFrame(byte[] data)
    {
        Interlocked.Exchange(ref _latest, data);
        if (Interlocked.Exchange(ref _renderPending, 1) == 0)
            Dispatcher.BeginInvoke(ShowLatest);
    }

    private void ShowLatest()
    {
        Interlocked.Exchange(ref _renderPending, 0);
        var data = Interlocked.Exchange(ref _latest, null);
        if (data is null) return;
        try
        {
            using var ms = new MemoryStream(data);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            Screen.Source = frame;
            StatusText.Text = $"{frame.PixelWidth}×{frame.PixelHeight}";
        }
        catch { }
    }

    private void OnJson(Msg m)
    {
        switch (m.Type)
        {
            case MsgType.CmdResult:
                Dispatcher.BeginInvoke(() => AppendOutput((m.Output ?? "") + $"[kod wyjścia: {m.Exit}]\n\n"));
                break;
            case MsgType.Ended:
                Dispatcher.BeginInvoke(() => OnEnded(m.Reason ?? "Sesja zakończona"));
                break;
        }
    }

    private void OnEnded(string reason)
    {
        if (_ended) return;
        _ended = true;
        StatusText.Text = reason;
        ControlCheck.IsEnabled = false;
        Title += " (rozłączono)";
    }

    // ------------------------------------------------------------ mysz

    private bool TryMap(MouseEventArgs e, out double nx, out double ny)
    {
        nx = ny = 0;
        if (Screen.Source is not BitmapSource src) return false;
        var p = e.GetPosition(Screen);
        double aw = Screen.ActualWidth, ah = Screen.ActualHeight;
        double scale = Math.Min(aw / src.Width, ah / src.Height);
        double dw = src.Width * scale, dh = src.Height * scale;
        nx = (p.X - (aw - dw) / 2) / dw;
        ny = (p.Y - (ah - dh) / 2) / dh;
        return nx is >= 0 and <= 1 && ny is >= 0 and <= 1;
    }

    private static int ButtonId(MouseButton b) => b switch { MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0 };

    private void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Controlling || _moveClock.ElapsedMilliseconds < 30 || !TryMap(e, out var x, out var y)) return;
        _moveClock.Restart();
        _ = _client.SendAsync(new Msg { Type = MsgType.Mouse, Kind = "move", X = x, Y = y });
    }

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Screen.Focus();
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        if (!Controlling || !TryMap(e, out var x, out var y)) return;
        Screen.CaptureMouse();
        _ = _client.SendAsync(new Msg { Type = MsgType.Mouse, Kind = "down", X = x, Y = y, Btn = ButtonId(e.ChangedButton) });
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Screen.IsMouseCaptured) Screen.ReleaseMouseCapture();
        if (!Controlling) return;
        TryMap(e, out var x, out var y);
        x = Math.Clamp(x, 0, 1); y = Math.Clamp(y, 0, 1);
        _ = _client.SendAsync(new Msg { Type = MsgType.Mouse, Kind = "up", X = x, Y = y, Btn = ButtonId(e.ChangedButton) });
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Controlling) return;
        _ = _client.SendAsync(new Msg { Type = MsgType.Mouse, Kind = "wheel", Delta = e.Delta });
    }

    // ------------------------------------------------------------ klawiatura

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => ForwardKey(e, true);
    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => ForwardKey(e, false);

    private void ForwardKey(KeyEventArgs e, bool down)
    {
        if (!Controlling || !Screen.IsKeyboardFocused) return;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        _ = _client.SendAsync(new Msg { Type = MsgType.Key, Vk = vk, Down = down });
        e.Handled = true;
    }

    // ------------------------------------------------------------ konsola

    private void CmdInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _ended) return;
        var text = CmdInput.Text.Trim();
        if (text.Length == 0) return;
        var shell = ShellBox.SelectedIndex == 1 ? "cmd" : "ps";
        AppendOutput($"> {text}\n");
        CmdInput.Clear();
        _ = _client.SendAsync(new Msg { Type = MsgType.Cmd, Shell = shell, Cmd = text });
        e.Handled = true;
    }

    private void AppendOutput(string s)
    {
        CmdOutput.AppendText(s);
        CmdOutput.ScrollToEnd();
    }

    // ------------------------------------------------------------ zamykanie

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await Disconnect();
        Close();
    }

    private async Task Disconnect()
    {
        if (!_ended) await _client.SendAsync(new Msg { Type = MsgType.Bye });
        _ended = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_ended) _ = _client.SendAsync(new Msg { Type = MsgType.Bye });
        _ended = true;
        _client.Dispose();
    }
}
