using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SharpLSL;

namespace G702_Trigno_LSL
{
    /// <summary>
    /// Trigno SDK -> LSL bridge. Same Trigno command/data protocol as the OSC
    /// version; the outputs are LSL stream outlets instead of OSC packets.
    ///
    /// Exposes two LSL streams (discoverable by Lab Recorder / pylsl / Unity LSL):
    ///   - name="Trigno-EMG", type="EMG", 16 channels, 1111.111 Hz, float32
    ///     (one primary sample per TCU slot, from Trigno port 50043)
    ///   - name="Trigno-AUX", type="IMU", 144 channels, 148.148 Hz, float32
    ///     (9 aux channels per slot: accX accY accZ gyroX gyroY gyroZ magX magY magZ,
    ///      from Trigno port 50044)
    ///
    /// A side-effect of multiplexing 16 sensors * 9 channels into one AUX stream
    /// is that channel 0..8 = slot 1, 9..17 = slot 2, etc. See the XML metadata
    /// attached to the StreamInfo for per-channel labels.
    /// </summary>
    internal class Program
    {
        enum SensorTypes { SensorTrigno, SensorTrignoImu, SensorTrignoMiniHead, NoSensor };

        private List<SensorTypes> _sensors = new List<SensorTypes>();
        private Dictionary<string, SensorTypes> sensorList = new Dictionary<string, SensorTypes>
        {
            { "Trigno Standard", SensorTypes.SensorTrigno },
            { "Trigno IM",       SensorTypes.SensorTrignoImu },
            { "Trigno MiniHead", SensorTypes.SensorTrignoMiniHead },
        };

        // Trigno SDK port layout (MAN-025-3-5 §6.1).
        private const int commandPort   = 50040;
        private const int legacyEmgPort = 50041;
        private const int legacyAuxPort = 50042;
        private const int emgPort       = 50043;   // primary EMG, 16 ch/frame
        private const int auxPort       = 50044;   // AUX, 144 ch/frame

        private TcpClient commandSocket, emgSocket, accSocket, imuEmgSocket, imuAuxSocket;
        private NetworkStream commandStream, emgStream, accStream, imuEmgStream, imuAuxStream;
        private StreamReader commandReader;
        private StreamWriter commandWriter;

        private Thread emgThread, accThread, imuEmgThread, imuAuxThread;

        private bool connected;
        private volatile bool running;

        // Trigno defaults (from the manual's §6.1.2 table, UPSAMPLE OFF,
        // BACKWARDS COMPATIBILITY ON — what the server reports on this rig).
        private const double FRAME_INTERVAL = 0.0135;       // seconds per frame
        private const double EMG_RATE_HZ    = 1111.111;
        private const double AUX_RATE_HZ    = 148.148;
        private const int    N_SENSORS      = 16;

        // LSL streams.
        private StreamInfo   emgInfo, auxInfo;
        private StreamOutlet emgOut, auxOut;

        // Reusable per-frame buffers — avoids allocation on every push.
        private readonly float[] emgFrame = new float[N_SENSORS];
        private readonly float[] auxFrame = new float[N_SENSORS * 9];

        // Sample counters for the status panel.
        private long emgSamples, auxSamples, legacyEmgSamples, legacyAuxSamples;

        private string trignoServer = "127.0.0.1";
        private string serverBanner = "(unknown)";
        private double frameInterval = FRAME_INTERVAL;
        private int    maxSamplesEmg, maxSamplesAux;
        private string backwardsCompat = "?", upsamplingState = "?";

        // ---- logging ---------------------------------------------------------

        private static readonly object _logLock = new object();
        private static bool _statusActive;

        private static void Log(string level, string msg)
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

        private static void LogEx(string where, Exception ex) =>
            Log("ERROR", where + ": " + ex.GetType().Name + ": " + ex.Message
                + Environment.NewLine + ex.StackTrace);

        private void PrintStatus()
        {
            string s = string.Format(
                "STREAMING | EMG {0,8:N0} samples | AUX {1,8:N0} samples | legacy EMG {2,8:N0} | legacy AUX {3,8:N0}",
                Interlocked.Read(ref emgSamples),
                Interlocked.Read(ref auxSamples),
                Interlocked.Read(ref legacyEmgSamples),
                Interlocked.Read(ref legacyAuxSamples));
            lock (_logLock)
            {
                Console.Write("\r" + s.PadRight(118));
                _statusActive = true;
            }
        }

        // ---- main ------------------------------------------------------------

        private static int Main(string[] args)
        {
            var app = new Program();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Log("INFO", "Ctrl+C received, shutting down...");
                app.running = false;
            };

            Log("INFO", "G702 Trigno -> LSL bridge starting.");
            Log("INFO", "LSL version: " + LSL.GetLibraryVersion() + "  protocol: " + LSL.GetProtocolVersion());

            try
            {
                app.Connect();
                if (!app.connected) { Log("ERROR", "Aborting: command channel never came up."); return 2; }

                app.PrintSummary();
                app.CreateLslStreams();

                app.Start();
                if (!app.running) { Log("ERROR", "Aborting: streaming failed to start."); app.Quit(); return 3; }

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
                try { app.Quit(); } catch (Exception ex) { LogEx("quit", ex); }
                try { app.emgOut?.Dispose(); app.auxOut?.Dispose(); } catch { }
                try { app.emgInfo?.Dispose(); app.auxInfo?.Dispose(); } catch { }
                Log("INFO", string.Format("Final samples: EMG={0:N0} AUX={1:N0} (plus legacy EMG={2:N0} legacy AUX={3:N0})",
                    app.emgSamples, app.auxSamples, app.legacyEmgSamples, app.legacyAuxSamples));
            }

            return 0;
        }

        // ---- LSL wiring ------------------------------------------------------

        private void CreateLslStreams()
        {
            string hostname = Environment.MachineName;

            // EMG stream: 16 channels @ 1111.111 Hz, float32.
            emgInfo = new StreamInfo(
                name:          "Trigno-EMG",
                type:          "EMG",
                channelCount:  N_SENSORS,
                nominalSrate:  EMG_RATE_HZ,
                channelFormat: ChannelFormat.Float,
                sourceId:      "G702-Trigno-EMG@" + hostname);
            AttachEmgChannelMetadata(emgInfo);
            emgOut = new StreamOutlet(emgInfo, chunkSize: 0, maxBuffered: 360, transportOptions: TransportOptions.Default);

            // AUX stream: 144 channels (9/sensor) @ 148.148 Hz.
            auxInfo = new StreamInfo(
                name:          "Trigno-AUX",
                type:          "IMU",
                channelCount:  N_SENSORS * 9,
                nominalSrate:  AUX_RATE_HZ,
                channelFormat: ChannelFormat.Float,
                sourceId:      "G702-Trigno-AUX@" + hostname);
            AttachAuxChannelMetadata(auxInfo);
            auxOut = new StreamOutlet(auxInfo, chunkSize: 0, maxBuffered: 360, transportOptions: TransportOptions.Default);

            Log("INFO", "LSL outlets created. Discoverable as:");
            Log("INFO", "  Trigno-EMG (type=EMG, 16ch, " + EMG_RATE_HZ.ToString("F3") + " Hz)");
            Log("INFO", "  Trigno-AUX (type=IMU, " + (N_SENSORS * 9) + "ch, " + AUX_RATE_HZ.ToString("F3") + " Hz)");
        }

        private static void AttachEmgChannelMetadata(StreamInfo info)
        {
            try
            {
                var channels = info.Description.AppendChild("channels");
                for (int i = 1; i <= N_SENSORS; i++)
                {
                    var ch = channels.AppendChild("channel");
                    ch.AppendChildValue("label", "sensor" + i + "_emg");
                    ch.AppendChildValue("type",  "EMG");
                    ch.AppendChildValue("unit",  "V");
                }
                info.Description.AppendChildValue("manufacturer", "Delsys");
            }
            catch (Exception ex) { LogEx("EMG metadata", ex); }
        }

        private static void AttachAuxChannelMetadata(StreamInfo info)
        {
            try
            {
                var channels = info.Description.AppendChild("channels");
                string[] kinds = { "accX", "accY", "accZ", "gyroX", "gyroY", "gyroZ", "magX", "magY", "magZ" };
                string[] units = { "g",    "g",    "g",    "deg/s", "deg/s", "deg/s", "uT",   "uT",   "uT" };
                string[] types = { "ACC",  "ACC",  "ACC",  "GYRO",  "GYRO",  "GYRO",  "MAG",  "MAG",  "MAG" };
                for (int s = 1; s <= N_SENSORS; s++)
                {
                    for (int k = 0; k < 9; k++)
                    {
                        var ch = channels.AppendChild("channel");
                        ch.AppendChildValue("label", "sensor" + s + "_" + kinds[k]);
                        ch.AppendChildValue("type",  types[k]);
                        ch.AppendChildValue("unit",  units[k]);
                    }
                }
                info.Description.AppendChildValue("manufacturer", "Delsys");
            }
            catch (Exception ex) { LogEx("AUX metadata", ex); }
        }

        // ---- Trigno connect + discovery --------------------------------------
        // Same protocol as the main OSC version — see the other Program.cs for
        // long-form comments. This is the minimal copy to stay self-contained.

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
            catch (Exception ex) { LogEx("Connect", ex); connected = false; return; }

            // Drain the connect-time banner (Trigno emits it before any command).
            try
            {
                string banner = commandReader.ReadLine();
                commandReader.ReadLine();
                serverBanner = banner ?? "(no banner)";
                Log("INFO", "Server banner: " + serverBanner);
            }
            catch (Exception ex) { LogEx("Connect/banner", ex); connected = false; return; }

            // Config queries (cheap, single-line answers).
            string fi  = SendCommand("FRAME INTERVAL?");
            string mse = SendCommand("MAX SAMPLES EMG?");
            string msa = SendCommand("MAX SAMPLES AUX?");
            backwardsCompat = SendCommand("BACKWARDS COMPATIBILITY?");
            upsamplingState = SendCommand("UPSAMPLING?");
            double.TryParse(fi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out frameInterval);
            int.TryParse(mse, out maxSamplesEmg);
            int.TryParse(msa, out maxSamplesAux);
            if (frameInterval <= 0) frameInterval = FRAME_INTERVAL;

            SendCommand("UPSAMPLE OFF");

            // Enumerate sensors.
            _sensors = new List<SensorTypes>();
            for (int i = 1; i <= N_SENSORS; i++)
            {
                string paired = SendCommand("SENSOR " + i + " PAIRED?");
                if (!connected) return;

                bool isPaired = paired != null && paired.Trim().ToUpperInvariant().StartsWith("YES");
                if (!isPaired) { _sensors.Add(SensorTypes.NoSensor); continue; }

                string typeResp = SendCommand("SENSOR " + i + " TYPE?");
                string modeResp = SendCommand("SENSOR " + i + " MODE?");
                Log("INFO", "Slot " + i + " paired: TYPE='" + typeResp + "' MODE='" + modeResp + "'");

                SensorTypes type;
                if (sensorList.TryGetValue((typeResp ?? "").Trim(), out type))
                    _sensors.Add(type);
                else
                {
                    Log("WARN", "Slot " + i + " type '" + typeResp + "' not in sensorList — classifying as SensorTrigno.");
                    _sensors.Add(SensorTypes.SensorTrigno);
                }
            }
        }

        private string SendCommand(string command)
        {
            if (!connected) { Log("WARN", "SendCommand skipped (not connected): " + command); return ""; }
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
                    if (line.Length == 0) break;
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(line);
                }
                string response = sb.ToString();
                Log("DEBUG", "<< " + response);
                return response;
            }
            catch (Exception ex) { LogEx("SendCommand('" + command + "')", ex); connected = false; return ""; }
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

            Log("INFO", "=== Trigno -> LSL summary ===");
            Log("INFO", " Server version : " + serverBanner);
            Log("INFO", string.Format(" Frame interval : {0} s   Backwards-compat: {1}   Upsampling: {2}",
                frameInterval, backwardsCompat, upsamplingState));
            Log("INFO", string.Format(" EMG port 50043 : {0} samples/frame  ->  {1:F2} Hz", maxSamplesEmg, emgHz));
            Log("INFO", string.Format(" AUX port 50044 : {0} samples/frame  ->  {1:F2} Hz", maxSamplesAux, auxHz));
            Log("INFO", string.Format(" Sensors found  : Trigno={0}  TrignoIM={1}  MiniHead={2}  empty={3}",
                trigno, imu, mini, empty));
            for (int i = 0; i < _sensors.Count; i++)
                if (_sensors[i] != SensorTypes.NoSensor)
                    Log("INFO", "   slot " + (i + 1) + ": " + _sensors[i]);
            Log("INFO", "=============================");
        }

        // ---- start streaming -------------------------------------------------

        private void Start()
        {
            if (!connected) { Log("ERROR", "Not connected, can't start streaming."); return; }

            try
            {
                Log("INFO", "Opening data channels " + legacyEmgPort + ".." + auxPort + "...");
                emgSocket    = new TcpClient(trignoServer, legacyEmgPort);
                accSocket    = new TcpClient(trignoServer, legacyAuxPort);
                imuEmgSocket = new TcpClient(trignoServer, emgPort);
                imuAuxSocket = new TcpClient(trignoServer, auxPort);

                emgStream    = emgSocket.GetStream();
                accStream    = accSocket.GetStream();
                imuEmgStream = imuEmgSocket.GetStream();
                imuAuxStream = imuAuxSocket.GetStream();
            }
            catch (Exception ex) { LogEx("Start/open-data-sockets", ex); running = false; return; }

            emgThread    = new Thread(LegacyEmgWorker)  { IsBackground = true, Name = "legacy-emg" };
            accThread    = new Thread(LegacyAuxWorker)  { IsBackground = true, Name = "legacy-aux" };
            imuEmgThread = new Thread(EmgLslWorker)     { IsBackground = true, Name = "emg"        };
            imuAuxThread = new Thread(AuxLslWorker)     { IsBackground = true, Name = "aux"        };

            running = true;
            emgThread.Start();
            accThread.Start();
            imuEmgThread.Start();
            imuAuxThread.Start();

            string response = SendCommand("START");
            if (response == null || !response.StartsWith("OK"))
            {
                Log("ERROR", "Trigno refused START. Response was: '" + (response ?? "<null>") + "'");
                running = false;
                return;
            }
            Log("INFO", "Trigno accepted START. Streaming live.");
        }

        private void Quit()
        {
            if (!connected && emgSocket == null && commandSocket == null) return;

            if (connected)
            {
                try { SendCommand("QUIT"); } catch (Exception ex) { LogEx("Quit/QUIT", ex); }
            }
            connected = false;
            running   = false;
            Thread.Sleep(50);

            if (commandReader != null) try { commandReader.Close(); } catch { }
            if (commandWriter != null) try { commandWriter.Close(); } catch { }
            if (commandStream != null) try { commandStream.Close(); } catch { }
            if (commandSocket != null) try { commandSocket.Close(); } catch { }
            if (emgStream    != null) try { emgStream.Close();    } catch { }
            if (emgSocket    != null) try { emgSocket.Close();    } catch { }
            if (accStream    != null) try { accStream.Close();    } catch { }
            if (accSocket    != null) try { accSocket.Close();    } catch { }
            if (imuEmgStream != null) try { imuEmgStream.Close(); } catch { }
            if (imuEmgSocket != null) try { imuEmgSocket.Close(); } catch { }
            if (imuAuxStream != null) try { imuAuxStream.Close(); } catch { }
            if (imuAuxSocket != null) try { imuAuxSocket.Close(); } catch { }

            Log("INFO", "Shutdown complete.");
        }

        // ---- workers ---------------------------------------------------------

        // Legacy EMG port (50041) — 16 channels/frame, not pushed to LSL (the
        // modern EMG port on 50043 already covers all sensor types). We still
        // drain the socket so the server doesn't block; sample count shows in
        // the status panel for diagnostics.
        private void LegacyEmgWorker()
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
                            for (int sn = 0; sn < N_SENSORS; ++sn) reader.ReadSingle();
                            Interlocked.Increment(ref legacyEmgSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("LegacyEmgWorker", ex); }
            finally { Log("INFO", "LegacyEmgWorker stopped."); }
        }

        // Legacy AUX port (50042) — 48 channels/frame (3 aux × 16 slots), not
        // pushed to LSL (modern AUX on 50044 has the full 9 aux per slot).
        private void LegacyAuxWorker()
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
                            for (int sn = 0; sn < N_SENSORS; ++sn)
                            {
                                reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
                            }
                            Interlocked.Increment(ref legacyAuxSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("LegacyAuxWorker", ex); }
            finally { Log("INFO", "LegacyAuxWorker stopped."); }
        }

        // EMG port (50043) -> LSL "Trigno-EMG" stream.
        // One frame = one multichannel LSL sample (16 floats).
        private void EmgLslWorker()
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
                            for (int sn = 0; sn < N_SENSORS; ++sn)
                                emgFrame[sn] = reader.ReadSingle();

                            try { emgOut?.PushSample(emgFrame); }
                            catch (Exception ex) { LogEx("LSL EMG push", ex); }

                            Interlocked.Increment(ref emgSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("EmgLslWorker", ex); }
            finally { Log("INFO", "EmgLslWorker stopped."); }
        }

        // AUX port (50044) -> LSL "Trigno-AUX" stream.
        // One frame = one multichannel LSL sample (144 floats). Channel layout:
        // [sensor 0: accX accY accZ gyroX gyroY gyroZ magX magY magZ]
        // [sensor 1: ...same 9 in order...] ... [sensor 15: ...]
        // See AttachAuxChannelMetadata for the labels attached to the stream.
        private void AuxLslWorker()
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
                            for (int sn = 0; sn < N_SENSORS; ++sn)
                            {
                                int baseIdx = sn * 9;
                                for (int c = 0; c < 9; c++)
                                    auxFrame[baseIdx + c] = reader.ReadSingle();
                            }

                            try { auxOut?.PushSample(auxFrame); }
                            catch (Exception ex) { LogEx("LSL AUX push", ex); }

                            Interlocked.Increment(ref auxSamples);
                        }
                        catch (IOException) { }
                    }
                }
            }
            catch (Exception ex) { if (running) LogEx("AuxLslWorker", ex); }
            finally { Log("INFO", "AuxLslWorker stopped."); }
        }
    }
}
