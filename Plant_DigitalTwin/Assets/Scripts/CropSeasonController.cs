using System;
using UnityEngine;

public class CropSeasonController : MonoBehaviour
{
    [Header("Core References")]
    public PlantGrowthSimulator plantGrowthSimulator;
    public UnityBackendCommandClient backendCommandClient;
    public UnityBackendRealtimeClient backendRealtimeClient;

    [Header("START Mode")]
    public bool resetLocalUnityPreviewImmediately = true;
    public bool sendEpochFromUnity = false;

    [Header("Debug")]
    public bool debugLog = true;

    private void Awake()
    {
        AutoFindReferences();
    }

    private void Start()
    {
        AutoFindReferences();
    }

    private void AutoFindReferences()
    {
        if (plantGrowthSimulator == null)
        {
            plantGrowthSimulator = FindFirstObjectByType<PlantGrowthSimulator>();
        }

        if (backendCommandClient == null)
        {
            backendCommandClient = FindFirstObjectByType<UnityBackendCommandClient>();
        }

        if (backendRealtimeClient == null)
        {
            backendRealtimeClient = FindFirstObjectByType<UnityBackendRealtimeClient>();
        }
    }

    public void StartNewSeason()
    {
        AutoFindReferences();

        DateTimeOffset localRequestTimeUtc = DateTimeOffset.UtcNow;
        long localRequestEpoch = localRequestTimeUtc.ToUnixTimeSeconds();

        if (debugLog)
        {
            Debug.Log("[SEASON] START requested from Unity at "
                + localRequestTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }

        if (resetLocalUnityPreviewImmediately)
        {
            ResetLocalUnityPreview(localRequestTimeUtc);
        }

        NotifyRealtimeClientStartRequested(localRequestEpoch);

        if (backendCommandClient == null)
        {
            Debug.LogError("[SEASON] UnityBackendCommandClient is missing. START command was not sent to Backend.");
            return;
        }

        if (sendEpochFromUnity)
        {
            backendCommandClient.SendPlantingStartEpoch(localRequestTimeUtc);
        }
        else
        {
            backendCommandClient.SendPlantingStartNow();
        }
    }

    private void ResetLocalUnityPreview(DateTimeOffset startUtc)
    {
        if (plantGrowthSimulator != null)
        {
            plantGrowthSimulator.StartNewSeasonFromStartButton(startUtc);

            if (debugLog)
            {
                Debug.Log("[SEASON] Local Unity preview reset to 0.");
            }
        }
        else
        {
            Debug.LogWarning("[SEASON] PlantGrowthSimulator is missing. Local preview was not reset.");
        }
    }

    private void NotifyRealtimeClientStartRequested(long epoch)
    {
        if (backendRealtimeClient != null)
        {
            backendRealtimeClient.SendMessage(
                "NotifyPlantingStartRequestedFromUnity",
                epoch.ToString(),
                SendMessageOptions.DontRequireReceiver
            );

            if (debugLog)
            {
                Debug.Log("[SEASON] Notify UnityBackendRealtimeClient about START epoch preview = " + epoch);
            }
        }
    }
}