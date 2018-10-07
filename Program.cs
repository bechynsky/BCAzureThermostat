using System;
using System.Text;
using System.IO;
using System.Threading;

using Microsoft.Azure.Devices.Client;
using Microsoft.Extensions.Configuration;

using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

using Newtonsoft.Json;

namespace BCAzureThermostat
{
    class OutlookEvent
    {
        [JsonProperty(PropertyName = "subject")]
        public string Subject { get; set; }

        [JsonProperty(PropertyName = "starttime")]
        public DateTime StartTime { get; set; }

        [JsonProperty(PropertyName = "endtime")]
        public DateTime EndTime { get; set; }

        public override string ToString()
        {
            return $"Event: {Subject} ({StartTime.ToString()}, {EndTime.ToString()})";
        }
    }
    class Program
    {
        private static double _lastTemperature = 0.0;
        private static double _lastSetTemperature = 0.0;

        private static Timer _switchHVACOff = null;

        private static MqttClient _mqttClient = new MqttClient("localhost");
        private static DeviceClient _deviceClient = null;
        private static IConfiguration _configuration { get; set; }
        
        static void Main(string[] args)
        {
            /*
                connection.json

                {
                    "connectionString": ""
                }
             */
            var builder = new ConfigurationBuilder()
                                .SetBasePath(Directory.GetCurrentDirectory())
                                .AddJsonFile("connection.json");
            _configuration = builder.Build();

            _deviceClient = DeviceClient.CreateFromConnectionString(_configuration["connectionString"]);

            _mqttClient.MqttMsgPublishReceived += Client_MqttMsgPublishReceived;

            _mqttClient.Connect("BC01");
            
            if (_mqttClient.IsConnected)
            {
                _mqttClient.Subscribe(new string[] { "#" }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE });
                Console.WriteLine("MQTT connected");
            }

            ReceiveC2dAsync();
            while (true) {}
        }

        private static async void Client_MqttMsgPublishReceived(object sender, uPLibrary.Networking.M2Mqtt.Messages.MqttMsgPublishEventArgs e)
        {
            string[] topicParts = e.Topic.Split('/');

            if (topicParts.Length != 5)
            {
                return;
            }

            string device = topicParts[1];
            string sensor = topicParts[2];
            string sensorInfo = topicParts[3];
            string measurement = topicParts[4];
            string value = System.Text.Encoding.Default.GetString(e.Message);

            string data = "";
            if (device.Equals("temperature-button:0") && 
                sensor.Equals("thermometer") && 
                measurement.Equals("temperature"))
            {
                if (sensorInfo.Equals("set-point"))
                {
                    _lastSetTemperature = double.Parse(value);
                }
                else if (sensorInfo.Equals("0:1"))
                {
                    _lastTemperature = double.Parse(value);
                } 
                data = $"{{\"temperature\":{_lastTemperature}, \"settemperature\":{_lastSetTemperature}}}";
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Temperature is {_lastTemperature} and should be {_lastSetTemperature}.");
                Console.ResetColor();
            }

            if (data.Length > 0)
            {
                // data = $"{{\"device\":\"{device}\",\"sensor\":\"{sensor}\",\"sensorInfo\":\"{sensorInfo}\",\"measurement\":\"{measurement}\",\"value\":{value}}}"; 
                Message payload = new Message(System.Text.Encoding.UTF8.GetBytes(data));
                await _deviceClient.SendEventAsync(payload);

                Console.WriteLine(data);            
            }
        }

        private static async void ReceiveC2dAsync()
        {
            Console.WriteLine("\nReceiving cloud to device messages from service");
            while (true)
            {
                Message receivedMessage = await _deviceClient.ReceiveAsync();
                if (receivedMessage == null) continue;
                
                OutlookEvent oe = JsonConvert.DeserializeObject<OutlookEvent>(Encoding.ASCII.GetString(receivedMessage.GetBytes()));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(oe);
                Console.ResetColor();

                // _mqttClient.Publish("node/kit-power-controller:0/relay/-/state/set", Encoding.ASCII.GetBytes("true"));
                string ledColor = "\"#250000\"";
                
                if (_lastSetTemperature < _lastTemperature)
                {
                    ledColor = "\"#000025\"";                    
                }
                _mqttClient.Publish("node/kit-power-controller:0/led-strip/-/color/set", Encoding.ASCII.GetBytes(ledColor));

                _switchHVACOff = new Timer(StopHVAC);
                TimeSpan switchOffTime = oe.EndTime - DateTime.Now;
                _switchHVACOff.Change(switchOffTime, new TimeSpan(0,0,0,0,-1));
                

                await _deviceClient.CompleteAsync(receivedMessage);
            }
        }

        public static void StopHVAC(object state)
        {
            _mqttClient.Publish("node/kit-power-controller:0/led-strip/-/color/set", Encoding.ASCII.GetBytes("\"#000000\""));
            _switchHVACOff.Dispose();            
        }
    }
}
