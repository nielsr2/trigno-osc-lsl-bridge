using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace G702_OSC_Tester
{
    /// <summary>
    /// Standalone OSC listener / tester. Binds a UDP port, prints every OSC
    /// message it receives, and keeps live per-address statistics. No OSC
    /// library dependency — parses packets by hand so we see exactly what's
    /// on the wire.
    ///
    /// Usage:
    ///   G702-OSC-Tester.exe                 listen on UDP 7001 (default)
    ///   G702-OSC-Tester.exe --port 9000     listen on a specific port
    ///   G702-OSC-Tester.exe --quiet         suppress per-message prints,
    ///                                        only show the status panel
    ///   G702-OSC-Tester.exe --firstN 50     show only first N messages
    ///                                        detailed, then just status
    /// </summary>
    internal class Program
    {
        private static readonly object _logLock = new object();
        private static bool _statusActive;

        private static void Write(string level, string msg)
        {
            lock (_logLock)
            {
                if (_statusActive)
                {
                    Console.Write("\r" + new string(' ', 118) + "\r");
                    _statusActive = false;
                }
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + msg);
            }
        }

        private static int Main(string[] args)
        {
            int port = 7001;
            bool quiet = false;
            int firstN = int.MaxValue;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                { port = p; i++; }
                else if (args[i] == "--quiet") quiet = true;
                else if (args[i] == "--firstN" && i + 1 < args.Length && int.TryParse(args[i + 1], out int n))
                { firstN = n; i++; }
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("G702 OSC Tester - standalone UDP OSC listener");
                    Console.WriteLine();
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  G702-OSC-Tester.exe [--port N] [--quiet] [--firstN N]");
                    Console.WriteLine();
                    Console.WriteLine("  --port N     Listen on UDP port N (default 7001)");
                    Console.WriteLine("  --quiet      Don't print per-message lines, only the status panel");
                    Console.WriteLine("  --firstN N   Print only the first N messages in detail, then go quiet");
                    Console.WriteLine("  --help       Show this help");
                    PauseIfInteractive();
                    return 0;
                }
            }

            Write("INFO", "G702 OSC Tester starting. Binding UDP *:" + port + "...");

            UdpClient udp;
            try { udp = new UdpClient(port); }
            catch (Exception ex)
            {
                Write("ERROR", "Bind failed: " + ex.GetType().Name + ": " + ex.Message);
                Write("INFO",  "Port " + port + " is probably already held by another process.");
                Write("INFO",  "Common culprits: Protokol already listening, an earlier tester/bridge still running.");
                DumpHoldersOfPort(port);
                PauseIfInteractive();
                return 2;
            }

            Write("INFO", "Listening. Press Ctrl+C to stop and show final tally.");

            bool stop = false;
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop = true;
                try { udp.Close(); } catch { }
            };

            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            long received = 0;
            long bytesReceived = 0;

            // Per-address tally: (count, latestTypetags, latestArgs, lastSeen)
            var tally = new Dictionary<string, AddressStats>(StringComparer.Ordinal);

            // For rate calculation
            long receivedAtLastTick = 0;
            DateTime lastTick = DateTime.UtcNow;
            double lastRate = 0;

            var statusThread = new Thread(() =>
            {
                while (!stop)
                {
                    Thread.Sleep(500);
                    PrintStatus(received, bytesReceived, tally.Count, ref receivedAtLastTick, ref lastTick, ref lastRate, port);
                }
            })
            { IsBackground = true, Name = "status" };
            statusThread.Start();

            try
            {
                while (!stop)
                {
                    byte[] data;
                    try { data = udp.Receive(ref endpoint); }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { continue; }

                    received++;
                    bytesReceived += data.Length;

                    string address, typetags, preview;
                    ParseOscMessage(data, out address, out typetags, out preview);

                    AddressStats st;
                    if (!tally.TryGetValue(address, out st))
                    {
                        st = new AddressStats();
                        tally[address] = st;
                    }
                    st.Count++;
                    st.LastTypetags = typetags;
                    st.LastPreview  = preview;

                    if (!quiet && received <= firstN)
                    {
                        Write("RECV", string.Format("#{0,-6} {1}:{2} {3,4}B {4} {5} {6}",
                            received, endpoint.Address, endpoint.Port, data.Length,
                            address, typetags, preview));
                    }
                }
            }
            finally
            {
                try { udp.Close(); } catch { }

                Thread.Sleep(300); // let the status thread fall through
                if (_statusActive) { Console.WriteLine(); _statusActive = false; }

                Write("INFO", "Stopped. Totals: " + received + " packets, " + bytesReceived + " bytes.");
                Write("INFO", "Per-address tally (" + tally.Count + " unique addresses):");
                var keys = new List<string>(tally.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys)
                {
                    var st = tally[k];
                    Write("INFO", string.Format("  {0,-32} x{1,-10:N0}  {2} {3}",
                        k, st.Count, st.LastTypetags, st.LastPreview));
                }
            }

            PauseIfInteractive();
            return 0;
        }

        // When the exe is double-clicked from File Explorer, Windows spawns a
        // fresh console window that closes the instant Main returns. That's
        // miserable for a diagnostic tool since any error flashes by. Detect
        // that case (no console input redirection + no debugger attached) and
        // hold the window open until the user hits a key.
        private static void PauseIfInteractive()
        {
            try
            {
                if (Console.IsInputRedirected) return;    // running under a pipe, don't block
                Console.WriteLine();
                Console.WriteLine("Press any key to close this window...");
                Console.ReadKey(true);
            }
            catch { /* headless, no console, etc. — just exit */ }
        }

        // Best-effort: on Windows, ask PowerShell who owns the UDP port we
        // failed to bind. Saves the user a second troubleshooting step.
        private static void DumpHoldersOfPort(int port)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \""
                              + "Get-NetUDPEndpoint -LocalPort " + port + " -ErrorAction SilentlyContinue | "
                              + "ForEach-Object { $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; "
                              + "'  holder: PID=' + $_.OwningProcess + ' Name=' + $p.ProcessName + ' LocalAddress=' + $_.LocalAddress }\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        foreach (var line in output.Split('\n'))
                            if (!string.IsNullOrWhiteSpace(line))
                                Write("INFO", line.TrimEnd('\r'));
                    }
                }
            }
            catch { /* PowerShell not available or permission denied — just skip */ }
        }

        private class AddressStats
        {
            public long Count;
            public string LastTypetags = "";
            public string LastPreview  = "";
        }

        private static void PrintStatus(long received, long bytesReceived, int uniqueAddresses,
                                        ref long receivedAtLastTick, ref DateTime lastTick,
                                        ref double lastRate, int port)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - lastTick).TotalSeconds;
            if (elapsed > 0)
            {
                lastRate = (received - receivedAtLastTick) / elapsed;
                receivedAtLastTick = received;
                lastTick = now;
            }

            string s = string.Format(
                "LISTENING :{0,-5} | packets {1,10:N0} | {2,7:N0} msg/s | {3,7:N1} kB | addresses {4,4}",
                port, received, lastRate, bytesReceived / 1024.0, uniqueAddresses);
            lock (_logLock)
            {
                Console.Write("\r" + s.PadRight(118));
                _statusActive = true;
            }
        }

        // Minimal OSC parser for /address ,typetags <args...>. Handles the arg
        // types that actually matter for debugging (f, i, s) + gracefully bails
        // out on anything more exotic so we still see the address & tags.
        private static void ParseOscMessage(byte[] data, out string address, out string typetags, out string preview)
        {
            address = "(malformed)";
            typetags = "";
            preview = "";

            if (data == null || data.Length < 4) { address = "(empty)"; return; }

            int i = 0;
            while (i < data.Length && data[i] != 0) i++;
            address = Encoding.ASCII.GetString(data, 0, i);
            i = (i + 4) & ~3;
            if (i >= data.Length) return;

            int tagStart = i;
            while (i < data.Length && data[i] != 0) i++;
            typetags = i > tagStart ? Encoding.ASCII.GetString(data, tagStart, i - tagStart) : "";
            i = (i + 4) & ~3;

            var sb = new StringBuilder();
            for (int t = 1; t < typetags.Length; t++)
            {
                if (i >= data.Length) break;
                char tag = typetags[t];
                if (sb.Length > 0) sb.Append(' ');
                if (tag == 'f' && i + 4 <= data.Length)
                {
                    byte[] buf = { data[i + 3], data[i + 2], data[i + 1], data[i] };
                    float f = BitConverter.ToSingle(buf, 0);
                    sb.Append(f.ToString("F4"));
                    i += 4;
                }
                else if (tag == 'i' && i + 4 <= data.Length)
                {
                    int v = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                    sb.Append(v);
                    i += 4;
                }
                else if (tag == 's')
                {
                    int s0 = i;
                    while (i < data.Length && data[i] != 0) i++;
                    sb.Append('"').Append(Encoding.ASCII.GetString(data, s0, i - s0)).Append('"');
                    i = (i + 4) & ~3;
                }
                else if (tag == 'T') { sb.Append("True"); }
                else if (tag == 'F') { sb.Append("False"); }
                else if (tag == 'N') { sb.Append("Nil"); }
                else
                {
                    sb.Append('<').Append(tag).Append('>');
                    break;
                }
            }
            preview = sb.ToString();
        }
    }
}
