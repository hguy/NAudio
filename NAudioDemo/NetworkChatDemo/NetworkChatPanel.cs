using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NAudio.Wave;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.ComponentModel.Composition;
using NAudio.Wave.Compression;
using System.Diagnostics;

namespace NAudioDemo.NetworkChatDemo
{
    public partial class NetworkChatPanel : UserControl
    {
        private WaveIn waveIn;
        private UdpClient udpSender;
        private UdpClient udpListener;
        private IWavePlayer waveOut;
        private BufferedWaveProvider waveProvider;
        private INetworkChatCodec codec;
        private volatile bool connected;

        public NetworkChatPanel(IEnumerable<INetworkChatCodec> codecs)
        {
            InitializeComponent();
            PopulateInputDevicesCombo();
            PopulateCodecsCombo(codecs);
            this.Disposed += new EventHandler(NetworkChatPanel_Disposed);
        }

        void NetworkChatPanel_Disposed(object sender, EventArgs e)
        {
            Disconnect();
        }

        private void PopulateCodecsCombo(IEnumerable<INetworkChatCodec> codecs)
        {
            var sorted = from codec in codecs 
                         where codec.IsAvailable
                         orderby codec.BitsPerSecond ascending 
                         select codec;
            
            foreach(var codec in sorted)
            {
                string bitRate = codec.BitsPerSecond == -1 ? "VBR" : String.Format("{0:0.#}kbps", codec.BitsPerSecond / 1000.0);
                string text = String.Format("{0} ({1})", codec.Name, bitRate);
                this.comboBoxCodecs.Items.Add(new CodecComboItem() { Text=text, Codec=codec });
            }
            this.comboBoxCodecs.SelectedIndex = 0;
        }

        class CodecComboItem
        {
            public string Text { get; set; }
            public INetworkChatCodec Codec { get; set; }
            public override string ToString()
            {
                return Text;
            }
        }

        private void PopulateInputDevicesCombo()
        {
            for (int n = 0; n < WaveIn.DeviceCount; n++)
            {
                var capabilities = WaveIn.GetCapabilities(n);
                this.comboBoxInputDevices.Items.Add(capabilities.ProductName);
            }
            if (comboBoxInputDevices.Items.Count > 0)
            {
                comboBoxInputDevices.SelectedIndex = 0;
            }
        }

        private void buttonStartStreaming_Click(object sender, EventArgs e)
        {
            if (!connected)
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(textBoxIPAddress.Text), int.Parse(textBoxPort.Text));
                int inputDeviceNumber = comboBoxInputDevices.SelectedIndex;
                this.codec = ((CodecComboItem)comboBoxCodecs.SelectedItem).Codec;
                Connect(endPoint, inputDeviceNumber,codec);
                buttonStartStreaming.Text = "Disconnect";
            }
            else
            {
                Disconnect();
                buttonStartStreaming.Text = "Connect";
            }
        }

        private void Connect(IPEndPoint endPoint, int inputDeviceNumber, INetworkChatCodec codec)
        {
            waveIn = new WaveIn();
            waveIn.BufferMilliseconds = 50;
            waveIn.DeviceNumber = inputDeviceNumber;
            waveIn.WaveFormat = codec.RecordFormat;
            waveIn.DataAvailable += waveIn_DataAvailable;
            waveIn.StartRecording();
            
            udpSender = new UdpClient();
            udpListener = new UdpClient();
            //endPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7080);
            // To allow us to talk to ourselves for test purposes:
            // http://stackoverflow.com/questions/687868/sending-and-receiving-udp-packets-between-two-programs-on-the-same-computer
            //udpSender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            //udpSender.Client.Bind(endPoint);
            udpListener.Client.Bind(endPoint);

            udpSender.Connect(endPoint);
            
            waveOut = new WaveOut();
            waveProvider = new BufferedWaveProvider(codec.RecordFormat);
            waveOut.Init(waveProvider);
            waveOut.Play();

            connected = true;
            ListenerThreadState state = new ListenerThreadState() { Codec = codec, EndPoint = endPoint };
            ThreadPool.QueueUserWorkItem(this.ListenerThread, state);
        }

        private void Disconnect()
        {
            if (connected)
            { 
                connected = false;
                waveIn.DataAvailable -= waveIn_DataAvailable;
                waveIn.StopRecording();
                waveOut.Stop();

                udpSender.Close();
                udpListener.Close();
                waveIn.Dispose();
                waveOut.Dispose();

                this.codec.Dispose(); // a bit naughty but we have designed the codecs to support multiple calls to Dispose, recreating their resources if Encode/Decode called again
            }
        }

        class ListenerThreadState
        {
            public IPEndPoint EndPoint { get; set; }
            public INetworkChatCodec Codec { get; set; }
        }

        private void ListenerThread(object state)
        {
            ListenerThreadState listenerThreadState = (ListenerThreadState)state;
            IPEndPoint endPoint = listenerThreadState.EndPoint;            
            try
            {
                while (connected)
                {
                    byte[] b = this.udpListener.Receive(ref endPoint);
                    byte[] decoded = listenerThreadState.Codec.Decode(b, 0, b.Length);
                    waveProvider.AddSamples(decoded, 0, decoded.Length);
                }
            }
            catch (SocketException)
            {
                // usually not a problem - just means we have disconnected
            }
        }

        void waveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            byte[] encoded = codec.Encode(e.Buffer, 0, e.BytesRecorded);
            udpSender.Send(encoded, encoded.Length);
        }
    }

    [Export(typeof(INAudioDemoPlugin))]
    public class NetworkChatPanelPlugin : INAudioDemoPlugin
    {
        public string Name
        {
            get { return "Network Chat"; }
        }

        [ImportMany(typeof(INetworkChatCodec))]
        public IEnumerable<INetworkChatCodec> Codecs { get; set; }

        public Control CreatePanel()
        {
            return new NetworkChatPanel(Codecs);
        }
    }
}
