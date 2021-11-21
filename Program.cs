using System;
using System.Net.Sockets;
using System.Collections.Generic;
//using System.Diagnostics;
//using System.Windows.Forms;
using System.Text;
using System.IO;
using System.Threading;
using Rug.Osc;
using System.Net;

namespace G702_Trigno_Console
{
    internal class Program
    {
        // DECLARATIONS
        //example of creating a list of sensor types to keep track of various TCP streams...
        enum SensorTypes { SensorTrigno, SensorTrignoImu, SensorTrignoMiniHead, NoSensor };

        private List<SensorTypes> _sensors = new List<SensorTypes>();

        private Dictionary<string, SensorTypes> sensorList = new Dictionary<string, SensorTypes>();

        //The following are used for TCP/IP connections
        private TcpClient commandSocket;
        private TcpClient emgSocket;
        private TcpClient accSocket;
        private TcpClient imuEmgSocket;
        private TcpClient imuAuxSocket;


        private const int commandPort = 50040;  //server command port
        private const int emgPort = 50041;  //port for EMG data
        private const int accPort = 50042;  //port for acc data
        private const int ImuEmgDataPort = 50043;
        private const int ImuAuxDataPort = 50044;


        //The following are streams and readers/writers for communication
        private NetworkStream commandStream;
        private NetworkStream emgStream;
        private NetworkStream accStream;
        private NetworkStream imuEmgStream;
        private NetworkStream imuAuxStream;
        private StreamReader commandReader;
        private StreamWriter commandWriter;

        private List<float>[] emgDataList = new List<float>[16];
        private List<float>[] accXDataList = new List<float>[16];
        private List<float>[] accYDataList = new List<float>[16];
        private List<float>[] accZDataList = new List<float>[16];

        private List<float>[] imuEmgDataList = new List<float>[16];
        private List<float>[] imuAxDataList = new List<float>[16];
        private List<float>[] imuAyDataList = new List<float>[16];
        private List<float>[] imuAzDataList = new List<float>[16];
        private List<float>[] imuGxDataList = new List<float>[16];
        private List<float>[] imuGyDataList = new List<float>[16];
        private List<float>[] imuGzDataList = new List<float>[16];
        private List<float>[] imuMxDataList = new List<float>[16];
        private List<float>[] imuMyDataList = new List<float>[16];
        private List<float>[] imuMzDataList = new List<float>[16];

        public StringBuilder emg_data_string = new StringBuilder();

        public StringBuilder accx_data_string = new StringBuilder();
        public StringBuilder accy_data_string = new StringBuilder();
        public StringBuilder accz_data_string = new StringBuilder();

        public StringBuilder im_emg_data_string = new StringBuilder();

        public StringBuilder im_accx_data_string = new StringBuilder();
        public StringBuilder im_accy_data_string = new StringBuilder();
        public StringBuilder im_accz_data_string = new StringBuilder();

        public StringBuilder im_gyrx_data_string = new StringBuilder();
        public StringBuilder im_gyry_data_string = new StringBuilder();
        public StringBuilder im_gyrz_data_string = new StringBuilder();

        public StringBuilder im_magx_data_string = new StringBuilder();
        public StringBuilder im_magy_data_string = new StringBuilder();
        public StringBuilder im_magz_data_string = new StringBuilder();


        //The following are storage for acquired data
        private float[] emgData = new float[16];
        private float[] imuEmgData = new float[16];

        private float[] accXData = new float[16];
        private float[] accYData = new float[16];
        private float[] accZData = new float[16];

        private float[] imuAccXData = new float[16];
        private float[] imuAccYData = new float[16];
        private float[] imuAccZData = new float[16];

        private float[] gyroXData = new float[16];
        private float[] gyroYData = new float[16];
        private float[] gyroZData = new float[16];

        private float[] magXData = new float[16];
        private float[] magYData = new float[16];
        private float[] magZData = new float[16];

        private bool connected = false; //true if connected to server
        private bool running = false;   //true when acquiring data

        //Server commands
        private const string COMMAND_QUIT = "QUIT";
        private const string COMMAND_GETTRIGGERS = "TRIGGER?";
        private const string COMMAND_SETSTARTTRIGGER = "TRIGGER START";
        private const string COMMAND_SETSTOPTRIGGER = "TRIGGER STOP";
        private const string COMMAND_START = "START";
        private const string COMMAND_STOP = "STOP";
        private const string COMMAND_SENSOR_TYPE = "TYPE?";

        //Threads for acquiring emg and acc data
        private Thread emgThread;
        private Thread accThread;
        private Thread imuEmgThread;
        private Thread imuAuxThread;

        //before your loop
        StringBuilder csvStandardSensors = new StringBuilder();
        StringBuilder csvIMSensors = new StringBuilder();
        //IPAddress address = IPAddress.Parse("127.0.0.1");
        //int port = 12345;
        private static OscSender oSender = new OscSender(IPAddress.Parse("127.0.0.1"), 7001);
        string trignoServer = "localhost";

        private static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");
            //oSender = new OscSender(IPAddress.Parse("127.0.0.1"), 12345);
            Osend(new OscMessage("/test", 1));
        }

        static void Osend(OscMessage omess)
        {
            Console.WriteLine("osend..");
            try {
                oSender.Send(omess);
            }
            catch
            {
                Console.WriteLine("caought");
            }
            
        }
        //Connect button handler
        private void Connect()
        {
            try
            {
                //Establish TCP/IP connection to server using URL entered
                commandSocket = new TcpClient("localhost", commandPort);

                //Set up communication streams
                commandStream = commandSocket.GetStream();
                commandReader = new StreamReader(commandStream, Encoding.ASCII);
                commandWriter = new StreamWriter(commandStream, Encoding.ASCII);

                //Get initial response from server and display
                connected = true;   //iindicate that we are connected
            }
            catch (Exception connectException)
            {
                //connection failed, display error message
                Console.WriteLine("Could not connect.\n" + connectException.Message);
            }

            //build a list of connected sensor types
            _sensors = new List<SensorTypes>();
            for (int i = 1; i <= 16; i++)
            {
                string query = "SENSOR " + i + " " + COMMAND_SENSOR_TYPE;
                string response = SendCommand(query);
                _sensors.Add(response.Contains("INVALID") ? SensorTypes.NoSensor : sensorList[response]);
            }

            SendCommand("UPSAMPLE OFF");
            //commandLine.Text = "";
            //responseLine.Text = "";
        }


        //Quit button handler
        private void quit()
        {
            //Check if running and display error message if not
            if (running)
            {
                Console.WriteLine("Can't quit while acquiring data!");
                return;
            }

            //send QUIT command
            SendCommand(COMMAND_QUIT);

            connected = false;  //no longer connected
            //connectButton.Enabled = true;   //enable connect button
            //quitButton.Enabled = false; //disable quit button

            //Close all streams and connections
            commandReader.Close();
            commandWriter.Close();
            commandStream.Close();
            commandSocket.Close();
            emgStream.Close();
            emgSocket.Close();

            accStream.Close();
            accSocket.Close();

            imuEmgStream.Close();
            imuEmgSocket.Close();

            imuAuxStream.Close();
            imuAuxSocket.Close();
        }


        //Send a command to the server and get the response
        private string SendCommand(string command)
        {
            string response = "";

            //Check if connected
            if (connected)
            {
                //Send the command
                //commandLine.Text = command;
                Console.WriteLine(command);
                commandWriter.WriteLine(command);
                commandWriter.WriteLine();  //terminate command
                commandWriter.Flush();  //make sure command is sent immediately

                //Read the response line and display    
                response = commandReader.ReadLine();
                commandReader.ReadLine();   //get extra line terminator
                Console.WriteLine(response);
                //responseLine.Text = response;
            }
            else
                Console.WriteLine("Not connected.");
            return response;    //return the response we got
        }

        //Start button handler
        private void start()
        {
            if (!connected)
            {
                Console.WriteLine("Not connected.");
                return;
            }

            //write data to csv
            csvStandardSensors = new StringBuilder();
            csvIMSensors = new StringBuilder();

            for (int i = 0; i < _sensors.Count; i++)
            {
                //Get First Data
                SensorTypes sensor = _sensors[i];
                switch (sensor)
                {
                    case SensorTypes.SensorTrignoImu:
                        csvIMSensors.Append(sensor + "EMG" + ",");
                        csvIMSensors.Append(sensor + "ACCX" + ",");
                        csvIMSensors.Append(sensor + "ACCY" + ",");
                        csvIMSensors.Append(sensor + "ACCZ" + ",");
                        csvIMSensors.Append(sensor + "GYROX" + ",");
                        csvIMSensors.Append(sensor + "GYROY" + ",");
                        csvIMSensors.Append(sensor + "GYROZ" + ",");
                        csvIMSensors.Append(sensor + "MAGX" + ",");
                        csvIMSensors.Append(sensor + "MAGY" + ",");
                        csvIMSensors.Append(sensor + "MAGZ" + ",");
                        break;
                    case SensorTypes.NoSensor:
                        //append nothing
                        break;
                    default:
                        csvStandardSensors.Append(sensor + "EMG" + ",");
                        csvStandardSensors.Append(sensor + "ACCX" + ",");
                        csvStandardSensors.Append(sensor + "ACCY" + ",");
                        csvStandardSensors.Append(sensor + "ACCZ" + ",");
                        break;
                }

            }
            if (csvStandardSensors.Length > 1)
                csvStandardSensors.Remove(csvStandardSensors.Length - 1, 1);
            if (csvIMSensors.Length > 1)
                csvIMSensors.Remove(csvIMSensors.Length - 1, 1);
            csvStandardSensors.Append(Environment.NewLine);
            csvIMSensors.Append(Environment.NewLine);
            for (int i = 0; i < 16; i++)
            {
                imuEmgDataList[i] = new List<float>();

                imuAxDataList[i] = new List<float>();
                imuAyDataList[i] = new List<float>();
                imuAzDataList[i] = new List<float>();

                imuGxDataList[i] = new List<float>();
                imuGyDataList[i] = new List<float>();
                imuGzDataList[i] = new List<float>();

                imuMxDataList[i] = new List<float>();
                imuMyDataList[i] = new List<float>();
                imuMzDataList[i] = new List<float>();

                emgDataList[i] = new List<float>();

                accXDataList[i] = new List<float>();
                accYDataList[i] = new List<float>();
                accZDataList[i] = new List<float>();
            }
            //Clear stale data
            emgData = new float[16];
            imuEmgData = new float[16];

            accXData = new float[16];
            accYData = new float[16];
            accZData = new float[16];

            imuAccXData = new float[16];
            imuAccYData = new float[16];
            imuAccZData = new float[16];

            gyroXData = new float[16];
            gyroYData = new float[16];
            gyroZData = new float[16];

            magXData = new float[16];
            magYData = new float[16];
            magZData = new float[16];

            //Establish data connections and creat streams
            emgSocket = new TcpClient(trignoServer, emgPort);
            accSocket = new TcpClient(trignoServer, accPort);
            imuEmgSocket = new TcpClient(trignoServer, ImuEmgDataPort);
            imuAuxSocket = new TcpClient(trignoServer, ImuAuxDataPort);

            emgStream = emgSocket.GetStream();
            accStream = accSocket.GetStream();
            imuEmgStream = imuEmgSocket.GetStream();
            imuAuxStream = imuAuxSocket.GetStream();

            //Create data acquisition threads
            emgThread = new Thread(EmgWorker);
            emgThread.IsBackground = true;
            accThread = new Thread(AccWorker);
            accThread.IsBackground = true;
            imuEmgThread = new Thread(ImuEmgWorker);
            imuEmgThread.IsBackground = true;
            imuAuxThread = new Thread(ImuAuxWorker);
            imuAuxThread.IsBackground = true;

            //Indicate we are running and start up the acquisition threads
            running = true;
            emgThread.Start();
            accThread.Start();
            imuEmgThread.Start();
            imuAuxThread.Start();

            //set emg sample rate to 1926 Hz
            string a = SendCommand("RATE?");

            //Send start command to server to stream data
            string response = SendCommand(COMMAND_START);

            //check response
            if (response.StartsWith("OK"))
            {
                //Enable stop button and disable start button
                //startButton.Enabled = false;
                //stopButton.Enabled = true;

                //Start the UI update timer
                //timer1.Start();
            }
            else
                running = false;    //stop threads
        }


        /// <summary>
        /// Thread for emg data acquisition
        /// </summary>
        private void EmgWorker()
        {
            emgStream.ReadTimeout = 1000;    //set timeout

            //Create a binary reader to read the data
            BinaryReader reader = new BinaryReader(emgStream);

            while (running)
            {
                try
                {
                    //Demultiplex the data and save for UI display
                    for (int sn = 0; sn < 16; ++sn)
                    {
                        emgDataList[sn].Add(reader.ReadSingle());
                    }
                }
                catch (IOException ee)
                {
                    //Trace.WriteLine("Error in emg found");
                }
            }

            reader.Close(); //close the reader. This also disconnects
        }


        /// <summary>
        /// Thread for acc data acquisition
        /// </summary>
        private void AccWorker()
        {
            accStream.ReadTimeout = 1000;    //set timeout

            //Create a binary reader to read the data
            BinaryReader reader = new BinaryReader(accStream);

            while (running)
            {
                try
                {
                    //Demultiplex the data and save for UI display
                    for (int sn = 0; sn < 16; ++sn)
                    {
                        accXDataList[sn].Add(reader.ReadSingle());
                        accYDataList[sn].Add(reader.ReadSingle());
                        accZDataList[sn].Add(reader.ReadSingle());
                    }
                }
                catch (IOException e)
                {
                    //catch errors
                }
            }

            reader.Close(); //close the reader. This also disconnects
        }

        /// <summary>
        /// Thread for imu emg acquisition
        /// </summary>
        private void ImuEmgWorker()
        {
            imuEmgStream.ReadTimeout = 1000;    //set timeout

            BinaryReader reader = new BinaryReader(imuEmgStream);
            while (running)
            {
                try
                {
                    for (int sn = 0; sn < 16; ++sn) { 
                        float val = reader.ReadSingle();
                        imuEmgDataList[sn].Add(val);
                        Osend(new OscMessage("/" + sn, val));
                    }
                }
                catch (IOException e)
                {

                }
            }

        }

        /// <summary>
        /// Thread for imu acc/gyro/mag aquisition
        /// </summary>
        private void ImuAuxWorker()
        {
            imuAuxStream.ReadTimeout = 1000;    //set timeout

            //Create a binary reader to read the data
            BinaryReader reader = new BinaryReader(imuAuxStream);

            while (running)
            {
                try
                {
                    //Demultiplex the data and save for UI display
                    for (int sn = 0; sn < 16; ++sn)
                    {
                        imuAxDataList[sn].Add(reader.ReadSingle());
                        imuAyDataList[sn].Add(reader.ReadSingle());
                        imuAzDataList[sn].Add(reader.ReadSingle());
                        imuGxDataList[sn].Add(reader.ReadSingle());
                        imuGyDataList[sn].Add(reader.ReadSingle());
                        imuGzDataList[sn].Add(reader.ReadSingle());
                        imuMxDataList[sn].Add(reader.ReadSingle());
                        imuMyDataList[sn].Add(reader.ReadSingle());
                        imuMzDataList[sn].Add(reader.ReadSingle());
                    }
                }
                catch (IOException)
                {
                    //Trace.WriteLine("Error in acc found");
                }
            }

            reader.Close(); //close the reader. This also disconnects 
        }

    }
}