using System;
using TMPro;
using UnityEngine;

public class PlantGrowthSimulator : MonoBehaviour
{
    [Header("Plant Stage Objects")]
    public GameObject stage1Seed;
    public GameObject stage2Sprout;
    public GameObject stage3Growing;
    public GameObject stage4Mature;

    [Header("START Time Sync")]
    public bool restoreStartTimeOnPlay = true;

    [Header("Backend Driven Growth")]
    [Tooltip("Bật chế độ này để cây nhận growth từ Backend/RealtimeClient, không tự chạy demo.")]
    public bool useDatabaseDrivenGrowth = true;

    [Tooltip("Bật để khi Play lại, cây dùng chiều cao gần nhất thay vì chạy từ 0.")]
    public bool restoreLastHeightOnPlay = true;

    [Header("Smooth Visual Growth")]
    [Tooltip("Bật để visual height chuyển mượt. Plant Height vẫn hiện dữ liệu thật.")]
    public bool smoothDatabaseGrowth = true;

    [Tooltip("Thời gian visual height đi từ chiều cao cũ tới chiều cao mới.")]
    public float databaseSmoothTime = 8.0f;

    public float databaseSnapThreshold = 0.0005f;

    [Tooltip("Nếu chưa có chiều cao cũ thì lần đầu nhận Backend sẽ nhảy ngay tới chiều cao hiện tại.")]
    public bool snapFirstBackendOutputIfNoCache = true;

    [Tooltip("Nếu visual đang gần 0 nhưng Backend gửi chiều cao lớn hơn mức này thì snap luôn để không chạy từ 0.")]
    public float minTargetHeightForStartupSnap = 0.5f;

    [Header("Legacy Demo Animation")]
    public float simulationDurationSeconds = 40f;

    [Tooltip("Bật để demo nhanh trong Unity. Ví dụ 120 giây là cây đi từ gieo đến trưởng thành.")]
    public bool useDemoCycleSeconds = false;

    [Tooltip("Thời gian hoàn tất một chu kỳ cây trong chế độ demo.")]
    public float demoFullCycleSeconds = 120f;

    [Tooltip("Thời gian hoàn tất một chu kỳ cây nếu chạy theo ngày thật.")]
    public float realFullCycleDays = 7f;

    [Tooltip("Khoảng cập nhật growth nếu dùng time-driven growth.")]
    public float timeSliceSeconds = 1f;

    [Header("Logistic Growth Model")]
    [Range(0.001f, 0.2f)]
    public float initialBiomass = 0.02f;

    [Range(0.5f, 0.999f)]
    public float targetBiomassAtCycleEnd = 0.98f;

    [Range(0f, 2f)]
    public float environmentGrowthFactor = 1f;

    [Header("Plant Height Output")]
    public float minPlantHeightCm = 0.0f;
    public float maxPlantHeightCm = 12.0f;
    public TMP_Text plantHeightText;

    [Header("Stage Height Thresholds")]
    [Tooltip("Từ 0 đến Stage 1 Max dùng Stage 1.")]
    public float stage1MaxHeightCm = 0.3f;

    [Tooltip("Lớn hơn Stage 1 Max đến Stage 2 Max dùng Stage 2.")]
    public float stage2MaxHeightCm = 3.0f;

    [Tooltip("Lớn hơn Stage 2 Max đến Stage 3 Max dùng Stage 3.")]
    public float stage3MaxHeightCm = 7.0f;

    [Tooltip("Lớn hơn Stage 3 Max dùng Stage 4.")]
    public float stage4MaxHeightCm = 12.0f;

    [Header("Controlled Objects")]
    public bool autoGrowLightByGrowthStage = false;
    public GameObject growLightSource;

    [Header("Optional UI")]
    public TMP_Text growthProgressText;
    public TMP_Text growthStageText;
    public TMP_Text growthDescriptionText;

    [Header("Debug")]
    public bool debugLog = true;

    private const string PlayerPrefsStartUnixMsKey = "CPS_PLANTING_START_UNIX_MS";
    private const string PlayerPrefsStartIsoKey = "CPS_PLANTING_START_ISO";
    private const string PlayerPrefsLastHeightKey = "CPS_LAST_PLANT_HEIGHT_CM";
    private const string PlayerPrefsLastProgressKey = "CPS_LAST_GROWTH_PROGRESS";

    private float targetPlantHeightCm = 0f;
    private float targetGrowthProgress = 0f;

    private float visualPlantHeightCm = 0f;
    private float visualGrowthProgress = 0f;

    private float visualHeightVelocity = 0f;
    private float visualProgressVelocity = 0f;

    private bool hasReceivedFirstBackendOutput = false;
    private bool hasRestoredCachedHeight = false;
    private bool legacyAnimationRunning = false;

    private bool hasPlantingStart = false;
    private DateTimeOffset plantingStartUtc;
    private float lastSliceUpdateTime = -999f;

    private bool warnedMissingStage = false;

    private void Start()
    {
        ClearAllStageRendererOverrides();

        targetPlantHeightCm = minPlantHeightCm;
        visualPlantHeightCm = minPlantHeightCm;

        targetGrowthProgress = 0f;
        visualGrowthProgress = 0f;

        hasReceivedFirstBackendOutput = false;
        hasRestoredCachedHeight = false;
        legacyAnimationRunning = false;

        if (restoreLastHeightOnPlay)
        {
            hasRestoredCachedHeight = LoadLastKnownHeight();
        }

        bool restoredPlantingStart = false;

        if (restoreStartTimeOnPlay)
        {
            restoredPlantingStart = LoadPlantingStartFromPlayerPrefs();
        }

        if (hasRestoredCachedHeight)
        {
            UpdatePlantHeightText(targetPlantHeightCm);
            UpdateGrowthProgressText(targetGrowthProgress);
            ApplyGrowthByHeight(visualPlantHeightCm);
        }
        else
        {
            ApplyGrowthByHeight(0f);
        }

        if (restoredPlantingStart && !useDatabaseDrivenGrowth)
        {
            UpdateGrowthFromPlantingTime(true);
        }

        WarnIfMissingStageObjects();
    }

    private void Update()
    {
        if (useDatabaseDrivenGrowth)
        {
            UpdateSmoothDatabaseGrowth();
            return;
        }

        if (hasPlantingStart)
        {
            float slice = Mathf.Max(0.1f, timeSliceSeconds);

            if (Time.time - lastSliceUpdateTime >= slice)
            {
                UpdateGrowthFromPlantingTime(false);
                lastSliceUpdateTime = Time.time;
            }

            return;
        }

        if (!legacyAnimationRunning)
        {
            return;
        }

        visualGrowthProgress += Time.deltaTime / Mathf.Max(1f, simulationDurationSeconds);
        visualGrowthProgress = Mathf.Clamp01(visualGrowthProgress);

        visualPlantHeightCm = Mathf.Lerp(minPlantHeightCm, maxPlantHeightCm, visualGrowthProgress);

        targetGrowthProgress = visualGrowthProgress;
        targetPlantHeightCm = visualPlantHeightCm;

        UpdatePlantHeightText(targetPlantHeightCm);
        UpdateGrowthProgressText(targetGrowthProgress);
        ApplyGrowthByHeight(visualPlantHeightCm);
        SaveLastKnownHeight();

        if (visualGrowthProgress >= 1f)
        {
            legacyAnimationRunning = false;
        }
    }

    public void StartNewCropSimulation()
    {
        StartNewCropFromNow();
    }

    public void StartNewCropFromNow()
    {
        StartNewCropAt(DateTimeOffset.UtcNow);
    }

    public void StartNewCropAt(DateTimeOffset startTime)
    {
        StartNewSeasonFromStartButton(startTime);
    }

    public void StartNewSeasonFromStartButton(DateTimeOffset startTime)
    {
        plantingStartUtc = startTime.ToUniversalTime();
        hasPlantingStart = true;
        legacyAnimationRunning = false;

        targetPlantHeightCm = minPlantHeightCm;
        visualPlantHeightCm = minPlantHeightCm;

        targetGrowthProgress = 0f;
        visualGrowthProgress = 0f;

        visualHeightVelocity = 0f;
        visualProgressVelocity = 0f;

        hasReceivedFirstBackendOutput = false;
        hasRestoredCachedHeight = false;

        lastSliceUpdateTime = Time.time;

        SavePlantingStartToPlayerPrefs();
        ClearLastKnownHeight();
        ClearAllStageRendererOverrides();

        UpdatePlantHeightText(0f);
        UpdateGrowthProgressText(0f);
        ApplyGrowthByHeight(0f);

        if (debugLog)
        {
            Debug.Log("[GROWTH] START new season | height = 0.0 cm");
        }
    }

    public void ResetGrowthSimulation()
    {
        hasPlantingStart = false;
        legacyAnimationRunning = false;
        hasReceivedFirstBackendOutput = false;
        hasRestoredCachedHeight = false;

        PlayerPrefs.DeleteKey(PlayerPrefsStartUnixMsKey);
        PlayerPrefs.DeleteKey(PlayerPrefsStartIsoKey);
        PlayerPrefs.Save();

        ClearLastKnownHeight();
        ClearAllStageRendererOverrides();

        targetPlantHeightCm = minPlantHeightCm;
        visualPlantHeightCm = minPlantHeightCm;

        targetGrowthProgress = 0f;
        visualGrowthProgress = 0f;

        visualHeightVelocity = 0f;
        visualProgressVelocity = 0f;

        UpdatePlantHeightText(0f);
        UpdateGrowthProgressText(0f);
        ApplyGrowthByHeight(0f);

        if (debugLog)
        {
            Debug.Log("[GROWTH] Reset growth simulation");
        }
    }

    public void SetGrowthProgressFromData(float progress)
    {
        float safeProgress = Mathf.Clamp01(progress);
        float safeHeight = Mathf.Lerp(minPlantHeightCm, maxPlantHeightCm, safeProgress);

        ApplyGrowthOutputFromDatabase(safeProgress, safeHeight);
    }

    public void ApplyGrowthOutputFromDatabase(float progress, float heightCm)
    {
        targetGrowthProgress = Mathf.Clamp01(progress);
        targetPlantHeightCm = Mathf.Clamp(heightCm, minPlantHeightCm, maxPlantHeightCm);

        legacyAnimationRunning = false;

        UpdatePlantHeightText(targetPlantHeightCm);
        UpdateGrowthProgressText(targetGrowthProgress);

        bool noCachedHeight = !hasRestoredCachedHeight;
        bool visualAlmostZero = visualPlantHeightCm <= 0.1f && targetPlantHeightCm >= minTargetHeightForStartupSnap;

        bool shouldSnap =
            snapFirstBackendOutputIfNoCache
            && !hasReceivedFirstBackendOutput
            && (noCachedHeight || visualAlmostZero);

        hasReceivedFirstBackendOutput = true;

        if (shouldSnap)
        {
            visualPlantHeightCm = targetPlantHeightCm;
            visualGrowthProgress = targetGrowthProgress;

            visualHeightVelocity = 0f;
            visualProgressVelocity = 0f;

            ApplyGrowthByHeight(visualPlantHeightCm);
            SaveLastKnownHeight();

            if (debugLog)
            {
                Debug.Log("[GROWTH] First backend output snapped | height = "
                    + targetPlantHeightCm.ToString("0.0")
                    + " cm");
            }

            return;
        }

        if (!smoothDatabaseGrowth)
        {
            visualPlantHeightCm = targetPlantHeightCm;
            visualGrowthProgress = targetGrowthProgress;

            visualHeightVelocity = 0f;
            visualProgressVelocity = 0f;

            ApplyGrowthByHeight(visualPlantHeightCm);
            SaveLastKnownHeight();
        }
    }

    public void ApplyHeightFromDatabase(float heightCm)
    {
        float safeHeight = Mathf.Clamp(heightCm, minPlantHeightCm, maxPlantHeightCm);
        float progress = 0f;

        if (maxPlantHeightCm > minPlantHeightCm)
        {
            progress = Mathf.InverseLerp(minPlantHeightCm, maxPlantHeightCm, safeHeight);
        }

        ApplyGrowthOutputFromDatabase(progress, safeHeight);
    }

    public void SetEnvironmentGrowthFactor(float factor)
    {
        environmentGrowthFactor = Mathf.Clamp(factor, 0f, 2f);

        if (hasPlantingStart && !useDatabaseDrivenGrowth)
        {
            UpdateGrowthFromPlantingTime(true);
        }
    }

    public bool HasPlantingStart()
    {
        return hasPlantingStart;
    }

    public DateTimeOffset GetPlantingStartUtc()
    {
        return plantingStartUtc;
    }

    public float GetGrowthProgress()
    {
        return targetGrowthProgress;
    }

    public float GetCurrentPlantHeightCm()
    {
        return targetPlantHeightCm;
    }

    public double GetElapsedDaysAfterPlanting()
    {
        if (!hasPlantingStart)
        {
            return 0;
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - plantingStartUtc;
        return Math.Max(0, elapsed.TotalDays);
    }

    public string GetPlantingStartLocalText()
    {
        if (!hasPlantingStart)
        {
            return "Chưa có dữ liệu";
        }

        return plantingStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    private void UpdateSmoothDatabaseGrowth()
    {
        if (!smoothDatabaseGrowth)
        {
            return;
        }

        if (!hasReceivedFirstBackendOutput)
        {
            return;
        }

        bool heightNeedsUpdate = Mathf.Abs(visualPlantHeightCm - targetPlantHeightCm) > databaseSnapThreshold;
        bool progressNeedsUpdate = Mathf.Abs(visualGrowthProgress - targetGrowthProgress) > databaseSnapThreshold;

        if (!heightNeedsUpdate && !progressNeedsUpdate)
        {
            UpdatePlantHeightText(targetPlantHeightCm);
            UpdateGrowthProgressText(targetGrowthProgress);
            return;
        }

        float smoothTime = Mathf.Max(0.01f, databaseSmoothTime);

        visualPlantHeightCm = Mathf.SmoothDamp(
            visualPlantHeightCm,
            targetPlantHeightCm,
            ref visualHeightVelocity,
            smoothTime
        );

        visualGrowthProgress = Mathf.SmoothDamp(
            visualGrowthProgress,
            targetGrowthProgress,
            ref visualProgressVelocity,
            smoothTime
        );

        if (Mathf.Abs(visualPlantHeightCm - targetPlantHeightCm) <= databaseSnapThreshold)
        {
            visualPlantHeightCm = targetPlantHeightCm;
            visualHeightVelocity = 0f;
        }

        if (Mathf.Abs(visualGrowthProgress - targetGrowthProgress) <= databaseSnapThreshold)
        {
            visualGrowthProgress = targetGrowthProgress;
            visualProgressVelocity = 0f;
        }

        visualPlantHeightCm = Mathf.Clamp(visualPlantHeightCm, minPlantHeightCm, maxPlantHeightCm);
        visualGrowthProgress = Mathf.Clamp01(visualGrowthProgress);

        UpdatePlantHeightText(targetPlantHeightCm);
        UpdateGrowthProgressText(targetGrowthProgress);

        ApplyGrowthByHeight(visualPlantHeightCm);
        SaveLastKnownHeight();
    }

    private void UpdateGrowthFromPlantingTime(bool force)
    {
        if (!hasPlantingStart)
        {
            return;
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - plantingStartUtc;
        double elapsedSeconds = Math.Max(0, elapsed.TotalSeconds);

        float newProgress = ComputeLogisticGrowthProgress(elapsedSeconds);
        float newHeightCm = Mathf.Lerp(minPlantHeightCm, maxPlantHeightCm, newProgress);

        if (force || Mathf.Abs(newHeightCm - visualPlantHeightCm) > 0.0001f)
        {
            visualGrowthProgress = newProgress;
            targetGrowthProgress = newProgress;

            visualPlantHeightCm = newHeightCm;
            targetPlantHeightCm = newHeightCm;

            UpdatePlantHeightText(targetPlantHeightCm);
            UpdateGrowthProgressText(targetGrowthProgress);
            ApplyGrowthByHeight(visualPlantHeightCm);
            SaveLastKnownHeight();
        }
    }

    private float ComputeLogisticGrowthProgress(double elapsedSeconds)
    {
        double totalSeconds = Math.Max(1.0, GetFullCycleDurationSeconds());

        double mMax = 1.0;
        double m0 = Mathf.Clamp(initialBiomass, 0.001f, 0.2f);
        double target = Mathf.Clamp(targetBiomassAtCycleEnd, 0.5f, 0.999f);
        double env = Mathf.Max(0.01f, environmentGrowthFactor);

        double a = (mMax - m0) / m0;
        double b = (mMax / target) - 1.0;
        double r = Math.Log(a / b) / totalSeconds;
        double rEffective = r * env;

        double biomass = mMax / (1.0 + a * Math.Exp(-rEffective * elapsedSeconds));
        double progress = (biomass - m0) / (target - m0);

        return Mathf.Clamp01((float)progress);
    }

    private double GetFullCycleDurationSeconds()
    {
        if (useDemoCycleSeconds)
        {
            return Math.Max(1.0, demoFullCycleSeconds);
        }

        return Math.Max(1.0, realFullCycleDays * 86400.0);
    }

    private void ApplyGrowthByHeight(float heightCm)
    {
        float safeHeight = Mathf.Clamp(heightCm, minPlantHeightCm, maxPlantHeightCm);
        int stage = GetStageFromHeight(safeHeight);
        float stageT = GetStageProgress(stage, safeHeight);

        HideAllStages();

        if (stage == 1)
        {
            ShowStage(stage1Seed);
        }
        else if (stage == 2)
        {
            ShowStage(stage2Sprout);
        }
        else if (stage == 3)
        {
            ShowStage(stage3Growing);
        }
        else
        {
            ShowStage(stage4Mature);
        }

        UpdateGrowLight(stage);
        UpdateGrowthUI(targetPlantHeightCm, safeHeight, stage, stageT);

        if (debugLog)
        {
            Debug.Log("[GROWTH STAGE] displayHeight="
                + targetPlantHeightCm.ToString("0.0")
                + " cm | visualHeight="
                + safeHeight.ToString("0.0")
                + " cm | currentStage="
                + stage);
        }
    }

    private int GetStageFromHeight(float heightCm)
    {
        if (heightCm <= stage1MaxHeightCm)
        {
            return 1;
        }

        if (heightCm <= stage2MaxHeightCm)
        {
            return 2;
        }

        if (heightCm <= stage3MaxHeightCm)
        {
            return 3;
        }

        return 4;
    }

    private float GetStageProgress(int stage, float heightCm)
    {
        if (stage == 1)
        {
            return Mathf.InverseLerp(0f, Mathf.Max(0.01f, stage1MaxHeightCm), heightCm);
        }

        if (stage == 2)
        {
            return Mathf.InverseLerp(stage1MaxHeightCm, Mathf.Max(stage1MaxHeightCm + 0.01f, stage2MaxHeightCm), heightCm);
        }

        if (stage == 3)
        {
            return Mathf.InverseLerp(stage2MaxHeightCm, Mathf.Max(stage2MaxHeightCm + 0.01f, stage3MaxHeightCm), heightCm);
        }

        return Mathf.InverseLerp(stage3MaxHeightCm, Mathf.Max(stage3MaxHeightCm + 0.01f, stage4MaxHeightCm), heightCm);
    }

    private void ShowStage(GameObject stageObject)
    {
        if (stageObject == null)
        {
            return;
        }

        stageObject.SetActive(true);
        ClearRendererOverrides(stageObject);
    }

    private void HideAllStages()
    {
        if (stage1Seed != null)
        {
            stage1Seed.SetActive(false);
        }

        if (stage2Sprout != null)
        {
            stage2Sprout.SetActive(false);
        }

        if (stage3Growing != null)
        {
            stage3Growing.SetActive(false);
        }

        if (stage4Mature != null)
        {
            stage4Mature.SetActive(false);
        }
    }

    private void ClearAllStageRendererOverrides()
    {
        ClearRendererOverrides(stage1Seed);
        ClearRendererOverrides(stage2Sprout);
        ClearRendererOverrides(stage3Growing);
        ClearRendererOverrides(stage4Mature);
    }

    private void ClearRendererOverrides(GameObject obj)
    {
        if (obj == null)
        {
            return;
        }

        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            // Chỉ xóa PropertyBlock override, KHÔNG can thiệp gameObject.SetActive / renderer.enabled
            renderer.SetPropertyBlock(null);

            Material[] mats = renderer.sharedMaterials;

            if (mats != null)
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    renderer.SetPropertyBlock(null, i);
                }
            }
        }
    }

    private void UpdateGrowLight(int stage)
    {
        if (growLightSource == null)
        {
            return;
        }

        if (autoGrowLightByGrowthStage)
        {
            growLightSource.SetActive(stage >= 3);
        }
    }

    private void UpdateGrowthUI(float displayHeightCm, float visualHeightCm, int stage, float stageT)
    {
        UpdatePlantHeightText(displayHeightCm);

        float shownProgress = Mathf.Clamp01(Mathf.InverseLerp(minPlantHeightCm, maxPlantHeightCm, displayHeightCm));

        if (growthProgressText != null)
        {
            growthProgressText.text = "Growth Progress: " + (shownProgress * 100f).ToString("0.0") + " %";
        }

        if (growthStageText != null)
        {
            growthStageText.text = "Growth Stage: " + stage;
        }

        if (growthDescriptionText != null)
        {
            if (stage == 1)
            {
                growthDescriptionText.text = "Stage 1: Gieo hạt";
            }
            else if (stage == 2)
            {
                growthDescriptionText.text = "Stage 2: Nảy mầm";
            }
            else if (stage == 3)
            {
                growthDescriptionText.text = "Stage 3: Cây phát triển";
            }
            else
            {
                growthDescriptionText.text = "Stage 4: Cây trưởng thành";
            }
        }
    }

    private void UpdatePlantHeightText(float heightCm)
    {
        if (plantHeightText != null)
        {
            plantHeightText.text = "Plant Height: " + heightCm.ToString("0.0") + " cm";
        }
    }

    private void UpdateGrowthProgressText(float progress)
    {
        if (growthProgressText != null)
        {
            growthProgressText.text = "Growth Progress: " + (Mathf.Clamp01(progress) * 100f).ToString("0.0") + " %";
        }
    }

    private void WarnIfMissingStageObjects()
    {
        if (warnedMissingStage)
        {
            return;
        }

        warnedMissingStage = true;

        if (stage1Seed == null)
        {
            Debug.LogWarning("[GROWTH] Stage 1 Seed is not assigned.");
        }

        if (stage2Sprout == null)
        {
            Debug.LogWarning("[GROWTH] Stage 2 Sprout is not assigned.");
        }

        if (stage3Growing == null)
        {
            Debug.LogWarning("[GROWTH] Stage 3 Growing is not assigned.");
        }

        if (stage4Mature == null)
        {
            Debug.LogWarning("[GROWTH] Stage 4 Mature is not assigned.");
        }
    }

    private void SaveLastKnownHeight()
    {
        PlayerPrefs.SetFloat(PlayerPrefsLastHeightKey, Mathf.Clamp(targetPlantHeightCm, minPlantHeightCm, maxPlantHeightCm));
        PlayerPrefs.SetFloat(PlayerPrefsLastProgressKey, Mathf.Clamp01(targetGrowthProgress));
        PlayerPrefs.Save();
    }

    private bool LoadLastKnownHeight()
    {
        if (!PlayerPrefs.HasKey(PlayerPrefsLastHeightKey))
        {
            return false;
        }

        float savedHeight = PlayerPrefs.GetFloat(PlayerPrefsLastHeightKey, 0f);
        float savedProgress = PlayerPrefs.GetFloat(PlayerPrefsLastProgressKey, 0f);

        savedHeight = Mathf.Clamp(savedHeight, minPlantHeightCm, maxPlantHeightCm);
        savedProgress = Mathf.Clamp01(savedProgress);

        targetPlantHeightCm = savedHeight;
        visualPlantHeightCm = savedHeight;

        targetGrowthProgress = savedProgress;
        visualGrowthProgress = savedProgress;

        return savedHeight > 0.01f;
    }

    private void ClearLastKnownHeight()
    {
        PlayerPrefs.DeleteKey(PlayerPrefsLastHeightKey);
        PlayerPrefs.DeleteKey(PlayerPrefsLastProgressKey);
        PlayerPrefs.Save();
    }

    private void SavePlantingStartToPlayerPrefs()
    {
        long unixMs = plantingStartUtc.ToUnixTimeMilliseconds();

        PlayerPrefs.SetString(PlayerPrefsStartUnixMsKey, unixMs.ToString());
        PlayerPrefs.SetString(PlayerPrefsStartIsoKey, plantingStartUtc.ToString("o"));
        PlayerPrefs.Save();
    }

    private bool LoadPlantingStartFromPlayerPrefs()
    {
        if (!PlayerPrefs.HasKey(PlayerPrefsStartUnixMsKey))
        {
            return false;
        }

        string unixMsText = PlayerPrefs.GetString(PlayerPrefsStartUnixMsKey, "");

        if (!long.TryParse(unixMsText, out long unixMs))
        {
            return false;
        }

        plantingStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToUniversalTime();
        hasPlantingStart = true;

        return true;
    }
}