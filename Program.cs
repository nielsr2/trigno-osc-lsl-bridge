using System;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Threading;
using CoreOSC;
using CoreOSC.Types;
using System.Net;

namespace G702_Trigno_Console
{
    internal class Program
    {
        enum SensorTypes { SensorTrigno, SensorTrignoImu, SensorTrignoMiniHead, NoSensor };

        private List<SensorTypes> _sensors = new List<SensorTypes>();

        // Map from the raw string returned by `SENSOR n TYPE?` to our enum.
        // Add Delsys model strings here as they are observed on real hardware;
        // unknown responses fall through to NoSensor (see Connect()).
        private Dictionary<string, SensorTypes> sensorList = new Dictionary<string, SensorTypes>
        {
            { "Trigno Standard", SensorTypes.SensorTrigno },
            { "Trigno IM", SensorTypes.SensorTrignoImu },
            { "Trigno MiniHead", SensorTypes.SensorTrignoMiniHead },
        };

        private TcpClient commandSocket;
        private TcpClient emgSocket;
        private TcpClient accSocket;
        private TcpClient imuEmgSocket;
        private TcpClient imuAuxSocket;

        // Trigno SDK port layout (per MAN-025-3-5 §6.1):
        //   50040 Command port       — request/reply ASCII protocol
        //   50041 Legacy EMG Data    — 16 channels/frame  (EMG + primary non-EMG, 4-ch sensors only)
        //   50042 Legacy AUX Data    — 48 channels/frame  (3 aux/sensor, 4-ch sensors only)
        //   50043 EMG Data           — 16 channels/frame  (EMG + primary non-EMG, ALL sensor types)
        //   50044 AUX Data           — 144 channels/frame (9 aux/sensor, ALL sensor types)
        // OSC output is emitted from the modern (50043 / 50044) ports only; the
        // legacy ports are read into buffers for later CSV export but not OSC'd.
        private const int commandPort       = 50040;
        private const int legacyEmgPort     = 50041;
        private const int legacyAuxPort     = 50042;
        private const int emgPort           = 50043;
        private const int auxPort           = 50044;

        private NetworkStream commandStream;
        private NetworkStream emgStream;
        private NetworkStream accStream;
        private NetworkStream imuEmgStream;
        private NetworkStream imuAuxStream;
        private StreamReader commandReader;
        private StreamWriter commandWriter;

        private bool connected = false;
        private bool running   = false;

        private const string COMMAND_QUIT           = "QUIT";
        private const string COMMAND_GETTRIGGERS    = "TRIGGER?";
        private const string COMMAND_SETSTARTTRIGGER = "TRIGGER START";
        private const string COMMAND_SETSTOPTRIGGER  = "TRIGGER STOP";
        private const string COMMAND_START          = "START";
        private const string COMMAND_STOP           = "STOP";
        private const string COMMAND_SENSOR_TYPE    = "TYPE?";

        private Thread emgThread;
        private Thread accThread;
        private Thread imuEmgThread;
        private Thread imuAuxThread;

        // CSV header builders — built but not yet written anywhere. Next iteration.
        private StringBuilder csvStandardSensors = new StringBuilder();
        private StringBuilder csvIMSensors       = new StringBuilder();

        private const string OSC_HOST = "127.0.0.1";
        private const int    OSC_PORT = 7001;
        // CoreOSC is encode-only. We own the UdpClient: Connect() sets the
        // *remote* target (local port auto-ephemeral) — no more colliding
        // with port 7001 the way Rug.Osc's OscSender(IPAddress, int) did.
        private static UdpClient oscUdp;
        private static readonly OscMessageConverter _oscEncoder = new OscMessageConverter();
        private static long _oscSent;

        // Use the literal IPv4 loopback, NOT "localhost". On Windows, "localhost"
        // resolves to ::1 first and each TcpClient spends ~2s failing IPv6
        // before it retries on 127.0.0.1 — adds up to 10s of startup stall.
        private string trignoServer = "127.0.0.1";
        private string serverBanner = "(unknown)";

        // Server config snapshot captured in Connect(), shown in the summary.
        private double frameInterval        = 0.0135;   // seconds, spec default
        private int    maxSamplesEmg        = 0;
        private int    maxSamplesAux        = 0;
        private string backwardsCompat      = "?";
        private string upsamplingState      = "?";

        // Sample counters for the status line (updated via Interlocked).
        private long emgSamples;
        private long accSamples;
        private long imuEmgSamples;
        private long imuAuxSamples;

        // ---- logging ---------------------------------------------------------

        private static readonly object _logLock = new object();

        // Status-line state: when a log line is printed, it should visually
        // overwrite the current status line (if any) rather than interleave.
        private static bool _statusActive;

        private static void Log(string level, string msg)
        {
            lock (_logLock)
            {
                if (_statusActive)
                {
                    Console.Write("\r" + new string(' ', 110) + "\r");
                    _statusActive = false;
                }
                Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + msg);
            }
        }

        private static void LogEx(string where, Exception ex)
        {
            Log("ERROR", where + ": " + ex.GetType().Name + ": " + ex.Message
                + Environment.NewLine + ex.StackTrace);
        }

        private void PrintStatus()
        {
            string s = string.Format(
                "STREAMING | EMG {0,8:N0} | AUX {1,8:N0} | OSC sent {2,10:N0} | OSC errs {3,5:N0}",
                Interlocked.Read(ref imuEmgSamples),
                Interlocked.Read(ref imuAuxSamples),
                Interlocked.Read(ref _oscSent),
                Interlocked.Read(ref _oscErrors));
            lock (_logLock)
            {
                Console.Write("\r" + s.PadRight(108));
                _statusActive = true;
            }
        }

        // ---- entry point -----------------------------------------------------

        private static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--listen")
                {
                    int port = OSC_PORT;
                    for (int j = 0; j < args.Length - 1; j++)
                        if (args[j] == "--port" && int.TryParse(args[j + 1], out int p)) port = p;
                    RunOscListener(port);
                    return;
                }
                if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  G702-Trigno-Console.exe                  Run the Trigno->OSC bridge (default).");
                    Console.WriteLine("  G702-Trigno-Console.exe --listen         Dump OSC messages received on UDP 7001.");
                    Console.WriteLine("  G702-Trigno-Console.exe --listen --port N  Listen on a specific port.");
                    return;
                }
            }

            var app = new Program();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Log("INFO", "Ctrl+C received, shutting down...");
                app.running = false;
            };

            Log("INFO", "G702 Trigno -> OSC bridge starting (OSC target " + OSC_HOST + ":" + OSC_PORT + ")");

            try
            {
                try
                {
                    oscUdp = new UdpClient();
                    oscUdp.Connect(OSC_HOST, OSC_PORT);
                    Log("INFO", "OSC UDP sender local=" + oscUdp.Client.LocalEndPoint
                              + " -> " + OSC_HOST + ":" + OSC_PORT);
                }
                catch (Exception ex) { LogEx("OSC connect", ex); }

                app.Connect();
                if (!app.connected)
                {
                    Log("ERROR", "Aborting: command channel never came up.");
                    return;
                }

                app.PrintSummary();

                app.start();
                if (!app.running)
                {
                    Log("ERROR", "Aborting: streaming failed to start.");
                    return;
                }

                Log("INFO", "Streaming. Press Ctrl+C to stop.");
                while (app.running)
                {
                    Thread.Sleep(500);
                    app.PrintStatus();
                }
            }
            catch (Exception ex)
            {
                LogEx("main", ex);
            }
            finally
            {
                if (_statusActive) { Console.WriteLine(); _statusActive = false; }
                try { app.quit(); } catch (Exception ex) { LogEx("quit", ex); }
                Log("INFO", string.Format("Final samples: EMG={0:N0} ACC={1:N0} IMU-EMG={2:N0} IMU-AUX={3:N0}",
                    app.emgSamples, app.accSamples, app.imuEmgSamples, app.imuAuxSamples));
            }
        }

        private void PrintSummary()
        {
            int trigno = 0, imu = 0, mini = 0, empty = 0;
            for (int i = 0; i < _sensors.Count; i++)
            {
                switch (_sensors[i])
                {
                    case SensorTypes.SensorTrigno:         trigno++; break;
                    case SensorTypes.SensorTrignoImu:      imu++;    break;
                    case SensorTypes.SensorTrignoMiniHead: mini++;   break;
                    default:                               empty++;  break;
                }
            }

            double emgHz = maxSamplesEmg > 0 ? maxSamplesEmg / frameInterval : 0;
            double auxHz = maxSamplesAux > 0 ? maxSamplesAux / frameInterval : 0;

            Log("INFO", "=== Trigno -> OSC summary ===");
            Log("INFO", " Server version : " + serverBanner);
            Log("INFO", string.Format(" Frame interval : {0} s   Backwards-compat: {1}   Upsampling: {2}",
                frameInterval, backwardsCompat, upsamplingState));
            Log("INFO", string.Format(" EMG port 50043 : {0} samples/frame  ->  {1:F2} Hz", maxSamplesEmg, emgHz));
            Log("INFO", string.Format(" AUX port 50044 : {0} samples/frame  ->  {1:F2} Hz", maxSamplesAux, auxHz));
            Log("INFO", string.Format(" Sensors found  : Trigno={0}  TrignoIM={1}  MiniHead={2}  empty={3}",
                trigno, imu, mini, empty));
            for (int i = 0; i < _sensors.Count; i++)
            {
                if (_sensors[i] != SensorTypes.NoSensor)
                    Log("INFO", "   slot " + (i + 1) + ": " + _sensors[i]);
            }
            Log("INFO", " OSC target     : " + OSC_HOST + ":" + OSC_PORT);
            Log("INFO", "   /trigno/{N}/emg                         (from EMG port, 16/frame)");
            Log("INFO", "   /trigno/{N}/acc/{x,y,z}                 (from AUX port, aux[0..2])");
            Log("INFO", "   /trigno/{N}/gyro/{x,y,z}                (from AUX port, aux[3..5])");
            Log("INFO", "   /trigno/{N}/mag/{x,y,z}                 (from AUX port, aux[6..8])");
            Log("INFO", "   where N = sensor slot 1..16");
            Log("INFO", "=============================");
        }

        // ---- OSC -------------------------------------------------------------

        private static long _oscErrors;

        // Build + send a single-float OSC message. Using CoreOSC for encoding
        // only (avoids its async Task allocation on the hot path — at ~39k
        // msgs/sec a Task per send would be all GC pressure).
        private static void OsendFloat(string address, float value)
        {
            if (oscUdp == null) return;
            try
            {
                var msg = new OscMessage(new Address(address), new object[] { value });
                var dwords = _oscEncoder.Serialize(msg);
                var bytes = new List<byte>(64);
                foreach (var d in dwords) bytes.AddRange(d.Bytes);
                byte[] buf = bytes.ToArray();
                // UdpClient.Send isn't documented as reentrant; two workers
                // (ImuEmgWorker + ImuAuxWorker) push concurrently, so serialize
                // the actual syscall. The encode step above stays outside the
                // lock — it's per-call data.
                lock (oscUdp)
                {
                    oscUdp.Send(buf, buf.Length);
                }
                Interlocked.Increment(ref _oscSent);
            }
            catch (Exception ex)
            {
                // Log only the 1st error and every 1000th thereafter.
                long n = Interlocked.Increment(ref _oscErrors);
                if (n == 1 || n % 1000 == 0) LogEx("OSC send (#" + n + ")", ex);
            }
        }

        // ---- connect / discovery --------------------------------------------

        private void Connect()
        {
            try
            {
                Log("INFO", "Connecting to Trigno command channel at " + trignoServer + ":" + commandPort + "...");
                commandSocket = new TcpClient(trignoServer, commandPort);
                commandStream = commandSocket.GetStream();
                commandReader = new StreamReader(commandStream, Encoding.ASCII);
                commandWriter = new StreamWriter(commandStream, Encoding.ASCII);
                connected = true;
                Log("INFO", "Command channel open.");
            }
            catch (Exception ex)
            {
                LogEx("Connect", ex);
                connected = false;
                return;
            }

            // Drain the server's connect-time banner. It's a greeting line + blank
            // terminator that arrives BEFORE we send anything; if we don't drain it,
            // every subsequent ReadLine is off by one (the real bug this replaces).
            try
            {
                string banner = commandReader.ReadLine();
                commandReader.ReadLine(); // blank terminator
                serverBanner = banner ?? "(no banner)";
                Log("INFO", "Server banner: " + serverBanner);
            }
            catch (Exception ex)
            {
                LogEx("Connect/banner", ex);
                connected = false;
                return;
            }

            // Query the server's current timing/config so we can report actual
            // expected rates rather than the bogus top-level "RATE?" (which isn't
            // a valid command and returns "CANNOT COMPLETE"). Per SDK §6.3 the
            // real queries are FRAME INTERVAL?, MAX SAMPLES EMG?, MAX SAMPLES AUX?,
            // BACKWARDS COMPATIBILITY?, UPSAMPLING?.
            string fi  = SendCommand("FRAME INTERVAL?");
            string mse = SendCommand("MAX SAMPLES EMG?");
            string msa = SendCommand("MAX SAMPLES AUX?");
            backwardsCompat = SendCommand("BACKWARDS COMPATIBILITY?");
            upsamplingState = SendCommand("UPSAMPLING?");
            double.TryParse(fi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out frameInterval);
            int.TryParse(mse, out maxSamplesEmg);
            int.TryParse(msa, out maxSamplesAux);
            if (frameInterval <= 0) frameInterval = 0.0135;

            // Apply UPSAMPLE OFF to keep the standard 1111.111 Hz EMG / 148.148 Hz
            // AUX rates documented in the manual's §6.1.2 default table.
            SendCommand("UPSAMPLE OFF");

            // Discover which of the 16 slots have paired sensors. We query
            // PAIRED? first (cheap, always single-line) and only chase TYPE?
            // / MODE? if the slot is actually populated.
            _sensors = new List<SensorTypes>();
            for (int i = 1; i <= 16; i++)
            {
                string paired = SendCommand("SENSOR " + i + " PAIRED?");
                if (!connected) return;

                bool isPaired = paired != null && paired.Trim().ToUpperInvariant().StartsWith("YES");
                if (!isPaired)
                {
                    _sensors.Add(SensorTypes.NoSensor);
                    continue;
                }

                // Paired → fetch type + mode for logging.
                string typeResp = SendCommand("SENSOR " + i + " " + COMMAND_SENSOR_TYPE);
                string modeResp = SendCommand("SENSOR " + i + " MODE?");
                Log("INFO", "Slot " + i + " paired: TYPE='" + typeResp + "' MODE='" + modeResp + "'");

                SensorTypes type;
                string key = (typeResp ?? "").Trim();
                if (sensorList.TryGetValue(key, out type))
                    _sensors.Add(type);
                else
                {
                    Log("WARN", "Slot " + i + " type '" + typeResp + "' not in sensorList — "
                        + "classifying as SensorTrigno for now. Add the exact string to sensorList "
                        + "if this sensor is actually IMU/MiniHead.");
                    _sensors.Add(SensorTypes.SensorTrigno);
                }
            }
        }

        // ---- command I/O -----------------------------------------------------

        // Send a command and read back the full response. The Trigno protocol
        // frames responses as one-or-more content lines terminated by a single
        // empty line, so we keep reading until we hit that empty line. This
        // matters for commands where the server replies with more than one
        // line (e.g. paired-sensor metadata) — reading only the first line
        // would leak the rest into the next SendCommand call.
        private string SendCommand(string command)
        {
            if (!connected)
            {
                Log("WARN", "SendCommand skipped (not connected): " + command);
                return "";
            }

            try
            {
                Log("DEBUG", ">> " + command);
                commandWriter.WriteLine(command);
                commandWriter.WriteLine();
                commandWriter.Flush();

                var sb = new StringBuilder();
                string line;
                while (true)
                {
                    line = commandReader.ReadLine();
                    if (line == null)
                    {
                        Log("ERROR", "Server closed command channel while waiting for '" + command + "'");
                        connected = false;
                        return sb.ToString();
                    }
                    if (line.Length == 0) break;          // empty line = end of response
                    if (sb.Length > 0) sb.Append(" | "); // join multi-line as "a | b | c"
                    sb.Append(line);
                }
                string response = sb.ToString();
                Log("DEBUG", "<< " + response);
                return response;
            }
            catch (Exception ex)
            {
                LogEx("SendCommand('" + command + "')", ex);
                connected = false;
                return "";
            }
        }

        // ---- streaming -------------------------------------------------------

        private void start()
        {
            if (!connected)
            {
                Log("ERROR", "Not connected, can't start streaming.");
                return;
            }

            // Build CSV headers (not yet written anywhere — placeholder for a
            // future CSV-output pass).
            csvStandardSensors = new StringBuilder();
            csvIMSensors       = new StringBuilder();
            for (int i = 0; i < _sensors.Count; i++)
            {
                SensorTypes sensor = _sensors[i];
                switch (sensor)
                {
                    case SensorTypes.SensorTrignoImu:
                        csvIMSensors.Append(sensor + "EMG,");
                        csvIMSensors.Append(sensor + "ACCX,");
                        csvIMSensors.Append(sensor + "ACCY,");
                        csvIMSensors.Append(sensor + "ACCZ,");
                        csvIMSensors.Append(sensor + "GYROX,");
                        csvIMSensors.Append(sensor + "GYROY,");
                        csvIMSensors.Append(sensor + "GYROZ,");
                        csvIMSensors.Append(sensor + "MAGX,");
                        csvIMSensors.Append(sensor + "MAGY,");
                        csvIMSensors.Append(sensor + "MAGZ,");
                        break;
                    case SensorTypes.NoSensor:
                        break;
                    default:
                        csvStandardSensors.Append(sensor + "EMG,");
                        csvStandardSensors.Append(sensor + "ACCX,");
                        csvStandardSensors.Append(sensor + "ACCY,");
                        csvStandardSensors.Append(sensor + "ACCZ,");
                        break;
                }
            }
            if (csvStandardSensors.Length > 0) csvStandardSensors.Length -= 1;
            if (csvIMSensors.Length > 0)       csvIMSensors.Length       -= 1;
            csvStandardSensors.AppendLine();
            csvIMSensors.AppendLine();

            // Open the four data channels. One try wrapping all four so a mid-way
            // failure doesn't leave partial sockets around — quit() will close
            // whatever opened.
            try
            {
                Log("INFO", "Opening data channels " + legacyEmgPort + ".." + auxPort + "...");
                // Variable-name legend: emgSocket/accSocket read the LEGACY ports;
                // imuEmgSocket/imuAuxSocket read the MODERN ports (used for OSC).
                emgSocket    = new TcpClient(trignoServer, legacyEmgPort);
                accSocket    = new TcpClient(trignoServer, legacyAuxPort);
                imuEmgSocket = new TcpClient(trignoServer, emgPort);
                imuAuxSocket = new TcpClient(trignoServer, auxPort);

                emgStream    = emgSocket.GetStream();
                accStream    = accSocket.GetStream();
                imuEmgStream = imuEmgSocket.GetStream();
                imuAuxStream = imuAuxSocket.GetStream();
            }
            catch (Exception ex)
            {
                LogEx("start/open-data-sockets", ex);
                running = false;
                return;
            }

            emgThread    = new Thread(EmgWorker)    { IsBackground = true, Name = "emg" };
            accThread    = new Thread(AccWorker)    { IsBackground = true, Name = "acc" };
            imuEmgThread = new Thread(ImuEmgWorker) { IsBackground = true, Name = "imu-emg" };
            imuAuxThread = new Thread(ImuAuxWorker) { IsBackground = true, Name = "imu-aux" };

            running = true;
            emgThread.Start();
            accThread.Start();
            imuEmgThread.Start();
            imuAuxThread.Start();

            string response = SendCommand(COMMAND_START);

            if (response == null || !response.StartsWith("OK"))
            {
                Log("ERROR", "Trigno refused START. Response was: '" + (response ?? "<null>") + "'");
                running = false;
                return;
            }
            Log("INFO", "Trigno accepted START. Streaming live.");
        }

        // ---- shutdown --------------------------------------------------------

        private void quit()
        {
            // Idempotent: if nothing's open, nothing to do.
            if (!connected && emgSocket == null && commandSocket == null) return;

            if (connected)
            {
                try { SendCommand(COMMAND_QUIT); }
                catch (Exception ex) { LogEx("quit/QUIT", ex); }
            }
            connected = false;

            // Give workers a moment to notice `running = false` and exit their
            // read loops before we yank the streams out from under them.
            running = false;
            Thread.Sleep(50);

            if (commandReader != null) try { commandReader.Close(); } catch { }
            if (commandWriter != null) try { commandWriter.Close(); } catch { }
            if (commandStream != null) try { commandStream.Close(); } catch { }
            if (commandSocket != null) try { commandSocket.Close(); } catch { }
            if (emgStream != null)    try { emgStream.Close();    } catch { }
            if (emgSocket != null)    try { emgSocket.Close();    } catch { }
            if (accStream != null)    try { accStream.Close();    } catch { }
            if (accSocket != null)    try { accSocket.Close();    } catch { }
            if (imuEmgStream != null) try { imuEmgStream.Close(); } catch { }
            if (imuEmgSocket != null) try { imuEmgSocket.Close(); } catch { }
            if (imuAuxStream != null) try { imuAuxStream.Close(); } catch { }
            if (imuAuxSocket != null) try { imuAuxSocket.Close(); } catch { }
            if (oscUdp != null)       try { oscUdp.Close();       } catch { }

            Log("INFO", "Shutdown complete.");
        }

        // ---- workers ---------------------------------------------------------

        private void EmgWorker()
        {
            try
            {
                emgStream.ReadTimeout = 1000;
                using (var reader = new BinaryReader(emgStream))
                {
                    while (running)
                    {
                        try
                        {
                            // Legacy EMG port (50041) — drain the frame to keep
                            // the TCP buffer from stalling. Not emitted over OSC
                            // since the modern EMG port (50043) already covers
                            // all sensor types.
                            for (int sn = 0; sn < 16; ++sn) reader.ReadSingle();
                            Interlocked.Increment(ref emgSamples);
                        }
                        catch (IOException)
                        {
                            // Read-timeout while waiting for data — expected when
                            // the server hasn't started streaming yet or is idle.
                        }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("EmgWorker", ex); }
            finally { Log("INFO", "EmgWorker stopped."); }
        }

        private void AccWorker()
        {
            try
            {
                accStream.ReadTimeout = 1000;
                using (var reader = new BinaryReader(accStream))
                {
                    while (running)
                    {
                        try
                        {
                            // Legacy AUX port (50042) — drain the 3-per-slot
                            // aux frame. Not OSC-emitted; modern AUX (50044)
                            // carries the full 9-per-slot data.
                            for (int sn = 0; sn < 16; ++sn)
                            {
                                reader.ReadSingle();
                                reader.ReadSingle();
                                reader.ReadSingle();
                            }
                            Interlocked.Increment(ref accSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("AccWorker", ex); }
            finally { Log("INFO", "AccWorker stopped."); }
        }

        // Reads the EMG Data port (50043) — 16 channels per frame, one primary
        // (EMG or equivalent) sample per sensor slot. Emits OSC /trigno/N/emg.
        private void ImuEmgWorker()
        {
            try
            {
                imuEmgStream.ReadTimeout = 1000;
                using (var reader = new BinaryReader(imuEmgStream))
                {
                    while (running)
                    {
                        try
                        {
                            for (int sn = 0; sn < 16; ++sn)
                            {
                                float val = reader.ReadSingle();
                                // Skip OSC for unpaired slots — they'd just emit
                                // zeros. Still read the 4 bytes off the wire to
                                // stay in frame.
                                if (_sensors != null && sn < _sensors.Count && _sensors[sn] != SensorTypes.NoSensor)
                                    OsendFloat("/trigno/" + (sn + 1) + "/emg", val);
                            }
                            Interlocked.Increment(ref imuEmgSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("ImuEmgWorker", ex); }
            finally { Log("INFO", "ImuEmgWorker stopped."); }
        }

        // Reads the AUX Data port (50044) — 144 channels per frame, grouped as
        // 9 aux channels per sensor slot. For Trigno IM sensors the channels are:
        // [0]accX [1]accY [2]accZ [3]gyroX [4]gyroY [5]gyroZ [6]magX [7]magY [8]magZ
        // (per SDK §6.1.1 example). For non-IMU sensor types the 9 slots may mean
        // something different, but the OSC topology stays consistent.
        private void ImuAuxWorker()
        {
            try
            {
                imuAuxStream.ReadTimeout = 1000;
                using (var reader = new BinaryReader(imuAuxStream))
                {
                    while (running)
                    {
                        try
                        {
                            for (int sn = 0; sn < 16; ++sn)
                            {
                                // Always read the 9 aux floats off the wire so
                                // we stay framed — even for empty slots.
                                float ax = reader.ReadSingle();
                                float ay = reader.ReadSingle();
                                float az = reader.ReadSingle();
                                float gx = reader.ReadSingle();
                                float gy = reader.ReadSingle();
                                float gz = reader.ReadSingle();
                                float mx = reader.ReadSingle();
                                float my = reader.ReadSingle();
                                float mz = reader.ReadSingle();

                                // Skip OSC emission for unpaired slots.
                                if (_sensors == null || sn >= _sensors.Count || _sensors[sn] == SensorTypes.NoSensor)
                                    continue;

                                string slot = "/trigno/" + (sn + 1);
                                OsendFloat(slot + "/acc/x",  ax);
                                OsendFloat(slot + "/acc/y",  ay);
                                OsendFloat(slot + "/acc/z",  az);
                                OsendFloat(slot + "/gyro/x", gx);
                                OsendFloat(slot + "/gyro/y", gy);
                                OsendFloat(slot + "/gyro/z", gz);
                                OsendFloat(slot + "/mag/x",  mx);
                                OsendFloat(slot + "/mag/y",  my);
                                OsendFloat(slot + "/mag/z",  mz);
                            }
                            Interlocked.Increment(ref imuAuxSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("ImuAuxWorker", ex); }
            finally { Log("INFO", "ImuAuxWorker stopped."); }
        }

        // ---- OSC listener (diagnostic mode) ---------------------------------

        // Raw UDP listener that parses OSC packets by hand — no Rug.Osc / SharpOSC
        // dependency on this side. Lets us verify what's actually on the wire,
        // independently of whichever OSC library we use for sending.
        private static void RunOscListener(int port)
        {
            Log("INFO", "OSC listener starting on UDP 0.0.0.0:" + port + ". Press Ctrl+C to stop.");

            UdpClient udp;
            try
            {
                udp = new UdpClient(port);
            }
            catch (Exception ex)
            {
                LogEx("OSC listener bind (" + port + ")", ex);
                return;
            }

            bool stop = false;
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop = true;
                try { udp.Close(); } catch { }
            };

            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            long received = 0;
            var addressCounts = new Dictionary<string, long>();

            try
            {
                while (!stop)
                {
                    byte[] data;
                    try
                    {
                        data = udp.Receive(ref endpoint);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { continue; }

                    received++;
                    string parsed = ParseOscMessage(data);
                    string address = parsed.Split(' ')[0];

                    long c;
                    addressCounts.TryGetValue(address, out c);
                    addressCounts[address] = c + 1;

                    if (received <= 20 || received % 500 == 0)
                        Log("RECV", "#" + received + " " + endpoint + " (" + data.Length + "B) " + parsed);
                }
            }
            finally
            {
                try { udp.Close(); } catch { }
                Log("INFO", "Listener stopped. " + received + " packets received total.");
                Log("INFO", "Per-address tally:");
                var keys = new List<string>(addressCounts.Keys);
                keys.Sort();
                foreach (var k in keys)
                    Log("INFO", "  " + k + "  x " + addressCounts[k]);
            }
        }

        // Minimal OSC message parser. Returns "address typetags arg0 arg1 ...".
        // Handles the arg types we actually send (f, i) + safely truncates on
        // anything weirder. OSC uses big-endian binary, padded to 4-byte blocks.
        private static string ParseOscMessage(byte[] data)
        {
            if (data == null || data.Length < 4) return "(empty)";

            int i = 0;
            int addrStart = 0;
            while (i < data.Length && data[i] != 0) i++;
            string address = Encoding.ASCII.GetString(data, addrStart, i - addrStart);
            i = (i + 4) & ~3;

            if (i >= data.Length) return address;

            int tagStart = i;
            while (i < data.Length && data[i] != 0) i++;
            string tags = i > tagStart ? Encoding.ASCII.GetString(data, tagStart, i - tagStart) : "";
            i = (i + 4) & ~3;

            var sb = new StringBuilder();
            sb.Append(address).Append(' ').Append(tags);

            for (int t = 1; t < tags.Length; t++)
            {
                if (i >= data.Length) break;
                char tag = tags[t];
                if (tag == 'f' && i + 4 <= data.Length)
                {
                    byte[] buf = { data[i + 3], data[i + 2], data[i + 1], data[i] };
                    float val = BitConverter.ToSingle(buf, 0);
                    sb.Append(' ').Append(val.ToString("F4"));
                    i += 4;
                }
                else if (tag == 'i' && i + 4 <= data.Length)
                {
                    int v = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                    sb.Append(' ').Append(v);
                    i += 4;
                }
                else
                {
                    sb.Append(" <").Append(tag).Append('>');
                    break;
                }
            }
            return sb.ToString();
        }
    }
}
