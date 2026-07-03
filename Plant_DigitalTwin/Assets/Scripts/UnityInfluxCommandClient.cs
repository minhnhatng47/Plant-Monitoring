using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class UnityInfluxCommandClient : MonoBehaviour
{
    [Header("InfluxDB Settings")]
    public string influxUrl = "https://us-east-1-1.aws.cloud2.influxdata.com";
    public string influxOrg = "DEV_TEAM";
    public string influxBucket = "digital_twin_data";

    [TextArea(2, 5)]
    public string influxToken = "";

    [Header("System Identity")]
    public string nodeId = "BRASSICA_JUNCEA_01";
    public string source = "unity";
    public string measurementDt = "dt";

    [Header("Debug")]
    public bool debugLog = true;

    public void SendPlantingStartNowToInflux()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        SendPlantingStartEpochToInflux(nowUtc);
    }

    public void SendPlantingStartEpochToInflux(DateTimeOffset plantingStartUtc)
    {
        string commandId = CreateCommandId();
        long epoch = plantingStartUtc.ToUnixTimeSeconds();

        string line =
            EscapeMeasurement(measurementDt)
            + ",node_id=" + EscapeTag(nodeId)
            + ",command_id=" + EscapeTag(commandId)
            + ",target=planting_start"
            + ",status=PENDING"
            + " "
            + "action=\"SET_EPOCH\","
            + "reason=\"unity_start_new_season\","
            + "source=\"" + EscapeFieldString(source) + "\","
            + "planting_start_epoch=" + epoch + "i"
            + " "
            + epoch;

        StartCoroutine(WriteLineProtocol(line, "[INFLUX CMD] planting_start"));
    }

    public void SendPlantingStartGetToInflux()
    {
        string commandId = CreateCommandId();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string line =
            EscapeMeasurement(measurementDt)
            + ",node_id=" + EscapeTag(nodeId)
            + ",command_id=" + EscapeTag(commandId)
            + ",target=planting_start"
            + ",status=PENDING"
            + " "
            + "action=\"GET\","
            + "reason=\"unity_get_planting_start\","
            + "source=\"" + EscapeFieldString(source) + "\""
            + " "
            + now;

        StartCoroutine(WriteLineProtocol(line, "[INFLUX CMD] planting_start_get"));
    }

    public void SendPlantingStartClearToInflux()
    {
        string commandId = CreateCommandId();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string line =
            EscapeMeasurement(measurementDt)
            + ",node_id=" + EscapeTag(nodeId)
            + ",command_id=" + EscapeTag(commandId)
            + ",target=planting_start"
            + ",status=PENDING"
            + " "
            + "action=\"CLEAR\","
            + "reason=\"unity_clear_planting_start\","
            + "source=\"" + EscapeFieldString(source) + "\""
            + " "
            + now;

        StartCoroutine(WriteLineProtocol(line, "[INFLUX CMD] planting_start_clear"));
    }

    public void SendPumpOnToInflux()
    {
        SendActuatorCommandToInflux("pump", "ON", 10, "unity_pump_on");
    }

    public void SendPumpOffToInflux()
    {
        SendActuatorCommandToInflux("pump", "OFF", 0, "unity_pump_off");
    }

    public void SendLightOnToInflux()
    {
        SendActuatorCommandToInflux("light", "ON", 300, "unity_light_on");
    }

    public void SendLightOffToInflux()
    {
        SendActuatorCommandToInflux("light", "OFF", 0, "unity_light_off");
    }

    public void SendActuatorCommandToInflux(string target, string state, int durationSeconds, string reason)
    {
        string commandId = CreateCommandId();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        target = NormalizeLower(target);
        state = NormalizeUpper(state);

        string line =
            EscapeMeasurement(measurementDt)
            + ",node_id=" + EscapeTag(nodeId)
            + ",command_id=" + EscapeTag(commandId)
            + ",target=" + EscapeTag(target)
            + ",status=PENDING"
            + " "
            + "state=\"" + EscapeFieldString(state) + "\","
            + "duration_s=" + durationSeconds + "i,"
            + "reason=\"" + EscapeFieldString(reason) + "\","
            + "source=\"" + EscapeFieldString(source) + "\""
            + " "
            + now;

        StartCoroutine(WriteLineProtocol(line, "[INFLUX CMD] " + target + "_" + state));
    }

    private IEnumerator WriteLineProtocol(string lineProtocol, string label)
    {
        if (string.IsNullOrWhiteSpace(influxToken))
        {
            Debug.LogError(label + " failed: Influx token is empty.");
            yield break;
        }

        string url =
            influxUrl.TrimEnd('/')
            + "/api/v2/write?org="
            + UnityWebRequest.EscapeURL(influxOrg)
            + "&bucket="
            + UnityWebRequest.EscapeURL(influxBucket)
            + "&precision=s";

        byte[] bodyRaw = Encoding.UTF8.GetBytes(lineProtocol);

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("Authorization", "Token " + influxToken);
        request.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");

        if (debugLog)
        {
            Debug.Log(label + " line=" + lineProtocol);
        }

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            if (debugLog)
            {
                Debug.Log(label + " written to InfluxDB.");
            }
        }
        else
        {
            Debug.LogError(label + " write failed: "
                + request.responseCode
                + " | "
                + request.error
                + " | "
                + request.downloadHandler.text);
        }

        request.Dispose();
    }

    private string CreateCommandId()
    {
        return "unity-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string NormalizeLower(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim().ToLowerInvariant();
    }

    private string NormalizeUpper(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim().ToUpperInvariant();
    }

    private string EscapeMeasurement(string value)
    {
        return EscapeTag(value);
    }

    private string EscapeTag(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value
            .Replace("\\", "\\\\")
            .Replace(" ", "\\ ")
            .Replace(",", "\\,")
            .Replace("=", "\\=");
    }

    private string EscapeFieldString(string value)
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