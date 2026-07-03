using UnityEngine;

public class PlantDataJsonTest : MonoBehaviour
{
    [Header("Digital Twin UI")]
    public PlantDigitalTwinUI digitalTwinUI;

    [Header("Test Mode")]
    public bool testPumpOn = true;
    public bool testLightOn = true;

    void Start()
    {
        string json = CreateTestJson(testPumpOn, testLightOn);
        PlantPayload payload = JsonUtility.FromJson<PlantPayload>(json);

        if (digitalTwinUI == null)
        {
            Debug.LogError("[PlantDataJsonTest] DigitalTwinUI is not assigned in Inspector.");
            return;
        }

        if (payload == null || payload.sensor == null || payload.ai == null || payload.pump == null || payload.light == null)
        {
            Debug.LogError("[PlantDataJsonTest] JSON parse failed or missing required fields.");
            return;
        }

        digitalTwinUI.UpdateDigitalTwin(
            payload.sensor.temperature,
            payload.sensor.air_humidity,
            payload.sensor.lux,
            payload.sensor.soil_moisture,
            payload.ai.need_watering,
            payload.pump.status,
            payload.light.status,
            payload.alert
        );
    }

    private string CreateTestJson(bool pumpOn, bool lightOn)
    {
        string pumpStatus = pumpOn ? "ON" : "OFF";
        string pumpAction = pumpOn ? "PUMP_ON" : "PUMP_OFF";
        int needWatering = pumpOn ? 1 : 0;

        string lightStatus = lightOn ? "ON" : "OFF";

        float temperature = pumpOn ? 32.0f : 28.0f;
        float airHumidity = pumpOn ? 60.0f : 72.0f;
        float lux = lightOn ? 1500.0f : 200.0f;
        float soilMoisture = pumpOn ? 25.0f : 55.0f;

        string alert = pumpOn
            ? "Dat kho, can tuoi nuoc"
            : "Dat du am, khong can tuoi";

        string json = $@"{{
            ""node_id"": ""CAI_XANH_01"",
            ""timestamp"": ""2026-05-25 10:30:00"",
            ""sensor"": {{
                ""temperature"": {temperature},
                ""air_humidity"": {airHumidity},
                ""lux"": {lux},
                ""soil_moisture"": {soilMoisture}
            }},
            ""ai"": {{
                ""need_watering"": {needWatering},
                ""action"": ""{pumpAction}""
            }},
            ""pump"": {{
                ""command"": ""{pumpStatus}"",
                ""status"": ""{pumpStatus}""
            }},
            ""light"": {{
                ""status"": ""{lightStatus}""
            }},
            ""alert"": ""{alert}""
        }}";

        return json;
    }
}