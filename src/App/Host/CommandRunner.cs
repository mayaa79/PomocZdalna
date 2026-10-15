using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PomocZdalna.App.Host;

/// <summary>Wykonuje polecenia zlecone przez technika (PowerShell lub cmd) z uprawnieniami użytkownika, który uruchomił aplikację.</summary>
public static class CommandRunner
{
    private const int MaxOutput = 256 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    static CommandRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<(string Output, int Exit)> RunAsync(string shell, string command, CancellationToken ct)
    {
        ProcessStartInfo psi;
        Encoding enc;

        if (shell == "cmd")
        {
            enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi = new ProcessStartInfo("cmd.exe", "/d /c " + command);
        }
        else
        {
            enc = new UTF8Encoding(false);
            var script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command;
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64);
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = enc;
        psi.StandardErrorEncoding = enc;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var sb = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (sb) { if (sb.Length < MaxOutput) sb.AppendLine(line); }
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Append(e.Data);
        p.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
                p.WaitForExit(); // dokończ odczyt strumieni
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                lock (sb) sb.AppendLine("[przerwano: przekroczono limit czasu 120 s]");
                return (sb.ToString(), -1);
            }

            lock (sb) return (sb.ToString(), p.ExitCode);
        }
        catch (Exception ex)
        {
            return ("Błąd uruchamiania: " + ex.Message, -1);
        }
    }
}
