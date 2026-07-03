using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class UnityBackendCommandClient : MonoBehaviour
{
    [Header("Backend API")]
    public string backendHttpBaseUrl = "http://100.110.157.78:8000";

    [Header("Command Paths")]
    public string pumpCommandPath = "/api/command/pump";
    public string lightCommandPath = "/api/command/light";
    public string plantingStartCommandPath = "/api/command/planting-start";

    [Header("Pump Command")]
    public int pumpOnDurationSeconds = 10;

    [Header("Light Command")]
    public int lightOnDurationSeconds = 300;

    [Header("Command Identity")]
    public string source = "unity";
    public string reason = "unity_manual";

    [Header("Optional Local Visual")]
    public bool optimisticLocalVisual = false;
    public UnityActuatorVisualController actuatorVisualController;
    public PlantGrowthSimulator growthSimulator;

    [Header("Debug")]
    public bool debugLog = true;

    public void SetPumpDurationSeconds(int seconds)
    {
        pumpOnDurationSeconds = Mathf.Max(1, seconds);
    }

    public void SetLightDurationSeconds(int seconds)
    {
        lightOnDurationSeconds = Mathf.Max(1, seconds);
    }

    public void SendPumpOn()
    {
        int duration = Mathf.Max(1, pumpOnDurationSeconds);

        string json =
            "{"
            + "\"state\":\"ON\","
            + "\"duration_s\":" + duration + ","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"" + EscapeJson(reason + "_pump_on") + "\""
            + "}";

        StartCoroutine(PostJson(pumpCommandPath, json, "Pump ON"));

        if (optimisticLocalVisual && actuatorVisualController != null)
        {
            actuatorVisualController.ApplyPumpState(true);
        }
    }

    public void SendPumpOff()
    {
        string json =
            "{"
            + "\"state\":\"OFF\","
            + "\"duration_s\":0,"
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"" + EscapeJson(reason + "_pump_off") + "\""
            + "}";

        StartCoroutine(PostJson(pumpCommandPath, json, "Pump OFF"));

        if (optimisticLocalVisual && actuatorVisualController != null)
        {
            actuatorVisualController.ApplyPumpState(false);
        }
    }

    public void SendLightOn()
    {
        int duration = Mathf.Max(1, lightOnDurationSeconds);

        string json =
            "{"
            + "\"state\":\"ON\","
            + "\"duration_s\":" + duration + ","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"" + EscapeJson(reason + "_light_on") + "\""
            + "}";

        StartCoroutine(PostJson(lightCommandPath, json, "Light ON"));

        if (optimisticLocalVisual && actuatorVisualController != null)
        {
            actuatorVisualController.ApplyLightState(true);
        }
    }

    public void SendLightOff()
    {
        string json =
            "{"
            + "\"state\":\"OFF\","
            + "\"duration_s\":0,"
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"" + EscapeJson(reason + "_light_off") + "\""
            + "}";

        StartCoroutine(PostJson(lightCommandPath, json, "Light OFF"));

        if (optimisticLocalVisual && actuatorVisualController != null)
        {
            actuatorVisualController.ApplyLightState(false);
        }
    }

    public void SendPlantingStartNow()
    {
        string json =
            "{"
            + "\"action\":\"SET_NOW\","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"unity_start_new_season\""
            + "}";

        StartCoroutine(PostJson(plantingStartCommandPath, json, "Planting START"));
    }

    public void SendPlantingStartEpoch(DateTimeOffset startUtc)
    {
        long epoch = startUtc.ToUnixTimeSeconds();

        string json =
            "{"
            + "\"action\":\"SET_EPOCH\","
            + "\"planting_start_epoch\":" + epoch + ","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"unity_start_new_season_epoch\""
            + "}";

        StartCoroutine(PostJson(plantingStartCommandPath, json, "Planting START EPOCH"));
    }

    public void SendPlantingStartGet()
    {
        string json =
            "{"
            + "\"action\":\"GET\","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"unity_get_start\""
            + "}";

        StartCoroutine(PostJson(plantingStartCommandPath, json, "Planting GET"));
    }

    public void SendPlantingStartClear()
    {
        string json =
            "{"
            + "\"action\":\"CLEAR\","
            + "\"source\":\"" + EscapeJson(source) + "\","
            + "\"reason\":\"unity_clear_start\""
            + "}";

        StartCoroutine(PostJson(plantingStartCommandPath, json, "Planting CLEAR"));
    }

    private IEnumerator PostJson(string path, string json, string label)
    {
        string url = backendHttpBaseUrl.TrimEnd('/') + path;

        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        if (debugLog)
        {
            Debug.Log("[BACKEND CMD] " + label + " -> " + url + " | " + json);
        }

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            if (debugLog)
            {
                Debug.Log("[BACKEND CMD] " + label + " OK: " + request.downloadHandler.text);
            }
        }
        else
        {
            Debug.LogError("[BACKEND CMD] " + label + " FAILED: "
                + request.responseCode
                + " | "
                + request.error
                + " | "
                + request.downloadHandler.text);
        }

        request.Dispose();
    }

    private string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }
}