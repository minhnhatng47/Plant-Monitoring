using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class UnityInfluxDashboardReader : MonoBehaviour
{
    public static UnityInfluxDashboardReader Instance { get; private set; }

    [Header("InfluxDB Settings")]
    public string influxUrl = "https://us-east-1-1.aws.cloud2.influxdata.com";
    public string influxOrg = "DEV_TEAM";
    public string influxBucket = "digital_twin_data";

    [TextArea(2, 5)]
    public string influxToken = "";

    [Header("Query Settings")]
    public string nodeId = "BRASSICA_JUNCEA_01";
    public float refreshIntervalSeconds = 10f;
    public string sensorLookback = "6h";
    public string actuatorLookback = "6h";
    public string plantingLookback = "30d";

    [Header("Realtime Data Health")]
    public float liveThresholdSeconds = 30f;
    public float staleThresholdSeconds = 90f;

    [Header("MQTT Live Override")]
    public bool preferMqttLiveData = true;
    public float mqttLiveHoldSeconds = 60f;

    [Header("Model Visual Link")]
    public UnityActuatorVisualController actuatorVisualController;

    [Header("Growth From Database")]
    public bool enableGrowthFromDatabase = true;
    public float growthRefreshIntervalSeconds = 30f;
    public string growthAggregateWindow = "1m";
    public float fallbackFullCycleDays = 7f;
    public float maxIntegrationGapHours = 3f;

    [Header("Growth Environment Model")]
    public float tempMin = 10f;
    public float tempOpt = 26f;
    public float tempMax = 40f;
    public float luxHalfSaturation = 300f;
    public float humidityMin = 40f;
    public float humidityOptLow = 60f;
    public float humidityOptHigh = 85f;
    public float humidityMax = 98f;
    public float soilMin = 20f;
    public float soilOptLow = 45f;
    public float soilOptHigh = 75f;
    public float soilMax = 95f;

    [Header("Plant Growth Link")]
    public PlantGrowthSimulator growthSimulator;

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
    public TMP_Text lastUpdateText;
    public TMP_Text dataStatusText;

    [Header("Debug")]
    public bool debugLog = true;

    private Coroutine dashboardLoop;
    private Coroutine growthLoop;

    private bool hasPlantingStart = false;
    private DateTimeOffset plantingStartUtc;
    private DateTimeOffset latestSensorTimeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastGrowthProcessedUtc;

    private DateTimeOffset lastMqttSensorUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastMqttActuatorUtc = DateTimeOffset.MinValue;

    private float latestTemperature = float.NaN;
    private float latestAirHumidity = float.NaN;
    private float latestLux = float.NaN;
    private float latestSoilMoisture = float.NaN;
    private int latestNeedWatering = -1;
    private string latestAlert = "";
    private string latestPumpStatus = "UNKNOWN";
    private string latestLightStatus = "UNKNOWN";

    private double currentBiomass = 0.02;
    private bool growthStateInitialized = false;

    private class SensorSample
    {
        public DateTimeOffset timeUtc;
        public float temperature;
        public float airHumidity;
        public float lux;
        public float soilMoisture;
    }

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {
        dashboardLoop = StartCoroutine(DashboardRefreshLoop());
        growthLoop = StartCoroutine(GrowthRefreshLoop());
    }

    private void OnDisable()
    {
        if (dashboardLoop != null)
        {
            StopCoroutine(dashboardLoop);
            dashboardLoop = null;
        }

        if (growthLoop != null)
        {
            StopCoroutine(growthLoop);
            growthLoop = null;
        }
    }

    public void SetPlantingStartLocal(DateTimeOffset startUtc)
    {
        SetPlantingStartLocal(startUtc, false);
    }

    public void SetPlantingStartLocal(DateTimeOffset startUtc, bool resetGrowthSimulator)
    {
        plantingStartUtc = startUtc.ToUniversalTime();
        hasPlantingStart = true;
        ResetGrowthStateForNewSeason();

        if (resetGrowthSimulator && growthSimulator != null)
        {
            growthSimulator.StartNewSeasonFromStartButton(plantingStartUtc);
        }

        UpdatePlantingStartUI();

        if (debugLog)
        {
            Debug.Log("[INFLUX UI] Local planting start = " + plantingStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
    }

    public void SetPumpStatusLocal(bool isOn)
    {
        latestPumpStatus = isOn ? "ON" : "OFF";
        UpdateActuatorUI();
    }

    public void SetLightStatusLocal(bool isOn)
    {
        latestLightStatus = isOn ? "ON" : "OFF";
        UpdateActuatorUI();
    }

    public void ApplyMqttLiveSensor(
        float temperature,
        float airHumidity,
        float lux,
        float soilMoisture,
        int needWatering,
        string pumpStatus,
        string lightStatus,
        string alert,
        DateTimeOffset timestampUtc
    )
    {
        lastMqttSensorUtc = DateTimeOffset.UtcNow;
        latestSensorTimeUtc = timestampUtc.ToUniversalTime();

        if (!float.IsNaN(temperature)) latestTemperature = temperature;
        if (!float.IsNaN(airHumidity)) latestAirHumidity = airHumidity;
        if (!float.IsNaN(lux)) latestLux = lux;
        if (!float.IsNaN(soilMoisture)) latestSoilMoisture = soilMoisture;
        if (needWatering >= 0) latestNeedWatering = needWatering;

        latestAlert = alert ?? "";

        if (!string.IsNullOrWhiteSpace(pumpStatus)) latestPumpStatus = NormalizeOnOff(pumpStatus);
        if (!string.IsNullOrWhiteSpace(lightStatus)) latestLightStatus = NormalizeOnOff(lightStatus);

        UpdateSensorUI();
        UpdateActuatorUI();
        UpdatePlantingStartUI();
        UpdateDataHealthUI();

        if (dataStatusText != null)
        {
            dataStatusText.text = "Data Status: LIVE MQTT";
        }
    }

    public void ApplyMqttLiveActuator(string pumpStatus, string lightStatus, DateTimeOffset timestampUtc)
    {
        lastMqttActuatorUtc = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(pumpStatus)) latestPumpStatus = NormalizeOnOff(pumpStatus);
        if (!string.IsNullOrWhiteSpace(lightStatus)) latestLightStatus = NormalizeOnOff(lightStatus);

        UpdateActuatorUI();
        UpdateDataHealthUI();

        if (dataStatusText != null)
        {
            dataStatusText.text = "Data Status: LIVE MQTT";
        }
    }

    private bool IsMqttSensorLive()
    {
        if (lastMqttSensorUtc == DateTimeOffset.MinValue) return false;
        double age = Math.Abs((DateTimeOffset.UtcNow - lastMqttSensorUtc).TotalSeconds);
        return age <= mqttLiveHoldSeconds;
    }

    private bool IsMqttActuatorLive()
    {
        if (lastMqttActuatorUtc == DateTimeOffset.MinValue) return false;
        double age = Math.Abs((DateTimeOffset.UtcNow - lastMqttActuatorUtc).TotalSeconds);
        return age <= mqttLiveHoldSeconds;
    }

    private IEnumerator DashboardRefreshLoop()
    {
        while (true)
        {
            yield return StartCoroutine(RefreshDashboardOnce());
            yield return new WaitForSeconds(Mathf.Max(1f, refreshIntervalSeconds));
        }
    }

    private IEnumerator GrowthRefreshLoop()
    {
        yield return new WaitForSeconds(2f);

        while (true)
        {
            if (enableGrowthFromDatabase && hasPlantingStart)
            {
                yield return StartCoroutine(RefreshGrowthFromDatabase());
            }

            yield return new WaitForSeconds(Mathf.Max(5f, growthRefreshIntervalSeconds));
        }
    }

    private IEnumerator RefreshDashboardOnce()
    {
        yield return StartCoroutine(QueryLatestSensor());
        yield return StartCoroutine(QueryLatestActuator());
        yield return StartCoroutine(QueryLatestPlantingStart());

        UpdateSensorUI();
        UpdateActuatorUI();
        UpdatePlantingStartUI();
        UpdateDataHealthUI();
    }

    private IEnumerator QueryLatestSensor()
    {
        if (preferMqttLiveData && IsMqttSensorLive())
        {
            yield break;
        }

        string flux =
            "from(bucket: \"" + influxBucket + "\")\n"
            + "  |> range(start: -" + sensorLookback + ")\n"
            + "  |> filter(fn: (r) => r._measurement == \"sensors\")\n"
            + "  |> filter(fn: (r) => r.node_id == \"" + nodeId + "\")\n"
            + "  |> pivot(rowKey: [\"_time\"], columnKey: [\"_field\"], valueColumn: \"_value\")\n"
            + "  |> sort(columns: [\"_time\"], desc: true)\n"
            + "  |> limit(n: 1)\n";

        string csv = null;

        yield return StartCoroutine(QueryFlux(flux, result => csv = result, error =>
        {
            Debug.LogError("[INFLUX SENSOR] " + error);
        }));

        if (string.IsNullOrWhiteSpace(csv)) yield break;

        List<Dictionary<string, string>> rows = ParseFluxCsv(csv);
        if (rows.Count == 0) yield break;

        Dictionary<string, string> row = rows[0];

        latestSensorTimeUtc = GetTime(row, "_time", latestSensorTimeUtc);
        latestTemperature = GetFloatAny(row, new string[] { "temperature", "temp" }, latestTemperature);
        latestAirHumidity = GetFloatAny(row, new string[] { "air_humidity", "humidity", "hum" }, latestAirHumidity);
        latestLux = GetFloatAny(row, new string[] { "lux", "light_lux" }, latestLux);
        latestSoilMoisture = GetFloatAny(row, new string[] { "soil_moisture", "soil_moisture_avg", "soil_avg", "soil" }, latestSoilMoisture);
        latestNeedWatering = GetIntAny(row, new string[] { "need_watering" }, latestNeedWatering);
        latestAlert = GetStringAny(row, new string[] { "alert" }, latestAlert);

        string pump = GetStringAny(row, new string[] { "pump_state" }, "");
        string light = GetStringAny(row, new string[] { "light_state" }, "");

        if (!string.IsNullOrWhiteSpace(pump)) latestPumpStatus = NormalizeOnOff(pump);
        if (!string.IsNullOrWhiteSpace(light)) latestLightStatus = NormalizeOnOff(light);
    }

    private IEnumerator QueryLatestActuator()
    {
        if (preferMqttLiveData && IsMqttActuatorLive())
        {
            yield break;
        }

        string flux =
            "from(bucket: \"" + influxBucket + "\")\n"
            + "  |> range(start: -" + actuatorLookback + ")\n"
            + "  |> filter(fn: (r) => r._measurement == \"actuator\")\n"
            + "  |> filter(fn: (r) => r.node_id == \"" + nodeId + "\")\n"
            + "  |> pivot(rowKey: [\"_time\"], columnKey: [\"_field\"], valueColumn: \"_value\")\n"
            + "  |> sort(columns: [\"_time\"], desc: true)\n"
            + "  |> limit(n: 1)\n";

        string csv = null;

        yield return StartCoroutine(QueryFlux(flux, result => csv = result, error =>
        {
            Debug.LogError("[INFLUX ACTUATOR] " + error);
        }));

        if (string.IsNullOrWhiteSpace(csv)) yield break;

        List<Dictionary<string, string>> rows = ParseFluxCsv(csv);
        if (rows.Count == 0) yield break;

        Dictionary<string, string> row = rows[0];

        string pumpState = GetStringAny(row, new string[] { "pump_state", "pump" }, "");
        string lightState = GetStringAny(row, new string[] { "light_state", "light" }, "");

        if (!string.IsNullOrWhiteSpace(pumpState)) latestPumpStatus = NormalizeOnOff(pumpState);
        if (!string.IsNullOrWhiteSpace(lightState)) latestLightStatus = NormalizeOnOff(lightState);
    }

    private IEnumerator QueryLatestPlantingStart()
    {
        string flux =
            "from(bucket: \"" + influxBucket + "\")\n"
            + "  |> range(start: -" + plantingLookback + ")\n"
            + "  |> filter(fn: (r) => r._measurement == \"status\")\n"
            + "  |> filter(fn: (r) => r.node_id == \"" + nodeId + "\")\n"
            + "  |> filter(fn: (r) => r.target == \"planting_start\")\n"
            + "  |> pivot(rowKey: [\"_time\"], columnKey: [\"_field\"], valueColumn: \"_value\")\n"
            + "  |> sort(columns: [\"_time\"], desc: true)\n"
            + "  |> limit(n: 1)\n";

        string csv = null;

        yield return StartCoroutine(QueryFlux(flux, result => csv = result, error =>
        {
            Debug.LogError("[INFLUX PLANTING] " + error);
        }));

        if (string.IsNullOrWhiteSpace(csv)) yield break;

        List<Dictionary<string, string>> rows = ParseFluxCsv(csv);
        if (rows.Count == 0) yield break;

        Dictionary<string, string> row = rows[0];

        DateTimeOffset parsedStart;
        if (!TryGetPlantingStart(row, out parsedStart))
        {
            yield break;
        }

        parsedStart = parsedStart.ToUniversalTime();

        if (hasPlantingStart)
        {
            double diffSeconds = (parsedStart - plantingStartUtc).TotalSeconds;
            if (diffSeconds < -2)
            {
                if (debugLog)
                {
                    Debug.LogWarning(
                        "[INFLUX PLANTING] Ignore OLD database planting_start. "
                        + "Local=" + plantingStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                        + " | DB=" + parsedStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                    );
                }

                yield break;
            }
        }

        if (!hasPlantingStart || Math.Abs((parsedStart - plantingStartUtc).TotalSeconds) > 2)
        {
            plantingStartUtc = parsedStart;
            hasPlantingStart = true;
            ResetGrowthStateForNewSeason();

            if (growthSimulator != null)
            {
                growthSimulator.StartNewSeasonFromStartButton(plantingStartUtc);
            }

            if (debugLog)
            {
                Debug.Log("[INFLUX PLANTING] Applied database planting_start = " + plantingStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
            }
        }
    }

    private IEnumerator RefreshGrowthFromDatabase()
    {
        if (!hasPlantingStart)
        {
            yield break;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset queryStart;

        if (!growthStateInitialized)
        {
            queryStart = plantingStartUtc;
            currentBiomass = GetInitialBiomass();
            lastGrowthProcessedUtc = plantingStartUtc;
            growthStateInitialized = true;
        }
        else
        {
            queryStart = lastGrowthProcessedUtc;
        }

        if (queryStart > nowUtc)
        {
            queryStart = plantingStartUtc;
        }

        string startIso = queryStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        string flux =
            "from(bucket: \"" + influxBucket + "\")\n"
            + "  |> range(start: " + startIso + ")\n"
            + "  |> filter(fn: (r) => r._measurement == \"sensors\")\n"
            + "  |> filter(fn: (r) => r.node_id == \"" + nodeId + "\")\n"
            + "  |> filter(fn: (r) => r._field == \"temperature\" or r._field == \"air_humidity\" or r._field == \"humidity\" or r._field == \"hum\" or r._field == \"lux\" or r._field == \"soil_moisture\" or r._field == \"soil_moisture_avg\" or r._field == \"soil_avg\" or r._field == \"soil\")\n"
            + "  |> aggregateWindow(every: " + growthAggregateWindow + ", fn: mean, createEmpty: false)\n"
            + "  |> pivot(rowKey: [\"_time\"], columnKey: [\"_field\"], valueColumn: \"_value\")\n"
            + "  |> sort(columns: [\"_time\"], desc: false)\n";

        string csv = null;

        yield return StartCoroutine(QueryFlux(flux, result => csv = result, error =>
        {
            Debug.LogError("[INFLUX GROWTH] " + error);
        }));

        if (string.IsNullOrWhiteSpace(csv))
        {
            ApplyFallbackGrowthByElapsedTime();
            yield break;
        }

        List<Dictionary<string, string>> rows = ParseFluxCsv(csv);
        List<SensorSample> samples = ParseSensorSamples(rows);

        if (samples.Count == 0)
        {
            ApplyFallbackGrowthByElapsedTime();
            yield break;
        }

        IntegrateGrowthSamples(samples, nowUtc);
        ApplyCurrentGrowthToUnity();
    }

    private void ResetGrowthStateForNewSeason()
    {
        currentBiomass = GetInitialBiomass();
        lastGrowthProcessedUtc = plantingStartUtc;
        growthStateInitialized = false;
    }

    private void IntegrateGrowthSamples(List<SensorSample> samples, DateTimeOffset nowUtc)
    {
        DateTimeOffset prev = lastGrowthProcessedUtc;
        if (prev < plantingStartUtc) prev = plantingStartUtc;

        double maxGapDays = Math.Max(0.001, maxIntegrationGapHours / 24.0);

        foreach (SensorSample sample in samples)
        {
            if (sample.timeUtc <= prev) continue;

            double dtDays = (sample.timeUtc - prev).TotalDays;
            if (dtDays <= 0) continue;

            dtDays = Math.Min(dtDays, maxGapDays);

            double env = ComputeEnvironmentFactor(sample);
            double r0 = GetBaseGrowthRatePerDay();

            currentBiomass = currentBiomass + r0 * env * currentBiomass * (1.0 - currentBiomass) * dtDays;
            currentBiomass = Clamp(currentBiomass, GetInitialBiomass(), 1.0);

            prev = sample.timeUtc;
        }

        lastGrowthProcessedUtc = prev;
    }

    private void ApplyFallbackGrowthByElapsedTime()
    {
        if (!hasPlantingStart) return;

        double elapsedDays = Math.Max(0, (DateTimeOffset.UtcNow - plantingStartUtc).TotalDays);
        double cycleDays = GetFullCycleDays();
        double progress = Clamp01(elapsedDays / cycleDays);

        double m0 = GetInitialBiomass();
        double target = GetTargetBiomass();
        currentBiomass = m0 + progress * (target - m0);

        ApplyCurrentGrowthToUnity();
    }

    private void ApplyCurrentGrowthToUnity()
    {
        if (growthSimulator == null) return;

        double m0 = GetInitialBiomass();
        double target = GetTargetBiomass();
        float progress = (float)Clamp01((currentBiomass - m0) / (target - m0));

        float minH = growthSimulator.minPlantHeightCm;
        float maxH = growthSimulator.maxPlantHeightCm;
        float smooth = Smooth01(progress);
        float heightCm = Mathf.Lerp(minH, maxH, smooth);

        growthSimulator.ApplyGrowthOutputFromDatabase(progress, heightCm);

        if (plantHeightText != null)
        {
            plantHeightText.text = "Plant Height: " + heightCm.ToString("0.0") + " cm";
        }

        if (growthProgressText != null)
        {
            growthProgressText.text = "Growth Progress: " + (progress * 100f).ToString("0.0") + " %";
        }
    }

    private double ComputeEnvironmentFactor(SensorSample sample)
    {
        double fT = ComputeTemperatureFactor(sample.temperature);
        double fI = ComputeLightFactor(sample.lux);
        double fH = ComputeHumidityFactor(sample.airHumidity);
        double fSM = ComputeSoilFactor(sample.soilMoisture);
        return Clamp(fT * fI * fH * fSM, 0.0, 1.5);
    }

    private double ComputeTemperatureFactor(float temp)
    {
        if (float.IsNaN(temp)) return 0.7;
        if (temp <= tempMin || temp >= tempMax) return 0.0;
        if (Mathf.Approximately(temp, tempOpt)) return 1.0;
        if (temp < tempOpt) return Clamp01((temp - tempMin) / (tempOpt - tempMin));
        return Clamp01((tempMax - temp) / (tempMax - tempOpt));
    }

    private double ComputeLightFactor(float lux)
    {
        if (float.IsNaN(lux)) return 0.7;
        lux = Mathf.Max(0f, lux);
        return Clamp01(lux / (lux + Mathf.Max(1f, luxHalfSaturation)));
    }

    private double ComputeHumidityFactor(float hum)
    {
        if (float.IsNaN(hum)) return 0.7;
        if (hum <= humidityMin || hum >= humidityMax) return 0.3;
        if (hum >= humidityOptLow && hum <= humidityOptHigh) return 1.0;
        if (hum < humidityOptLow) return Clamp01((hum - humidityMin) / (humidityOptLow - humidityMin));
        return Clamp01((humidityMax - hum) / (humidityMax - humidityOptHigh));
    }

    private double ComputeSoilFactor(float soil)
    {
        if (float.IsNaN(soil)) return 0.7;
        if (soil <= soilMin) return 0.25;
        if (soil >= soilMax) return 0.5;
        if (soil >= soilOptLow && soil <= soilOptHigh) return 1.0;
        if (soil < soilOptLow) return 0.25 + 0.75 * Clamp01((soil - soilMin) / (soilOptLow - soilMin));
        return 1.0 - 0.5 * Clamp01((soil - soilOptHigh) / (soilMax - soilOptHigh));
    }

    private double GetBaseGrowthRatePerDay()
    {
        double m0 = GetInitialBiomass();
        double target = GetTargetBiomass();
        double totalDays = GetFullCycleDays();
        double a = (1.0 - m0) / m0;
        double b = (1.0 / target) - 1.0;
        return Math.Log(a / b) / totalDays;
    }

    private double GetInitialBiomass()
    {
        if (growthSimulator != null) return Clamp(growthSimulator.initialBiomass, 0.001, 0.2);
        return 0.02;
    }

    private double GetTargetBiomass()
    {
        if (growthSimulator != null) return Clamp(growthSimulator.targetBiomassAtCycleEnd, 0.5, 0.999);
        return 0.98;
    }

    private double GetFullCycleDays()
    {
        if (growthSimulator != null)
        {
            if (growthSimulator.useDemoCycleSeconds)
            {
                return Math.Max(1.0 / 86400.0, growthSimulator.demoFullCycleSeconds / 86400.0);
            }

            return Math.Max(0.01, growthSimulator.realFullCycleDays);
        }

        return Math.Max(0.01, fallbackFullCycleDays);
    }

    private List<SensorSample> ParseSensorSamples(List<Dictionary<string, string>> rows)
    {
        List<SensorSample> samples = new List<SensorSample>();

        foreach (Dictionary<string, string> row in rows)
        {
            DateTimeOffset t = GetTime(row, "_time", DateTimeOffset.MinValue);
            if (t == DateTimeOffset.MinValue) continue;

            SensorSample s = new SensorSample();
            s.timeUtc = t.ToUniversalTime();
            s.temperature = GetFloatAny(row, new string[] { "temperature", "temp" }, latestTemperature);
            s.airHumidity = GetFloatAny(row, new string[] { "air_humidity", "humidity", "hum" }, latestAirHumidity);
            s.lux = GetFloatAny(row, new string[] { "lux", "light_lux" }, latestLux);
            s.soilMoisture = GetFloatAny(row, new string[] { "soil_moisture", "soil_moisture_avg", "soil_avg", "soil" }, latestSoilMoisture);
            samples.Add(s);
        }

        samples.Sort((a, b) => a.timeUtc.CompareTo(b.timeUtc));
        return samples;
    }

    private void UpdateSensorUI()
    {
        if (temperatureText != null) temperatureText.text = "Temperature: " + FormatFloat(latestTemperature, "0.0") + " °C";
        if (airHumidityText != null) airHumidityText.text = "Air Humidity: " + FormatFloat(latestAirHumidity, "0.0") + " %";
        if (luxText != null) luxText.text = "Lux: " + FormatFloat(latestLux, "0");
        if (soilMoistureText != null) soilMoistureText.text = "Soil Moisture: " + FormatFloat(latestSoilMoisture, "0.0") + " %";

        if (needWateringText != null)
        {
            string value = latestNeedWatering < 0 ? "--" : latestNeedWatering.ToString();
            needWateringText.text = "Need Watering: " + value;
        }

        if (alertText != null)
        {
            alertText.text = string.IsNullOrWhiteSpace(latestAlert) ? "Alert: Normal" : "Alert: " + latestAlert;
        }
    }

    private void UpdateActuatorUI()
    {
        if (pumpStatusText != null) pumpStatusText.text = "Pump Status: " + latestPumpStatus;
        if (lightStatusText != null) lightStatusText.text = "Light Status: " + latestLightStatus;

        if (actuatorVisualController != null)
        {
            actuatorVisualController.ApplyActuatorState(latestPumpStatus, latestLightStatus);
        }
    }

    private void UpdatePlantingStartUI()
    {
        if (startInfoText == null) return;

        if (!hasPlantingStart)
        {
            startInfoText.text = "Thời gian gieo: --\nSố ngày sau gieo: --";
            return;
        }

        double days = Math.Max(0, (DateTimeOffset.UtcNow - plantingStartUtc).TotalDays);
        startInfoText.text = "Thời gian gieo: " + plantingStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") + "\n"
            + "Số ngày sau gieo: " + days.ToString("0.00") + " ngày";
    }

    private void UpdateDataHealthUI()
    {
        if (lastUpdateText != null)
        {
            lastUpdateText.text = latestSensorTimeUtc == DateTimeOffset.MinValue
                ? "Last Update: --"
                : "Last Update: " + latestSensorTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (dataStatusText != null)
        {
            if (IsMqttSensorLive())
            {
                dataStatusText.text = "Data Status: LIVE MQTT";
                return;
            }

            if (latestSensorTimeUtc == DateTimeOffset.MinValue)
            {
                dataStatusText.text = "Data Status: NO DATA";
                return;
            }

            double ageSeconds = Math.Abs((DateTimeOffset.UtcNow - latestSensorTimeUtc).TotalSeconds);

            if (ageSeconds <= liveThresholdSeconds) dataStatusText.text = "Data Status: LIVE DB";
            else if (ageSeconds <= staleThresholdSeconds) dataStatusText.text = "Data Status: DELAYED DB";
            else dataStatusText.text = "Data Status: STALE";
        }
    }

    private bool TryGetPlantingStart(Dictionary<string, string> row, out DateTimeOffset startUtc)
    {
        startUtc = DateTimeOffset.MinValue;
        string epochText = GetStringAny(row, new string[] { "planting_start_epoch" }, "");

        if (long.TryParse(epochText, NumberStyles.Any, CultureInfo.InvariantCulture, out long epoch) && epoch > 0)
        {
            startUtc = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }

        string timeText = GetStringAny(row, new string[] { "planting_start_time" }, "");

        if (!string.IsNullOrWhiteSpace(timeText)
            && DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            startUtc = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private IEnumerator QueryFlux(string flux, Action<string> onSuccess, Action<string> onError)
    {
        if (string.IsNullOrWhiteSpace(influxToken))
        {
            onError?.Invoke("Influx token is empty.");
            yield break;
        }

        string url = influxUrl.TrimEnd('/') + "/api/v2/query?org=" + UnityWebRequest.EscapeURL(influxOrg);
        string json = "{\"query\":\"" + EscapeJson(flux) + "\",\"type\":\"flux\"}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Authorization", "Token " + influxToken);
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "application/csv");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            onSuccess?.Invoke(request.downloadHandler.text);
        }
        else
        {
            onError?.Invoke(request.responseCode + " | " + request.error + " | " + request.downloadHandler.text);
        }

        request.Dispose();
    }

    private List<Dictionary<string, string>> ParseFluxCsv(string csv)
    {
        List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(csv)) return rows;

        string[] lines = csv.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
        string[] headers = null;

        foreach (string rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            if (rawLine.StartsWith("#")) continue;

            string[] cols = SplitCsvLine(rawLine);
            if (cols.Length == 0) continue;

            bool isHeader = false;
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == "_time")
                {
                    isHeader = true;
                    break;
                }
            }

            if (isHeader)
            {
                headers = cols;
                continue;
            }

            if (headers == null) continue;

            Dictionary<string, string> row = new Dictionary<string, string>();
            for (int i = 0; i < headers.Length && i < cols.Length; i++)
            {
                string key = headers[i].Trim();
                string value = CleanCsvValue(cols[i]);
                if (!string.IsNullOrWhiteSpace(key)) row[key] = value;
            }

            rows.Add(row);
        }

        return rows;
    }

    private string[] SplitCsvLine(string line)
    {
        List<string> result = new List<string>();
        StringBuilder current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Length = 0;
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }

    private string CleanCsvValue(string value)
    {
        if (value == null) return "";
        value = value.Trim();

        if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
        {
            value = value.Substring(1, value.Length - 2);
            value = value.Replace("\"\"", "\"");
        }

        return value;
    }

    private string GetStringAny(Dictionary<string, string> row, string[] keys, string defaultValue)
    {
        foreach (string key in keys)
        {
            if (row != null && row.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private float GetFloatAny(Dictionary<string, string> row, string[] keys, float defaultValue)
    {
        foreach (string key in keys)
        {
            if (row != null && row.TryGetValue(key, out string value)
                && float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
            {
                return result;
            }
        }

        return defaultValue;
    }

    private int GetIntAny(Dictionary<string, string> row, string[] keys, int defaultValue)
    {
        foreach (string key in keys)
        {
            if (row != null && row.TryGetValue(key, out string value))
            {
                if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int result)) return result;
                if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out float f)) return Mathf.RoundToInt(f);
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("ON", StringComparison.OrdinalIgnoreCase)) return 1;
                if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return 0;
            }
        }

        return defaultValue;
    }

    private DateTimeOffset GetTime(Dictionary<string, string> row, string key, DateTimeOffset defaultValue)
    {
        if (row == null) return defaultValue;
        if (!row.TryGetValue(key, out string value)) return defaultValue;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result))
        {
            return result.ToUniversalTime();
        }

        return defaultValue;
    }

    private string NormalizeOnOff(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "UNKNOWN";
        value = value.Trim().ToUpperInvariant();

        if (value == "1" || value == "TRUE" || value == "ON" || value == "PUMP_ON" || value == "LIGHT_ON") return "ON";
        if (value == "0" || value == "FALSE" || value == "OFF" || value == "PUMP_OFF" || value == "LIGHT_OFF") return "OFF";

        return value;
    }

    private string FormatFloat(float value, string format)
    {
        if (float.IsNaN(value)) return "--";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }

    private double Clamp01(double value)
    {
        return Clamp(value, 0.0, 1.0);
    }

    private double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
