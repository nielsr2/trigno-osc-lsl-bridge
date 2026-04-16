using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CoreOSC;
using CoreOSC.Types;

namespace G702_Wek_Controller
{
    /// <summary>
    /// Bidirectional bridge to Wekinator (https://doc.gold.ac.uk/~mas01rf/Wekinator/).
    /// - Sends /wek/inputs (auto-streamed float vector) and /wekinator/control/* commands
    ///   to Wekinator on UDP 6448 (Wekinator's default input port).
    /// - Listens on UDP 12000 for Wekinator's /wek/outputs and displays the vector live.
    ///
    /// Keyboard-driven TUI — you nudge input channels with arrow keys, and trigger
    /// Wekinator's train/record/run controls with letter keys. See the help text
    /// printed on startup (or press ? at any time).
    /// </summary>
    internal class Program
    {
        // ---- config ----------------------------------------------------------

        private const string DEFAULT_HOST      = "127.0.0.1";
        private const int    DEFAULT_SEND_PORT = 6448;   // Wekinator listens here
        private const int    DEFAULT_RECV_PORT = 12000;  // Wekinator sends here
        private const int    DEFAULT_INPUTS    = 3;
        private const int    DEFAULT_RATE_HZ   = 30;

        // ---- state -----------------------------------------------------------

        private static UdpClient sendUdp;
        private static UdpClient recvUdp;
        private static readonly OscMessageConverter _enc = new OscMessageConverter();

        private static float[] inputs = new float[0];
        private static int     activeChannel;
        private static float   stepSize = 0.05f;

        private static float[] lastOutputs = new float[0];
        private static DateTime lastOutputUtc = DateTime.MinValue;
        private static long oscSent, oscRecv;

        private static volatile bool running = true;
        private static readonly object _logLock = new object();
        private static bool _statusActive;

        // ---- entry -----------------------------------------------------------

        private static int Main(string[] args)
        {
            string host     = DEFAULT_HOST;
            int sendPort    = DEFAULT_SEND_PORT;
            int recvPort    = DEFAULT_RECV_PORT;
            int nInputs     = DEFAULT_INPUTS;
            int rateHz      = DEFAULT_RATE_HZ;
            bool autoStream = true;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--host":      if (i + 1 < args.Length) host = args[++i]; break;
                    case "--send-port": if (i + 1 < args.Length) int.TryParse(args[++i], out sendPort); break;
                    case "--recv-port": if (i + 1 < args.Length) int.TryParse(args[++i], out recvPort); break;
                    case "--inputs":    if (i + 1 < args.Length) int.TryParse(args[++i], out nInputs); break;
                    case "--rate":      if (i + 1 < args.Length) int.TryParse(args[++i], out rateHz); break;
                    case "--no-stream": autoStream = false; break;
                    case "-h": case "--help":
                        PrintHelp();
                        PauseIfInteractive();
                        return 0;
                }
            }

            if (nInputs < 1 || nInputs > 16) nInputs = DEFAULT_INPUTS;
            if (rateHz  < 1 || rateHz  > 500) rateHz = DEFAULT_RATE_HZ;
            inputs = new float[nInputs];

            Log("INFO", "G702 Wek Controller");
            Log("INFO", "  send /wek/inputs + control cmds -> " + host + ":" + sendPort);
            Log("INFO", "  listen /wek/outputs on :" + recvPort);
            Log("INFO", "  " + nInputs + " input channels, auto-stream " + (autoStream ? rateHz + " Hz" : "OFF"));

            // Sender
            try
            {
                sendUdp = new UdpClient();
                sendUdp.Connect(host, sendPort);
                Log("INFO", "Sender bound local=" + sendUdp.Client.LocalEndPoint);
            }
            catch (Exception ex)
            {
                Log("ERROR", "Failed to set up sender: " + ex.GetType().Name + ": " + ex.Message);
                PauseIfInteractive();
                return 2;
            }

            // Receiver
            try
            {
                recvUdp = new UdpClient(recvPort);
                Log("INFO", "Receiver bound on *:" + recvPort);
            }
            catch (Exception ex)
            {
                Log("WARN", "Receiver bind failed on :" + recvPort + " — " + ex.Message);
                Log("WARN", "Continuing in send-only mode. (Port probably held by another OSC tool.)");
                recvUdp = null;
            }

            PrintHelp();

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

            new Thread(ReceiveLoop)                  { IsBackground = true, Name = "recv"   }.Start();
            new Thread(StatusLoop)                   { IsBackground = true, Name = "status" }.Start();
            if (autoStream)
                new Thread(() => AutoSendLoop(rateHz)) { IsBackground = true, Name = "send"   }.Start();

            // Main loop. When stdin is redirected (test harness, pipe, scheduled
            // task), Console.KeyAvailable throws — skip keyboard handling in
            // that case and just run until Ctrl+C.
            if (Console.IsInputRedirected)
            {
                Log("INFO", "stdin is redirected — keyboard disabled. Ctrl+C to quit.");
                while (running) Thread.Sleep(200);
            }
            else
            {
                while (running)
                {
                    if (!Console.KeyAvailable) { Thread.Sleep(20); continue; }
                    HandleKey(Console.ReadKey(true));
                }
            }

            Log("INFO", "Shutting down...");
            try { sendUdp?.Close(); } catch { }
            try { recvUdp?.Close(); } catch { }
            Thread.Sleep(200);
            if (_statusActive) Console.WriteLine();
            Log("INFO", "Totals: sent=" + oscSent + "  recv=" + oscRecv);
            PauseIfInteractive();
            return 0;
        }

        // ---- help ------------------------------------------------------------

        private static void PrintHelp()
        {
            lock (_logLock)
            {
                if (_statusActive) { Console.Write("\r" + new string(' ', 118) + "\r"); _statusActive = false; }
                Console.WriteLine();
                Console.WriteLine("  === KEYS ===============================================================");
                Console.WriteLine("    1..9      select input channel                 (active channel = '*')");
                Console.WriteLine("    ← / →     previous / next channel");
                Console.WriteLine("    ↑ / +     increase active channel by step");
                Console.WriteLine("    ↓ / -     decrease active channel by step");
                Console.WriteLine("    [ / ]     halve / double the step size");
                Console.WriteLine("    0         zero all channels");
                Console.WriteLine("    space     send /wek/inputs immediately (outside the auto-stream tick)");
                Console.WriteLine();
                Console.WriteLine("    r / R     start / stop recording    (/wekinator/control/startRecording)");
                Console.WriteLine("    t         train                     (/wekinator/control/train)");
                Console.WriteLine("    g / G     start / stop running      (/wekinator/control/startRunning)");
                Console.WriteLine("    c         delete ALL examples       (/wekinator/control/deleteAllExamples)");
                Console.WriteLine();
                Console.WriteLine("    ?         this help");
                Console.WriteLine("    q         quit");
                Console.WriteLine("  =========================================================================");
                Console.WriteLine();
            }
        }

        // ---- keyboard --------------------------------------------------------

        private static void HandleKey(ConsoleKeyInfo k)
        {
            // Letter / char actions first.
            switch (k.KeyChar)
            {
                case '?': case 'H':                PrintHelp(); return;
                case 'q': case 'Q':                running = false; return;
                case ' ':                          SendInputs(manual: true); return;
                case '0':                          Array.Clear(inputs, 0, inputs.Length); Log("CMD", "zeroed all channels"); return;
                case '+': case '=':                Nudge(+stepSize); return;
                case '-': case '_':                Nudge(-stepSize); return;
                case '[':                          stepSize = Math.Max(0.001f, stepSize / 2f); Log("CMD", "step = " + stepSize); return;
                case ']':                          stepSize = Math.Min(1.0f,   stepSize * 2f); Log("CMD", "step = " + stepSize); return;

                case 'r':                          Cmd("/wekinator/control/startRecording"); return;
                case 'R':                          Cmd("/wekinator/control/stopRecording");  return;
                case 't': case 'T':                Cmd("/wekinator/control/train");           return;
                case 'g':                          Cmd("/wekinator/control/startRunning");    return;
                case 'G':                          Cmd("/wekinator/control/stopRunning");     return;
                case 'c': case 'C':                Cmd("/wekinator/control/deleteAllExamples"); return;
            }

            // Digit = select channel (1-indexed in the UI).
            if (k.KeyChar >= '1' && k.KeyChar <= '9')
            {
                int idx = k.KeyChar - '1';
                if (idx < inputs.Length)
                {
                    activeChannel = idx;
                    Log("CMD", "active channel = " + (idx + 1));
                }
                return;
            }

            // Arrow keys.
            switch (k.Key)
            {
                case ConsoleKey.UpArrow:    Nudge(+stepSize); break;
                case ConsoleKey.DownArrow:  Nudge(-stepSize); break;
                case ConsoleKey.LeftArrow:
                    activeChannel = Math.Max(0, activeChannel - 1);
                    Log("CMD", "active channel = " + (activeChannel + 1));
                    break;
                case ConsoleKey.RightArrow:
                    activeChannel = Math.Min(inputs.Length - 1, activeChannel + 1);
                    Log("CMD", "active channel = " + (activeChannel + 1));
                    break;
            }
        }

        private static void Nudge(float delta)
        {
            float v = inputs[activeChannel] + delta;
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            inputs[activeChannel] = v;
        }

        // ---- OSC send --------------------------------------------------------

        private static void Cmd(string address)
        {
            var msg = new OscMessage(new Address(address), new object[0]);
            SendMessage(msg);
            Log("SEND", ">> " + address);
        }

        private static void SendInputs(bool manual)
        {
            if (inputs.Length == 0) return;
            var args = new object[inputs.Length];
            for (int i = 0; i < inputs.Length; i++) args[i] = inputs[i];
            SendMessage(new OscMessage(new Address("/wek/inputs"), args));
            if (manual) Log("SEND", ">> /wek/inputs  " + FormatFloats(inputs));
        }

        private static void SendMessage(OscMessage msg)
        {
            if (sendUdp == null) return;
            try
            {
                var dwords = _enc.Serialize(msg);
                var bytes = new List<byte>(64);
                foreach (var d in dwords) bytes.AddRange(d.Bytes);
                byte[] buf = bytes.ToArray();
                sendUdp.Send(buf, buf.Length);
                Interlocked.Increment(ref oscSent);
            }
            catch (Exception ex)
            {
                Log("ERROR", "send failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void AutoSendLoop(int hz)
        {
            int intervalMs = Math.Max(1, 1000 / hz);
            while (running)
            {
                SendInputs(manual: false);
                Thread.Sleep(intervalMs);
            }
        }

        // ---- OSC receive -----------------------------------------------------

        private static void ReceiveLoop()
        {
            if (recvUdp == null) return;
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                byte[] data;
                try { data = recvUdp.Receive(ref endpoint); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }

                Interlocked.Increment(ref oscRecv);
                string address; string tags; float[] floats;
                ParseOsc(data, out address, out tags, out floats);

                if (address != null && address.StartsWith("/wek/outputs"))
                {
                    lastOutputs = floats;
                    lastOutputUtc = DateTime.UtcNow;
                }
                else if (address != null)
                {
                    // Unexpected address — log it once in a while so the user notices.
                    if (oscRecv <= 5 || oscRecv % 500 == 0)
                        Log("RECV", "unexpected " + address + " " + tags + " " + FormatFloats(floats));
                }
            }
        }

        // ---- status ----------------------------------------------------------

        private static void StatusLoop()
        {
            while (running)
            {
                Thread.Sleep(200);
                RedrawStatus();
            }
        }

        private static void RedrawStatus()
        {
            var sb = new StringBuilder();
            sb.Append("IN ");
            for (int i = 0; i < inputs.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i == activeChannel ? '*' : ' ');
                sb.Append(inputs[i].ToString("F2"));
            }
            sb.Append("  step=").Append(stepSize.ToString("F3"));
            sb.Append("  | OUT ");
            if (lastOutputs.Length == 0)
            {
                sb.Append("(waiting)");
            }
            else
            {
                sb.Append('[');
                for (int i = 0; i < lastOutputs.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(lastOutputs[i].ToString("F2"));
                }
                sb.Append(']');
                double ageSec = (DateTime.UtcNow - lastOutputUtc).TotalSeconds;
                sb.Append("  (").Append(ageSec.ToString("F1")).Append("s ago)");
            }
            sb.Append("  | tx ").Append(oscSent).Append("  rx ").Append(oscRecv);

            lock (_logLock)
            {
                Console.Write("\r" + sb.ToString().PadRight(118));
                _statusActive = true;
            }
        }

        // ---- logging ---------------------------------------------------------

        private static void Log(string level, string msg)
        {
            lock (_logLock)
            {
                if (_statusActive) { Console.Write("\r" + new string(' ', 118) + "\r"); _statusActive = false; }
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + msg);
            }
        }

        private static string FormatFloats(float[] vals)
        {
            if (vals == null || vals.Length == 0) return "[]";
            var sb = new StringBuilder(vals.Length * 6);
            sb.Append('[');
            for (int i = 0; i < vals.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(vals[i].ToString("F3"));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ---- OSC parser ------------------------------------------------------
        // Same shape as the parser in osc-tester; extracts all float args as
        // a float[] array so /wek/outputs can be displayed directly.

        private static void ParseOsc(byte[] data, out string address, out string typetags, out float[] floats)
        {
            address = null; typetags = ""; floats = new float[0];
            if (data == null || data.Length < 4) return;

            int i = 0;
            while (i < data.Length && data[i] != 0) i++;
            address = Encoding.ASCII.GetString(data, 0, i);
            i = (i + 4) & ~3;
            if (i >= data.Length) return;

            int tagStart = i;
            while (i < data.Length && data[i] != 0) i++;
            typetags = i > tagStart ? Encoding.ASCII.GetString(data, tagStart, i - tagStart) : "";
            i = (i + 4) & ~3;

            var floatList = new List<float>();
            for (int t = 1; t < typetags.Length; t++)
            {
                if (i >= data.Length) break;
                char tag = typetags[t];
                if (tag == 'f' && i + 4 <= data.Length)
                {
                    byte[] buf = { data[i + 3], data[i + 2], data[i + 1], data[i] };
                    floatList.Add(BitConverter.ToSingle(buf, 0));
                    i += 4;
                }
                else if (tag == 'i' && i + 4 <= data.Length)
                {
                    int v = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                    floatList.Add(v);
                    i += 4;
                }
                else if (tag == 's')
                {
                    int s0 = i;
                    while (i < data.Length && data[i] != 0) i++;
                    i = (i + 4) & ~3;
                }
                else break;
            }
            floats = floatList.ToArray();
        }

        // ---- pause-on-exit ---------------------------------------------------

        private static void PauseIfInteractive()
        {
            try
            {
                if (Console.IsInputRedirected) return;
                Console.WriteLine();
                Console.WriteLine("Press any key to close this window...");
                Console.ReadKey(true);
            }
            catch { }
        }
    }
}
