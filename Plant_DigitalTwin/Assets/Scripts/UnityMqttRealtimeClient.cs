using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

public class UnityMqttRealtimeClient : MonoBehaviour
{
    [Header("MQTT Broker")]
    public string brokerAddress = "100.110.157.78";
    public int brokerPort = 1883;
    public string clientIdPrefix = "unity_realtime_";
    public bool connectOnStart = true;

    [Header("MQTT Authentication")]
    public bool useCredentials = false;
    public string mqttUsername = "";
    public string mqttPassword = "";

    [Header("Subscribe Topics")]
    public string topicSensor = "cps/greenhouse/brassica_01/telemetry/sensors";
    public string topicActuator = "cps/greenhouse/brassica_01/state/actuator";
    public string topicEsp32Status = "cps/greenhouse/brassica_01/status/esp32";
    public string topicGatewayStatus = "cps/greenhouse/gateway/status";

    [Header("Dashboard Link")]
    public UnityInfluxDashboardReader dashboardReader;

    [Header("Debug")]
    public bool debugLog = true;

    private IMqttClient mqttClient;
    private MqttClientOptions mqttOptions;
    private string clientId;
    private bool isConnecting = false;
    private bool isDisconnecting = false;

    private readonly Queue<Action> mainThreadQueue = new Queue<Action>();
    private readonly object queueLock = new object();

    [Serializable]
    private class SensorPayload
    {
        public string node_id;
        public string timestamp;
        public int step;
        public int gw_step;
        public int phase;
        public float days_after_planting;

        public SensorData sensor;
        public AiData ai;
        public ControlData control;
        public StatusData status;

        public string alert;
    }

    [Serializable]
    private class SensorData
    {
        public float temperature;
        public float temp;

        public float air_humidity;
        public float humidity;
        public float hum;

        public float lux;

        public float soil_moisture;
        public float soil_moisture_avg;
        public float soil_avg;
        public float soil;

        public float s1;
        public float s2;
        public float s3;
        public float s4;
    }

    [Serializable]
    private class AiData
    {
        public int need_watering;
        public float confidence;
        public string action;
        public string reason;
    }

    [Serializable]
    private class ControlData
    {
        public PumpLightState pump;
        public PumpLightState light;
    }

    [Serializable]
    private class StatusData
    {
        public bool pump_on;
        public bool light_on;
        public string pump_state;
        public string light_state;
        public string light_mode;
        public string light_reason;
        public int wifi_rssi;
    }

    [Serializable]
    private class PumpLightState
    {
        public string state;
        public string mode;
        public string reason;
    }

    [Serializable]
    private class ActuatorPayload
    {
        public string node_id;
        public string timestamp;

        public PumpLightState pump;
        public PumpLightState light;

        public string pump_state;
        public string light_state;
    }

    async void Start()
    {
        clientId = clientIdPrefix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (dashboardReader == null && UnityInfluxDashboardReader.Instance != null)
        {
            dashboardReader = UnityInfluxDashboardReader.Instance;
        }

        if (connectOnStart)
        {
            await ConnectToBroker();
        }
    }

    void Update()
    {
        while (true)
        {
            Action action = null;

            lock (queueLock)
            {
                if (mainThreadQueue.Count > 0)
                {
                    action = mainThreadQueue.Dequeue();
                }
            }

            if (action == null)
            {
                break;
            }

            action.Invoke();
        }
    }

    public async Task ConnectToBroker()
    {
        if (isConnecting)
        {
            return;
        }

        if (mqttClient != null && mqttClient.IsConnected)
        {
            return;
        }

        try
        {
            isConnecting = true;

            MqttFactory factory = new MqttFactory();
            mqttClient = factory.CreateMqttClient();

            mqttClient.ConnectedAsync += async args =>
            {
                if (debugLog)
                {
                    Debug.Log("[MQTT REALTIME] Connected to broker: " + brokerAddress + ":" + brokerPort);
                }

                await SubscribeTopics();
            };

            mqttClient.DisconnectedAsync += args =>
            {
                if (!isDisconnecting)
                {
                    Debug.LogWarning("[MQTT REALTIME] Disconnected from broker.");
                }

                return Task.CompletedTask;
            };

            mqttClient.ApplicationMessageReceivedAsync += args =>
            {
                string topic = args.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

                HandleMqttMessage(topic, payload);

                return Task.CompletedTask;
            };

            MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(brokerAddress, brokerPort)
                .WithCleanSession();

            if (useCredentials)
            {
                builder = builder.WithCredentials(mqttUsername, mqttPassword);
            }

            mqttOptions = builder.Build();

            await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT REALTIME] Connect failed: " + ex.Message);
        }
        finally
        {
            isConnecting = false;
        }
    }

    private async Task SubscribeTopics()
    {
        if (mqttClient == null || !mqttClient.IsConnected)
        {
            return;
        }

        MqttClientSubscribeOptions options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f =>
            {
                f.WithTopic(topicSensor);
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            })
            .WithTopicFilter(f =>
            {
                f.WithTopic(topicActuator);
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            })
            .WithTopicFilter(f =>
            {
                f.WithTopic(topicEsp32Status);
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            })
            .WithTopicFilter(f =>
            {
                f.WithTopic(topicGatewayStatus);
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            })
            .Build();

        await mqttClient.SubscribeAsync(options, CancellationToken.None);

        if (debugLog)
        {
            Debug.Log("[MQTT REALTIME] Subscribed:");
            Debug.Log(" - " + topicSensor);
            Debug.Log(" - " + topicActuator);
            Debug.Log(" - " + topicEsp32Status);
            Debug.Log(" - " + topicGatewayStatus);
        }
    }

    private void HandleMqttMessage(string topic, string payload)
    {
        if (debugLog)
        {
            Debug.Log("[MQTT REALTIME] topic=" + topic + " payload=" + payload);
        }

        if (topic == topicSensor)
        {
            HandleSensorPayload(payload);
        }
        else if (topic == topicActuator)
        {
            HandleActuatorPayload(payload);
        }
        else if (topic == topicEsp32Status)
        {
            HandleEsp32StatusPayload(payload);
        }
        else if (topic == topicGatewayStatus)
        {
            HandleGatewayStatusPayload(payload);
        }
    }

    private void HandleSensorPayload(string json)
    {
        try
        {
            SensorPayload payload = JsonUtility.FromJson<SensorPayload>(json);

            if (payload == null)
            {
                return;
            }

            float temperature = ExtractTemperature(payload);
            float airHumidity = ExtractAirHumidity(payload);
            float lux = ExtractLux(payload);
            float soilMoisture = ExtractSoilMoisture(payload);
            int needWatering = ExtractNeedWatering(payload);

            string pumpStatus = ExtractPumpStatus(payload);
            string lightStatus = ExtractLightStatus(payload);
            string alert = string.IsNullOrWhiteSpace(payload.alert) ? "" : payload.alert;

            DateTimeOffset timeUtc = ExtractTimestamp(payload.timestamp);

            EnqueueMainThread(() =>
            {
                UnityInfluxDashboardReader reader = GetDashboardReader();

                if (reader != null)
                {
                    reader.ApplyMqttLiveSensor(
                        temperature,
                        airHumidity,
                        lux,
                        soilMoisture,
                        needWatering,
                        pumpStatus,
                        lightStatus,
                        alert,
                        timeUtc
                    );
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT REALTIME] Sensor parse error: " + ex.Message);
        }
    }

    private void HandleActuatorPayload(string json)
    {
        try
        {
            ActuatorPayload payload = JsonUtility.FromJson<ActuatorPayload>(json);

            if (payload == null)
            {
                return;
            }

            string pumpStatus = "";
            string lightStatus = "";

            if (payload.pump != null && !string.IsNullOrWhiteSpace(payload.pump.state))
            {
                pumpStatus = payload.pump.state;
            }
            else if (!string.IsNullOrWhiteSpace(payload.pump_state))
            {
                pumpStatus = payload.pump_state;
            }

            if (payload.light != null && !string.IsNullOrWhiteSpace(payload.light.state))
            {
                lightStatus = payload.light.state;
            }
            else if (!string.IsNullOrWhiteSpace(payload.light_state))
            {
                lightStatus = payload.light_state;
            }

            DateTimeOffset timeUtc = ExtractTimestamp(payload.timestamp);

            EnqueueMainThread(() =>
            {
                UnityInfluxDashboardReader reader = GetDashboardReader();

                if (reader != null)
                {
                    reader.ApplyMqttLiveActuator(pumpStatus, lightStatus, timeUtc);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT REALTIME] Actuator parse error: " + ex.Message);
        }
    }

    private void HandleEsp32StatusPayload(string json)
    {
        try
        {
            SensorPayload payload = JsonUtility.FromJson<SensorPayload>(json);

            if (payload == null || payload.status == null)
            {
                return;
            }

            string pumpStatus = payload.status.pump_on ? "ON" : "";
            string lightStatus = payload.status.light_on ? "ON" : "";

            if (!string.IsNullOrWhiteSpace(payload.status.pump_state))
            {
                pumpStatus = payload.status.pump_state;
            }

            if (!string.IsNullOrWhiteSpace(payload.status.light_state))
            {
                lightStatus = payload.status.light_state;
            }

            DateTimeOffset timeUtc = ExtractTimestamp(payload.timestamp);

            EnqueueMainThread(() =>
            {
                UnityInfluxDashboardReader reader = GetDashboardReader();

                if (reader != null)
                {
                    reader.ApplyMqttLiveActuator(pumpStatus, lightStatus, timeUtc);
                }
            });
        }
        catch
        {
        }
    }

    private void HandleGatewayStatusPayload(string json)
    {
        if (debugLog)
        {
            Debug.Log("[MQTT REALTIME] Gateway status received.");
        }
    }

    private float ExtractTemperature(SensorPayload payload)
    {
        if (payload.sensor == null)
        {
            return float.NaN;
        }

        if (Mathf.Abs(payload.sensor.temperature) > 0.0001f)
        {
            return payload.sensor.temperature;
        }

        return payload.sensor.temp;
    }

    private float ExtractAirHumidity(SensorPayload payload)
    {
        if (payload.sensor == null)
        {
            return float.NaN;
        }

        if (Mathf.Abs(payload.sensor.air_humidity) > 0.0001f)
        {
            return payload.sensor.air_humidity;
        }

        if (Mathf.Abs(payload.sensor.humidity) > 0.0001f)
        {
            return payload.sensor.humidity;
        }

        return payload.sensor.hum;
    }

    private float ExtractLux(SensorPayload payload)
    {
        if (payload.sensor == null)
        {
            return float.NaN;
        }

        return payload.sensor.lux;
    }

    private float ExtractSoilMoisture(SensorPayload payload)
    {
        if (payload.sensor == null)
        {
            return float.NaN;
        }

        if (Mathf.Abs(payload.sensor.soil_moisture) > 0.0001f)
        {
            return payload.sensor.soil_moisture;
        }

        if (Mathf.Abs(payload.sensor.soil_moisture_avg) > 0.0001f)
        {
            return payload.sensor.soil_moisture_avg;
        }

        if (Mathf.Abs(payload.sensor.soil_avg) > 0.0001f)
        {
            return payload.sensor.soil_avg;
        }

        if (Mathf.Abs(payload.sensor.soil) > 0.0001f)
        {
            return payload.sensor.soil;
        }

        float sum = 0f;
        int count = 0;

        if (Mathf.Abs(payload.sensor.s1) > 0.0001f)
        {
            sum += payload.sensor.s1;
            count++;
        }

        if (Mathf.Abs(payload.sensor.s2) > 0.0001f)
        {
            sum += payload.sensor.s2;
            count++;
        }

        if (Mathf.Abs(payload.sensor.s3) > 0.0001f)
        {
            sum += payload.sensor.s3;
            count++;
        }

        if (Mathf.Abs(payload.sensor.s4) > 0.0001f)
        {
            sum += payload.sensor.s4;
            count++;
        }

        if (count > 0)
        {
            return sum / count;
        }

        return 0f;
    }

    private int ExtractNeedWatering(SensorPayload payload)
    {
        if (payload.ai != null)
        {
            return payload.ai.need_watering;
        }

        return -1;
    }

    private string ExtractPumpStatus(SensorPayload payload)
    {
        if (payload.control != null && payload.control.pump != null)
        {
            return payload.control.pump.state;
        }

        if (payload.status != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.status.pump_state))
            {
                return payload.status.pump_state;
            }

            return payload.status.pump_on ? "ON" : "";
        }

        return "";
    }

    private string ExtractLightStatus(SensorPayload payload)
    {
        if (payload.control != null && payload.control.light != null)
        {
            return payload.control.light.state;
        }

        if (payload.status != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.status.light_state))
            {
                return payload.status.light_state;
            }

            return payload.status.light_on ? "ON" : "";
        }

        return "";
    }

    private DateTimeOffset ExtractTimestamp(string timestamp)
    {
        if (!string.IsNullOrWhiteSpace(timestamp))
        {
            if (DateTimeOffset.TryParse(timestamp, out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private UnityInfluxDashboardReader GetDashboardReader()
    {
        if (dashboardReader != null)
        {
            return dashboardReader;
        }

        if (UnityInfluxDashboardReader.Instance != null)
        {
            dashboardReader = UnityInfluxDashboardReader.Instance;
        }

        return dashboardReader;
    }

    private void EnqueueMainThread(Action action)
    {
        lock (queueLock)
        {
            mainThreadQueue.Enqueue(action);
        }
    }

    private async void OnApplicationQuit()
    {
        await DisconnectFromBroker();
    }

    private async void OnDestroy()
    {
        await DisconnectFromBroker();
    }

    private async Task DisconnectFromBroker()
    {
        if (isDisconnecting)
        {
            return;
        }

        isDisconnecting = true;

        try
        {
            if (mqttClient != null && mqttClient.IsConnected)
            {
                await mqttClient.DisconnectAsync();
                Debug.Log("[MQTT REALTIME] Disconnected.");
            }
        }
        catch
        {
        }
        finally
        {
            isDisconnecting = false;
        }
    }
}