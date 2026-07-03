using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

public class UnityMqttCommandClient : MonoBehaviour
{
    [Header("MQTT Broker Settings")]
    public string brokerAddress = "100.110.157.78";
    public int brokerPort = 1883;
    public string clientIdPrefix = "unity_digital_twin_";
    public bool connectOnStart = true;

    [Header("MQTT Authentication")]
    public bool useCredentials = false;
    public string mqttUsername = "";
    public string mqttPassword = "";

    [Header("MQTT Topics - ESP32 Topic v2")]
    public string topicDtPump = "cps/greenhouse/brassica_01/cmd/direct/pump";
    public string topicDtLight = "cps/greenhouse/brassica_01/cmd/direct/light";
    public string topicPlantingStart = "cps/greenhouse/brassica_01/cmd/config/planting_start";

    [Header("Command Settings")]
    public string source = "unity";
    public int pumpOnDurationSeconds = 10;
    public int lightOnDurationSeconds = 300;

    [Header("Local Unity Effects")]
    public GameObject waterEffect;
    public GameObject growLightSource;
    public PlantDigitalTwinUI digitalTwinUI;
    public PlantGrowthSimulator plantGrowthSimulator;
    public UnityInfluxDashboardReader dashboardReader;

    private IMqttClient mqttClient;
    private MqttClientOptions mqttOptions;
    private string clientId;
    private bool isConnecting = false;
    private bool isDisconnecting = false;

    private async void Start()
    {
        clientId = clientIdPrefix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (connectOnStart)
        {
            await ConnectToBroker();
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

            if (string.IsNullOrWhiteSpace(clientId))
            {
                clientId = clientIdPrefix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            MqttFactory factory = new MqttFactory();
            mqttClient = factory.CreateMqttClient();

            mqttClient.ConnectedAsync += args =>
            {
                Debug.Log("[MQTT] Connected to broker: " + brokerAddress + ":" + brokerPort);
                return Task.CompletedTask;
            };

            mqttClient.DisconnectedAsync += args =>
            {
                if (!isDisconnecting)
                {
                    Debug.LogWarning("[MQTT] Disconnected from broker.");
                }

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

            if (!mqttClient.IsConnected)
            {
                Debug.LogWarning("[MQTT] Connect finished but client is not connected.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT] Connect failed: " + ex.Message);
        }
        finally
        {
            isConnecting = false;
        }
    }

    public async void SendPumpOn()
    {
        Debug.Log("[UNITY → MQTT] Pump ON");

        if (waterEffect != null)
        {
            waterEffect.SetActive(true);
        }

        if (digitalTwinUI != null)
        {
            digitalTwinUI.SetPumpStatusLocal("ON");
        }

        UnityInfluxDashboardReader reader = GetDashboardReader();
        if (reader != null)
        {
            reader.SetPumpStatusLocal(true);
        }

        await PublishActuatorCommand(
            topicDtPump,
            "pump",
            "ON",
            pumpOnDurationSeconds,
            "unity_pump_on"
        );
    }

    public async void SendPumpOff()
    {
        Debug.Log("[UNITY → MQTT] Pump OFF");

        if (waterEffect != null)
        {
            waterEffect.SetActive(false);
        }

        if (digitalTwinUI != null)
        {
            digitalTwinUI.SetPumpStatusLocal("OFF");
        }

        UnityInfluxDashboardReader reader = GetDashboardReader();
        if (reader != null)
        {
            reader.SetPumpStatusLocal(false);
        }

        await PublishActuatorCommand(
            topicDtPump,
            "pump",
            "OFF",
            0,
            "unity_pump_off"
        );
    }

    public async void SendLightOn()
    {
        Debug.Log("[UNITY → MQTT] Light ON");

        if (growLightSource != null)
        {
            growLightSource.SetActive(true);
        }

        UnityInfluxDashboardReader reader = GetDashboardReader();
        if (reader != null)
        {
            reader.SetLightStatusLocal(true);
        }

        await PublishActuatorCommand(
            topicDtLight,
            "light",
            "ON",
            lightOnDurationSeconds,
            "unity_light_on"
        );
    }

    public async void SendLightOff()
    {
        Debug.Log("[UNITY → MQTT] Light OFF");

        if (growLightSource != null)
        {
            growLightSource.SetActive(false);
        }

        UnityInfluxDashboardReader reader = GetDashboardReader();
        if (reader != null)
        {
            reader.SetLightStatusLocal(false);
        }

        await PublishActuatorCommand(
            topicDtLight,
            "light",
            "OFF",
            0,
            "unity_light_off"
        );
    }

    public async void SendStartNewCrop()
    {
        Debug.Log("[UNITY → MQTT] START new season");

        DateTimeOffset plantingStartUtc = DateTimeOffset.UtcNow;

        if (plantGrowthSimulator != null)
        {
            plantGrowthSimulator.StartNewSeasonFromStartButton(plantingStartUtc);
        }
        else
        {
            Debug.LogWarning("[START] PlantGrowthSimulator is missing.");
        }

        UnityInfluxDashboardReader reader = GetDashboardReader();
        if (reader != null)
        {
            reader.SetPlantingStartLocal(plantingStartUtc);
        }

        await PublishPlantingStartCommand(plantingStartUtc);
    }

    private async Task PublishActuatorCommand(string topic, string target, string state, int durationSeconds, string commandReason)
    {
        string commandId = CreateCommandId();

        string json =
            "{"
            + "\"id\":\"" + EscapeJson(commandId) + "\","
            + "\"command_id\":\"" + EscapeJson(commandId) + "\","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"mode\":\"DIRECT\","
            + "\"target\":\"" + EscapeJson(target) + "\","
            + "\"state\":\"" + EscapeJson(state) + "\","
            + "\"duration_s\":" + durationSeconds + ","
            + "\"reason\":\"" + EscapeJson(commandReason) + "\","
            + "\"sent_at\":\"" + EscapeJson(DateTime.UtcNow.ToString("o")) + "\""
            + "}";

        await Publish(topic, json, false);
    }

    private async Task PublishPlantingStartCommand(DateTimeOffset plantingStartUtc)
    {
        string commandId = CreateCommandId();

        long plantingStartEpoch = plantingStartUtc.ToUnixTimeSeconds();
        string plantingStartLocal = plantingStartUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz");

        string json =
            "{"
            + "\"id\":\"" + EscapeJson(commandId) + "\","
            + "\"command_id\":\"" + EscapeJson(commandId) + "\","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"target\":\"planting_start\","
            + "\"action\":\"SET_NOW\","
            + "\"planting_start_epoch\":" + plantingStartEpoch + ","
            + "\"planting_start_time\":\"" + EscapeJson(plantingStartLocal) + "\","
            + "\"reason\":\"unity_start_new_season\","
            + "\"sent_at\":\"" + EscapeJson(DateTime.UtcNow.ToString("o")) + "\""
            + "}";

        await Publish(topicPlantingStart, json, true);
    }

    private async Task Publish(string topic, string payload, bool retain)
    {
        try
        {
            if (mqttClient == null || !mqttClient.IsConnected)
            {
                await ConnectToBroker();
            }

            if (mqttClient == null || !mqttClient.IsConnected)
            {
                Debug.LogError("[MQTT] Publish failed because broker is not connected.");
                return;
            }

            MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build();

            await mqttClient.PublishAsync(message, CancellationToken.None);

            Debug.Log("[MQTT] Published topic=" + topic + " retain=" + retain + " payload=" + payload);
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT] Publish error: " + ex.Message);
        }
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
            return dashboardReader;
        }

        return null;
    }

    private string CreateCommandId()
    {
        return "unity-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "");
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

        try
        {
            isDisconnecting = true;

            if (mqttClient != null && mqttClient.IsConnected)
            {
                await mqttClient.DisconnectAsync();
                Debug.Log("[MQTT] Disconnected.");
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