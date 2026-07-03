using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class UnityBackendRealtimeClient : MonoBehaviour
{
    [Header("Backend API")]
    public string backendHttpBaseUrl = "http://100.110.157.78:8000";
    public string backendWsUrl = "ws://100.110.157.78:8000/ws/realtime";
    public bool connectOnStart = true;
    public bool loadLatestOnStart = true;
    public float reconnectDelaySeconds = 3f;

    [Header("Dashboard Texts")]
    public TMP_Text temperatureText;
    public TMP_Text airHumidityText;
    public TMP_Text luxText;
    public TMP_Text soilMoistureText;
    public TMP_Text needWateringText;
    public TMP_Text pumpStatusText;
    public TMP_Text lightStatusText;
    public TMP_Text alertText;
    public TMP_Text startInfoText;
    public TMP_Text plantHeightText;
    public TMP_Text growthProgressText;
    public TMP_Text dataStatusText;
    public TMP_Text lastUpdateText;

    [Header("Model Visual Link")]
    public UnityActuatorVisualController actuatorVisualController;

    [Header("Plant Growth")]
    public PlantGrowthSimulator growthSimulator;
    public float maxPlantHeightCm = 12f;

    [Tooltip("Bật để PlantGrowthSimulator là script duy nhất cập nhật Plant Height Text. Tránh lỗi Plant Height bị script khác ghi sai.")]
    public bool plantGrowthSimulatorOwnsHeightText = true;

    [Header("Logistic Growth Model")]
    public float realFullCycleDays = 7f;
    public float initialBiomass = 0.02f;
    public float targetBiomassAtCycleEnd = 0.98f;
    public float maxBiomass = 1.0f;
    public float environmentGrowthFactor = 1.0f;

    [Tooltip("Hệ số hiệu chỉnh chiều cao để Unity khớp với mô hình sinh trưởng thực tế. 0.94 nghĩa là giảm nhẹ chiều cao tính toán.")]
    public float heightCalibrationFactor = 0.94f;

    [Header("Backend Duration Simulation")]
    public bool simulateDurationFromBackendCommand = true;
    public float fallbackPumpDurationSeconds = 10f;
    public float fallbackLightDurationSeconds = 300f;

    [Header("Alert Stability")]
    public float soilDryOnThreshold = 35f;
    public float soilDryOffThreshold = 40f;

    [Header("Debug")]
    public bool debugLog = true;

    private ClientWebSocket webSocket;
    private CancellationTokenSource cancellationTokenSource;

    private readonly Queue<Action> mainThreadActions = new Queue<Action>();
    private readonly object queueLock = new object();

    private string lastPumpState = "UNKNOWN";
    private string lastLightState = "UNKNOWN";

    private string lastAlertText = "Normal";
    private bool soilDryAlertActive = false;

    private bool pumpSimulationActive = false;
    private bool lightSimulationActive = false;

    private float pumpSimulationUntil = 0f;
    private float lightSimulationUntil = 0f;

    private string activePumpCommandId = "";
    private string activeLightCommandId = "";

    private float lastDisplayedDaysAfterPlanting = float.NaN;
    private long lastPlantingStartEpoch = 0;

    private DateTimeOffset lastRealtimeUtc = DateTimeOffset.MinValue;

    [Serializable]
    private class BackendEnvelope
    {
        public string type;
        public string source;
        public string timestamp;
        public string event_name;
        public string event_type;
        public string message_type;
        public string status;
        public string target;
        public string command_id;

        public RealtimePayload data;
        public RealtimePayload payload;

        public LatestBlock latest;
    }

    [Serializable]
    private class LatestBlock
    {
        public RealtimePayload sensors;
        public RealtimePayload actuator;
        public RealtimePayload status;
        public RealtimePayload command_event;
        public RealtimePayload dt_command;
        public string updated_at;
    }

    [Serializable]
    private class RealtimePayload
    {
        public string _time;
        public string node_id;
        public string timestamp;
        public string event_name;
        public string event_type;
        public string message_type;
        public string type;
        public string source;

        public float temperature;
        public float temp;

        public float air_humidity;
        public float humidity;
        public float hum;

        public float lux;

        public float soil_moisture;
        public float soil_moisture_fused;
        public float soil_moisture_mean;
        public float soil_moisture_avg;
        public float soil_avg;
        public float soil;

        public int need_watering;
        public string alert;

        public string pump_state;
        public string pump_status;
        public string light_state;
        public string light_status;

        public string id;
        public string command_id;
        public string target;
        public string state;
        public string action;
        public string status;
        public string message;
        public int duration_s;
        public int retry_count;
        public string mqtt_topic;

        public float days_after_planting;
        public long planting_start_epoch;
        public string planting_start_time;

        public int step;
        public int gw_step;
        public int phase;
        public int uptime_s;
        public int wifi_rssi;

        public SensorBlock sensor;
        public AiBlock ai;
        public StatusBlock status_data;
        public StatusBlock status_block;
        public ControlBlock control;
        public ActuatorBlock actuator;

        public PumpLightBlock pump;
        public PumpLightBlock light;

        public PlantingStartBlock planting_start;
    }

    [Serializable]
    private class SensorBlock
    {
        public float temperature;
        public float temp;

        public float air_humidity;
        public float humidity;
        public float hum;

        public float lux;

        public float soil_moisture;
        public float soil_moisture_fused;
        public float soil_moisture_mean;
        public float soil_moisture_avg;
        public float soil_avg;
        public float soil;
    }

    [Serializable]
    private class AiBlock
    {
        public int need_watering;
        public string action;
        public string reason;
        public float confidence;
        public string source;
    }

    [Serializable]
    private class StatusBlock
    {
        public string pump_state;
        public string pump_status;
        public string light_state;
        public string light_status;
        public string connection;
        public string event_name;
        public string event_type;
    }

    [Serializable]
    private class ControlBlock
    {
        public PumpLightBlock pump;
        public PumpLightBlock light;
    }

    [Serializable]
    private class ActuatorBlock
    {
        public string pump_state;
        public string pump_status;
        public string light_state;
        public string light_status;

        public PumpLightBlock pump;
        public PumpLightBlock light;
    }

    [Serializable]
    private class PumpLightBlock
    {
        public string state;
        public string status;
        public string mode;
        public string reason;
    }

    [Serializable]
    private class PlantingStartBlock
    {
        public long planting_start_epoch;
        public string planting_start_time;
        public float days_after_planting;
    }

    private void Start()
    {
        if (loadLatestOnStart)
        {
            StartCoroutine(LoadLatestFromBackend());
        }

        if (connectOnStart)
        {
            StartWebSocketClient();
        }
    }

    private void Update()
    {
        while (true)
        {
            Action action = null;

            lock (queueLock)
            {
                if (mainThreadActions.Count > 0)
                {
                    action = mainThreadActions.Dequeue();
                }
            }

            if (action == null)
            {
                break;
            }

            action.Invoke();
        }

        CheckSimulationTimeouts();
    }

    private void OnDestroy()
    {
        StopWebSocketClient();
    }

    private void OnApplicationQuit()
    {
        StopWebSocketClient();
    }

    public void StartWebSocketClient()
    {
        StopWebSocketClient();

        cancellationTokenSource = new CancellationTokenSource();
        _ = WebSocketLoop(cancellationTokenSource.Token);
    }

    public void StopWebSocketClient()
    {
        try
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }

            if (webSocket != null)
            {
                webSocket.Dispose();
                webSocket = null;
            }
        }
        catch
        {
        }
    }

    public void ReloadLatest()
    {
        StartCoroutine(LoadLatestFromBackend());
    }

    public void NotifyPlantingStartRequestedFromUnity(string epochText)
    {
        long epoch = 0;

        if (!long.TryParse(epochText, out epoch))
        {
            epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        lastPlantingStartEpoch = epoch;
        lastDisplayedDaysAfterPlanting = 0f;

        lastAlertText = "Normal";
        soilDryAlertActive = false;

        DateTimeOffset startTime = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();

        if (startInfoText != null)
        {
            startInfoText.text =
                "Thời gian gieo: "
                + startTime.ToString("yyyy-MM-dd HH:mm:ss zzz")
                + "\n"
                + "Số ngày sau gieo: 0.00 ngày";
        }

        if (!plantGrowthSimulatorOwnsHeightText)
        {
            if (plantHeightText != null)
            {
                plantHeightText.text = "Plant Height: 0.0 cm";
            }

            if (growthProgressText != null)
            {
                growthProgressText.text = "Growth Progress: 0.0 %";
            }
        }

        if (alertText != null)
        {
            alertText.text = "Alert: Normal";
        }

        if (growthSimulator != null)
        {
            growthSimulator.StartNewSeasonFromStartButton(DateTimeOffset.FromUnixTimeSeconds(epoch));
        }

        if (debugLog)
        {
            Debug.Log("[BACKEND REALTIME] Local START preview applied. Waiting for Backend/ESP32 confirmation. epoch=" + epoch);
        }
    }

    private async Task WebSocketLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                webSocket = new ClientWebSocket();

                if (debugLog)
                {
                    Debug.Log("[BACKEND WS] Connecting to " + backendWsUrl);
                }

                await webSocket.ConnectAsync(new Uri(backendWsUrl), token);

                EnqueueMainThread(() =>
                {
                    if (dataStatusText != null)
                    {
                        dataStatusText.text = "Data Status: WS CONNECTED";
                    }
                });

                if (debugLog)
                {
                    Debug.Log("[BACKEND WS] Connected.");
                }

                await ReceiveLoop(webSocket, token);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Debug.LogWarning("[BACKEND WS] Disconnected or failed: " + ex.Message);
                }
            }

            EnqueueMainThread(() =>
            {
                if (dataStatusText != null)
                {
                    dataStatusText.text = "Data Status: WS RECONNECTING";
                }
            });

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds), token);
            }
            catch
            {
            }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[65536];

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            StringBuilder messageBuilder = new StringBuilder();
            WebSocketReceiveResult result;

            do
            {
                ArraySegment<byte> segment = new ArraySegment<byte>(buffer);
                result = await socket.ReceiveAsync(segment, token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                string part = Encoding.UTF8.GetString(buffer, 0, result.Count);
                messageBuilder.Append(part);

            } while (!result.EndOfMessage);

            string json = messageBuilder.ToString();

            if (debugLog)
            {
                Debug.Log("[BACKEND WS] " + json);
            }

            EnqueueMainThread(() =>
            {
                ApplyRealtimeJson(json, "WEBSOCKET");
            });
        }
    }

    private IEnumerator LoadLatestFromBackend()
    {
        string url = backendHttpBaseUrl.TrimEnd('/') + "/api/realtime/latest";

        UnityWebRequest request = UnityWebRequest.Get(url);
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string json = request.downloadHandler.text;

            if (debugLog)
            {
                Debug.Log("[BACKEND LATEST] " + json);
            }

            ApplyRealtimeJson(json, "LATEST API");
        }
        else
        {
            Debug.LogWarning("[BACKEND LATEST] Failed: " + request.responseCode + " | " + request.error);

            if (dataStatusText != null)
            {
                dataStatusText.text = "Data Status: LATEST API FAILED";
            }
        }

        request.Dispose();
    }

    private void ApplyRealtimeJson(string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        BackendEnvelope envelope = null;

        try
        {
            envelope = JsonUtility.FromJson<BackendEnvelope>(json);
        }
        catch
        {
        }

        bool appliedSomething = false;

        if (envelope != null && envelope.latest != null)
        {
            if (envelope.latest.command_event != null)
            {
                HandleCommandEventForSimulation(envelope.latest.command_event, "LATEST");
                appliedSomething = true;
            }

            if (envelope.latest.sensors != null)
            {
                ApplyPayload(envelope.latest.sensors, source + " LATEST_SENSORS");
                appliedSomething = true;
            }

            if (envelope.latest.actuator != null)
            {
                ApplyPayload(envelope.latest.actuator, source + " LATEST_ACTUATOR");
                appliedSomething = true;
            }

            if (envelope.latest.status != null)
            {
                ApplyPayload(envelope.latest.status, source + " LATEST_STATUS");
                appliedSomething = true;
            }
        }

        if (envelope != null && envelope.data != null)
        {
            string type = envelope.type == null ? "" : envelope.type.Trim().ToLowerInvariant();

            if (type == "command_event" || type == "command_sent")
            {
                HandleCommandEventForSimulation(envelope.data, envelope.status);
                appliedSomething = true;
            }
            else if (ShouldApplyEnvelopeType(envelope.type))
            {
                ApplyPayload(envelope.data, source + " " + envelope.type);
                appliedSomething = true;
            }
        }
        else if (envelope != null && envelope.payload != null)
        {
            string type = envelope.type == null ? "" : envelope.type.Trim().ToLowerInvariant();

            if (type == "command_event" || type == "command_sent")
            {
                HandleCommandEventForSimulation(envelope.payload, envelope.status);
                appliedSomething = true;
            }
            else if (ShouldApplyEnvelopeType(envelope.type))
            {
                ApplyPayload(envelope.payload, source + " " + envelope.type);
                appliedSomething = true;
            }
        }

        if (!appliedSomething)
        {
            try
            {
                RealtimePayload directPayload = JsonUtility.FromJson<RealtimePayload>(json);
                if (directPayload != null)
                {
                    ApplyPayload(directPayload, source + " DIRECT");
                }
            }
            catch
            {
                Debug.LogWarning("[BACKEND REALTIME] Cannot parse payload.");
            }
        }

        ApplyDaysAfterPlantingFromRawJson(json, source);
    }

    private bool ShouldApplyEnvelopeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return true;
        }

        type = type.Trim().ToLowerInvariant();

        if (type == "command_event")
        {
            return false;
        }

        if (type == "command_sent")
        {
            return false;
        }

        return true;
    }

    private void HandleCommandEventForSimulation(RealtimePayload payload, string envelopeStatus)
    {
        if (payload == null)
        {
            return;
        }

        string commandId = GetCommandId(payload);
        string target = payload.target == null ? "" : payload.target.Trim().ToLowerInvariant();
        string state = payload.state == null ? "" : payload.state.Trim().ToUpperInvariant();
        string status = "";

        if (!string.IsNullOrWhiteSpace(payload.status))
        {
            status = payload.status.Trim().ToUpperInvariant();
        }
        else if (!string.IsNullOrWhiteSpace(envelopeStatus))
        {
            status = envelopeStatus.Trim().ToUpperInvariant();
        }

        int duration = Mathf.Max(0, payload.duration_s);

        if (!ShouldUseCommandEventStatus(status))
        {
            if (debugLog)
            {
                Debug.Log("[BACKEND COMMAND] Ignore command_event status=" + status
                    + " target=" + target
                    + " state=" + state);
            }

            return;
        }

        if (target == "pump")
        {
            if (state == "ON")
            {
                if (!string.IsNullOrWhiteSpace(commandId) && commandId == activePumpCommandId)
                {
                    return;
                }

                float durationSeconds = duration > 0 ? duration : Mathf.Max(1f, fallbackPumpDurationSeconds);
                StartPumpSimulation(durationSeconds, commandId);
            }
            else if (state == "OFF")
            {
                StopPumpSimulation();
            }
        }

        if (target == "light")
        {
            if (state == "ON")
            {
                if (!string.IsNullOrWhiteSpace(commandId) && commandId == activeLightCommandId)
                {
                    return;
                }

                float durationSeconds = duration > 0 ? duration : Mathf.Max(1f, fallbackLightDurationSeconds);
                StartLightSimulation(durationSeconds, commandId);
            }
            else if (state == "OFF")
            {
                StopLightSimulation();
            }
        }
    }

    private bool ShouldUseCommandEventStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        status = status.Trim().ToUpperInvariant();

        if (status == "QUEUED")
        {
            return true;
        }

        if (status == "SENT")
        {
            return true;
        }

        if (status == "RETRY")
        {
            return true;
        }

        return false;
    }

    private string GetCommandId(RealtimePayload payload)
    {
        if (payload == null)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(payload.command_id))
        {
            return payload.command_id;
        }

        if (!string.IsNullOrWhiteSpace(payload.id))
        {
            return payload.id;
        }

        return "";
    }

    private void StartPumpSimulation(float durationSeconds, string commandId)
    {
        if (!simulateDurationFromBackendCommand)
        {
            return;
        }

        pumpSimulationActive = true;
        pumpSimulationUntil = Time.time + Mathf.Max(0.1f, durationSeconds);
        activePumpCommandId = commandId;

        lastPumpState = "ON";

        if (pumpStatusText != null)
        {
            pumpStatusText.text = "Pump Status: ON";
        }

        if (actuatorVisualController != null)
        {
            actuatorVisualController.ApplyPumpState(true);
        }
    }

    private void StopPumpSimulation()
    {
        pumpSimulationActive = false;
        pumpSimulationUntil = 0f;
        activePumpCommandId = "";

        lastPumpState = "OFF";

        if (pumpStatusText != null)
        {
            pumpStatusText.text = "Pump Status: OFF";
        }

        if (actuatorVisualController != null)
        {
            actuatorVisualController.ApplyPumpState(false);
        }
    }

    private void StartLightSimulation(float durationSeconds, string commandId)
    {
        if (!simulateDurationFromBackendCommand)
        {
            return;
        }

        lightSimulationActive = true;
        lightSimulationUntil = Time.time + Mathf.Max(0.1f, durationSeconds);
        activeLightCommandId = commandId;

        lastLightState = "ON";

        if (lightStatusText != null)
        {
            lightStatusText.text = "Light Status: ON";
        }

        if (actuatorVisualController != null)
        {
            actuatorVisualController.ApplyLightState(true);
        }
    }

    private void StopLightSimulation()
    {
        lightSimulationActive = false;
        lightSimulationUntil = 0f;
        activeLightCommandId = "";

        lastLightState = "OFF";

        if (lightStatusText != null)
        {
            lightStatusText.text = "Light Status: OFF";
        }

        if (actuatorVisualController != null)
        {
            actuatorVisualController.ApplyLightState(false);
        }
    }

    private void CheckSimulationTimeouts()
    {
        if (pumpSimulationActive && Time.time >= pumpSimulationUntil)
        {
            StopPumpSimulation();
        }

        if (lightSimulationActive && Time.time >= lightSimulationUntil)
        {
            StopLightSimulation();
        }
    }

    private void ApplyPayload(RealtimePayload payload, string source)
    {
        if (payload == null)
        {
            return;
        }

        float temperature = ExtractTemperature(payload);
        float humidity = ExtractHumidity(payload);
        float lux = ExtractLux(payload);
        float soil = ExtractSoilMoisture(payload);
        int needWatering = ExtractNeedWatering(payload);

        UpdateStableAlert(payload, source, soil, needWatering);
        string alert = lastAlertText;

        string parsedPumpState = ExtractPumpState(payload);
        string parsedLightState = ExtractLightState(payload);

        if (IsKnownState(parsedPumpState))
        {
            if (!pumpSimulationActive)
            {
                lastPumpState = ToDisplayState(parsedPumpState);
            }
        }

        if (IsKnownState(parsedLightState))
        {
            if (!lightSimulationActive)
            {
                lastLightState = ToDisplayState(parsedLightState);
            }
        }

        float daysAfterPlanting = float.NaN;

        if (PayloadMayContainPlantingDays(payload))
        {
            float rawDays = ExtractDaysAfterPlanting(payload);

            if (ShouldApplyDaysAfterPlanting(rawDays))
            {
                daysAfterPlanting = rawDays;
            }
        }

        DateTimeOffset timestamp = ExtractTimestamp(payload);
        lastRealtimeUtc = timestamp;

        UpdatePanel(
            temperature,
            humidity,
            lux,
            soil,
            needWatering,
            alert,
            lastPumpState,
            lastLightState,
            daysAfterPlanting,
            timestamp,
            source
        );

        UpdateActuatorModel(parsedPumpState, parsedLightState);
    }

    private void UpdateStableAlert(RealtimePayload payload, string source, float soil, int needWatering)
    {
        if (payload == null)
        {
            return;
        }

        bool isSensorPayload = false;

        string src = source == null ? "" : source.ToLowerInvariant();

        if (src.Contains("sensor"))
        {
            isSensorPayload = true;
        }

        if (payload.sensor != null)
        {
            isSensorPayload = true;
        }

        if (!float.IsNaN(soil))
        {
            isSensorPayload = true;
        }

        if (!isSensorPayload)
        {
            return;
        }

        if (!float.IsNaN(soil))
        {
            if (soil <= soilDryOnThreshold)
            {
                soilDryAlertActive = true;
            }
            else if (soil >= soilDryOffThreshold)
            {
                soilDryAlertActive = false;
            }
        }

        if (needWatering == 1)
        {
            soilDryAlertActive = true;
        }

        if (!string.IsNullOrWhiteSpace(payload.alert))
        {
            string rawAlert = payload.alert.Trim();

            if (rawAlert.ToLowerInvariant() == "normal")
            {
                if (!soilDryAlertActive)
                {
                    lastAlertText = "Normal";
                }
            }
            else
            {
                lastAlertText = rawAlert;
                return;
            }
        }

        if (soilDryAlertActive)
        {
            if (!float.IsNaN(soil))
            {
                lastAlertText = "Đất khô " + soil.ToString("0.0") + "%";
            }
            else
            {
                lastAlertText = "Đất khô";
            }
        }
        else
        {
            lastAlertText = "Normal";
        }
    }

    private void ApplyDaysAfterPlantingFromRawJson(string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        bool hasDays = TryExtractFloatFromJson(json, "days_after_planting", out float daysAfterPlanting);
        bool hasEpoch = TryExtractLongFromJson(json, "planting_start_epoch", out long plantingEpoch);

        if (plantingEpoch > 0)
        {
            lastPlantingStartEpoch = plantingEpoch;
        }

        if (!hasDays)
        {
            return;
        }

        if (!ShouldApplyDaysAfterPlanting(daysAfterPlanting))
        {
            return;
        }

        UpdatePlantingPanelAndGrowth(daysAfterPlanting, source + " RAW_DAYS");
    }

    private bool TryExtractFloatFromJson(string json, string fieldName, out float value)
    {
        value = 0f;

        string pattern =
            "\\\"" + Regex.Escape(fieldName) + "\\\"\\s*:\\s*\\\"?(-?\\d+(\\.\\d+)?)\\\"?";

        Match match = Regex.Match(json, pattern);

        if (!match.Success)
        {
            return false;
        }

        string numberText = match.Groups[1].Value;

        return float.TryParse(
            numberText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value
        );
    }

    private bool TryExtractLongFromJson(string json, string fieldName, out long value)
    {
        value = 0;

        string pattern =
            "\\\"" + Regex.Escape(fieldName) + "\\\"\\s*:\\s*\\\"?(\\d+)\\\"?";

        Match match = Regex.Match(json, pattern);

        if (!match.Success)
        {
            return false;
        }

        string numberText = match.Groups[1].Value;

        return long.TryParse(
            numberText,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value
        );
    }

    private void UpdatePlantingPanelAndGrowth(float daysAfterPlanting, string source)
    {
        if (startInfoText != null)
        {
            string startText = "Thời gian gieo: --";

            if (lastPlantingStartEpoch > 0)
            {
                DateTimeOffset startTime = DateTimeOffset.FromUnixTimeSeconds(lastPlantingStartEpoch).ToLocalTime();
                startText = "Thời gian gieo: " + startTime.ToString("yyyy-MM-dd HH:mm:ss zzz");
            }

            startInfoText.text =
                startText
                + "\n"
                + "Số ngày sau gieo: " + daysAfterPlanting.ToString("0.00") + " ngày";
        }

        float safeDays = Mathf.Max(0f, daysAfterPlanting);

        float progress = ComputeLogisticGrowthProgress(safeDays);

        float maxHeight = GetEffectiveMaxPlantHeightCm();

        float rawHeightCm = Mathf.Lerp(0f, maxHeight, progress);

        float calibratedHeightCm = rawHeightCm * Mathf.Clamp(heightCalibrationFactor, 0.1f, 2.0f);

        float heightCm = Mathf.Clamp(calibratedHeightCm, 0f, maxHeight);

        if (growthSimulator != null)
        {
            growthSimulator.ApplyGrowthOutputFromDatabase(progress, heightCm);
        }

        if (!plantGrowthSimulatorOwnsHeightText)
        {
            if (plantHeightText != null)
            {
                plantHeightText.text = "Plant Height: " + heightCm.ToString("0.0") + " cm";
            }

            if (growthProgressText != null)
            {
                growthProgressText.text = "Growth Progress: " + (progress * 100f).ToString("0.0") + " %";
            }
        }

        if (debugLog)
        {
            Debug.Log("[GROWTH BACKEND] days="
                + daysAfterPlanting.ToString("0.00")
                + " | progress="
                + progress.ToString("0.000")
                + " | rawHeight="
                + rawHeightCm.ToString("0.0")
                + " cm | calibratedHeight="
                + heightCm.ToString("0.0")
                + " cm | source="
                + source);
        }
    }

    private float GetEffectiveMaxPlantHeightCm()
    {
        if (growthSimulator != null)
        {
            return Mathf.Max(0.01f, growthSimulator.maxPlantHeightCm);
        }

        return Mathf.Max(0.01f, maxPlantHeightCm);
    }

    private float ComputeLogisticGrowthProgress(float daysAfterPlanting)
    {
        float t = Mathf.Max(0f, daysAfterPlanting);

        float mMax = Mathf.Max(0.0001f, maxBiomass);
        float m0 = Mathf.Clamp(initialBiomass, 0.0001f, mMax - 0.0001f);
        float mTarget = Mathf.Clamp(targetBiomassAtCycleEnd, m0 + 0.0001f, mMax - 0.0001f);

        float cycle = Mathf.Max(0.01f, realFullCycleDays);
        float envFactor = Mathf.Max(0.01f, environmentGrowthFactor);

        float a = (mMax - m0) / m0;
        float b = (mMax / mTarget) - 1f;

        float r = Mathf.Log(a / b) / cycle;
        float rEffective = r * envFactor;

        float biomass = mMax / (1f + a * Mathf.Exp(-rEffective * t));
        float progress = (biomass - m0) / (mTarget - m0);

        return Mathf.Clamp01(progress);
    }

    private bool PayloadMayContainPlantingDays(RealtimePayload payload)
    {
        if (payload == null)
        {
            return false;
        }

        if (payload.planting_start != null)
        {
            return true;
        }

        if (payload.planting_start_epoch > 0)
        {
            return true;
        }

        if (Mathf.Abs(payload.days_after_planting) > 0.0001f)
        {
            return true;
        }

        return false;
    }

    private bool ShouldApplyDaysAfterPlanting(float incomingDays)
    {
        if (float.IsNaN(incomingDays))
        {
            return false;
        }

        if (incomingDays < -1.0f)
        {
            return false;
        }

        lastDisplayedDaysAfterPlanting = incomingDays;

        if (debugLog)
        {
            Debug.Log("[PLANTING] Force apply days_after_planting = "
                + incomingDays.ToString("0.00"));
        }

        return true;
    }

    private void UpdatePanel(
        float temperature,
        float humidity,
        float lux,
        float soil,
        int needWatering,
        string alert,
        string pumpState,
        string lightState,
        float daysAfterPlanting,
        DateTimeOffset timestamp,
        string source
    )
    {
        if (temperatureText != null && !float.IsNaN(temperature))
        {
            temperatureText.text = "Temperature: " + FormatFloat(temperature, "0.0") + " °C";
        }

        if (airHumidityText != null && !float.IsNaN(humidity))
        {
            airHumidityText.text = "Air Humidity: " + FormatFloat(humidity, "0.0") + " %";
        }

        if (luxText != null && !float.IsNaN(lux))
        {
            luxText.text = "Lux: " + FormatFloat(lux, "0");
        }

        if (soilMoistureText != null && !float.IsNaN(soil))
        {
            soilMoistureText.text = "Soil Moisture: " + FormatFloat(soil, "0.0") + " %";
        }

        if (needWateringText != null && needWatering >= 0)
        {
            needWateringText.text = "Need Watering: " + needWatering;
        }

        if (pumpStatusText != null)
        {
            pumpStatusText.text = "Pump Status: " + ToDisplayState(pumpState);
        }

        if (lightStatusText != null)
        {
            lightStatusText.text = "Light Status: " + ToDisplayState(lightState);
        }

        if (alertText != null && !string.IsNullOrWhiteSpace(alert))
        {
            alertText.text = "Alert: " + alert;
        }

        if (!float.IsNaN(daysAfterPlanting))
        {
            UpdatePlantingPanelAndGrowth(daysAfterPlanting, source + " PANEL");
        }

        if (lastUpdateText != null)
        {
            lastUpdateText.text = "Last Update: " + timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (dataStatusText != null)
        {
            dataStatusText.text = "Data Status: LIVE " + source;
        }
    }

    private void UpdateActuatorModel(string pumpState, string lightState)
    {
        if (actuatorVisualController != null)
        {
            if (!pumpSimulationActive)
            {
                actuatorVisualController.ApplyActuatorState(pumpState, "");
            }

            if (!lightSimulationActive)
            {
                actuatorVisualController.ApplyActuatorState("", lightState);
            }
        }
        else
        {
            Debug.LogWarning("[BACKEND REALTIME] ActuatorVisualController is NOT assigned.");
        }
    }

    private float ExtractTemperature(RealtimePayload payload)
    {
        if (payload.sensor != null)
        {
            if (HasValue(payload.sensor.temperature)) return payload.sensor.temperature;
            if (HasValue(payload.sensor.temp)) return payload.sensor.temp;
        }

        if (HasValue(payload.temperature)) return payload.temperature;
        if (HasValue(payload.temp)) return payload.temp;

        return float.NaN;
    }

    private float ExtractHumidity(RealtimePayload payload)
    {
        if (payload.sensor != null)
        {
            if (HasValue(payload.sensor.air_humidity)) return payload.sensor.air_humidity;
            if (HasValue(payload.sensor.humidity)) return payload.sensor.humidity;
            if (HasValue(payload.sensor.hum)) return payload.sensor.hum;
        }

        if (HasValue(payload.air_humidity)) return payload.air_humidity;
        if (HasValue(payload.humidity)) return payload.humidity;
        if (HasValue(payload.hum)) return payload.hum;

        return float.NaN;
    }

    private float ExtractLux(RealtimePayload payload)
    {
        if (payload.sensor != null)
        {
            return payload.sensor.lux;
        }

        return payload.lux;
    }

    private float ExtractSoilMoisture(RealtimePayload payload)
    {
        if (payload.sensor != null)
        {
            if (HasValue(payload.sensor.soil_moisture)) return payload.sensor.soil_moisture;
            if (HasValue(payload.sensor.soil_moisture_fused)) return payload.sensor.soil_moisture_fused;
            if (HasValue(payload.sensor.soil_moisture_mean)) return payload.sensor.soil_moisture_mean;
            if (HasValue(payload.sensor.soil_moisture_avg)) return payload.sensor.soil_moisture_avg;
            if (HasValue(payload.sensor.soil_avg)) return payload.sensor.soil_avg;
            if (HasValue(payload.sensor.soil)) return payload.sensor.soil;
        }

        if (HasValue(payload.soil_moisture)) return payload.soil_moisture;
        if (HasValue(payload.soil_moisture_fused)) return payload.soil_moisture_fused;
        if (HasValue(payload.soil_moisture_mean)) return payload.soil_moisture_mean;
        if (HasValue(payload.soil_moisture_avg)) return payload.soil_moisture_avg;
        if (HasValue(payload.soil_avg)) return payload.soil_avg;
        if (HasValue(payload.soil)) return payload.soil;

        return float.NaN;
    }

    private int ExtractNeedWatering(RealtimePayload payload)
    {
        if (payload.ai != null)
        {
            return payload.ai.need_watering;
        }

        return payload.need_watering;
    }

    private string ExtractPumpState(RealtimePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.pump_state)) return payload.pump_state;
        if (!string.IsNullOrWhiteSpace(payload.pump_status)) return payload.pump_status;

        if (!string.IsNullOrWhiteSpace(payload.target) && !string.IsNullOrWhiteSpace(payload.state))
        {
            if (payload.target.Trim().ToLowerInvariant() == "pump")
            {
                return payload.state;
            }
        }

        if (payload.pump != null && !string.IsNullOrWhiteSpace(payload.pump.state))
        {
            return payload.pump.state;
        }

        if (payload.pump != null && !string.IsNullOrWhiteSpace(payload.pump.status))
        {
            return payload.pump.status;
        }

        if (payload.actuator != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.actuator.pump_state)) return payload.actuator.pump_state;
            if (!string.IsNullOrWhiteSpace(payload.actuator.pump_status)) return payload.actuator.pump_status;

            if (payload.actuator.pump != null && !string.IsNullOrWhiteSpace(payload.actuator.pump.state))
            {
                return payload.actuator.pump.state;
            }

            if (payload.actuator.pump != null && !string.IsNullOrWhiteSpace(payload.actuator.pump.status))
            {
                return payload.actuator.pump.status;
            }
        }

        if (payload.status_data != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.status_data.pump_state)) return payload.status_data.pump_state;
            if (!string.IsNullOrWhiteSpace(payload.status_data.pump_status)) return payload.status_data.pump_status;
        }

        if (payload.status_block != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.status_block.pump_state)) return payload.status_block.pump_state;
            if (!string.IsNullOrWhiteSpace(payload.status_block.pump_status)) return payload.status_block.pump_status;
        }

        if (payload.control != null && payload.control.pump != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.control.pump.state))
            {
                return payload.control.pump.state;
            }

            if (!string.IsNullOrWhiteSpace(payload.control.pump.status))
            {
                return payload.control.pump.status;
            }
        }

        return "";
    }

    private string ExtractLightState(RealtimePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.light_state)) return payload.light_state;
        if (!string.IsNullOrWhiteSpace(payload.light_status)) return payload.light_status;

        if (!string.IsNullOrWhiteSpace(payload.target) && !string.IsNullOrWhiteSpace(payload.state))
        {
            if (payload.target.Trim().ToLowerInvariant() == "light")
            {
                return payload.state;
            }
        }

        if (payload.light != null && !string.IsNullOrWhiteSpace(payload.light.state))
        {
            return payload.light.state;
        }

        if (payload.light != null && !string.IsNullOrWhiteSpace(payload.light.status))
        {
            return payload.light.status;
        }

        if (payload.actuator != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.actuator.light_state)) return payload.actuator.light_state;
            if (!string.IsNullOrWhiteSpace(payload.actuator.light_status)) return payload.actuator.light_status;

            if (payload.actuator.light != null && !string.IsNullOrWhiteSpace(payload.actuator.light.state))
            {
                return payload.actuator.light.state;
            }

            if (payload.actuator.light != null && !string.IsNullOrWhiteSpace(payload.actuator.light.status))
            {
                return payload.actuator.light.status;
            }
        }

        if (payload.status_data != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.status_data.light_state)) return payload.status_data.light_state;
            if (!string.IsNullOrWhiteSpace(payload.status_data.light_status)) return payload.status_data.light_status;
        }

        if (payload.status_block != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.status_block.light_state)) return payload.status_block.light_state;
            if (!string.IsNullOrWhiteSpace(payload.status_block.light_status)) return payload.status_block.light_status;
        }

        if (payload.control != null && payload.control.light != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.control.light.state))
            {
                return payload.control.light.state;
            }

            if (!string.IsNullOrWhiteSpace(payload.control.light.status))
            {
                return payload.control.light.status;
            }
        }

        return "";
    }

    private float ExtractDaysAfterPlanting(RealtimePayload payload)
    {
        if (payload.planting_start != null && payload.planting_start.planting_start_epoch > 0)
        {
            lastPlantingStartEpoch = payload.planting_start.planting_start_epoch;
        }

        if (payload.planting_start_epoch > 0)
        {
            lastPlantingStartEpoch = payload.planting_start_epoch;
        }

        if (payload.planting_start != null)
        {
            return payload.planting_start.days_after_planting;
        }

        return payload.days_after_planting;
    }

    private DateTimeOffset ExtractTimestamp(RealtimePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.timestamp))
        {
            if (DateTimeOffset.TryParse(payload.timestamp, out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        if (!string.IsNullOrWhiteSpace(payload._time))
        {
            if (DateTimeOffset.TryParse(payload._time, out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private bool HasValue(float value)
    {
        return !float.IsNaN(value) && Mathf.Abs(value) > 0.0001f;
    }

    private bool IsKnownState(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().ToUpperInvariant();

        return value == "ON"
            || value == "OFF"
            || value == "1"
            || value == "0"
            || value == "TRUE"
            || value == "FALSE"
            || value == "PUMP_ON"
            || value == "PUMP_OFF"
            || value == "LIGHT_ON"
            || value == "LIGHT_OFF";
    }

    private string ToDisplayState(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "UNKNOWN";
        }

        value = value.Trim().ToUpperInvariant();

        if (value == "ON" || value == "1" || value == "TRUE" || value == "PUMP_ON" || value == "LIGHT_ON")
        {
            return "ON";
        }

        if (value == "OFF" || value == "0" || value == "FALSE" || value == "PUMP_OFF" || value == "LIGHT_OFF")
        {
            return "OFF";
        }

        return value;
    }

    private string FormatFloat(float value, string format)
    {
        if (float.IsNaN(value))
        {
            return "--";
        }

        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private string FormatDaysForLog(float value)
    {
        if (float.IsNaN(value))
        {
            return "--";
        }

        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void EnqueueMainThread(Action action)
    {
        lock (queueLock)
        {
            mainThreadActions.Enqueue(action);
        }
    }
}