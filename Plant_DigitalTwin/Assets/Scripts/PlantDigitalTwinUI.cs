using UnityEngine;
using TMPro;

public class PlantDigitalTwinUI : MonoBehaviour
{
    [Header("UI Text")]
    public TMP_Text temperatureText;
    public TMP_Text airHumidityText;
    public TMP_Text luxText;
    public TMP_Text soilMoistureText;
    public TMP_Text needWateringText;
    public TMP_Text pumpStatusText;
    public TMP_Text alertText;

    [Header("Controlled Objects")]
    public GameObject waterEffect;       // WaterParticle hoặc Water_Effect
    public GameObject growLightSource;   // Grow_Light_Source
    public GameObject plantObject;       // Có thể để trống nếu chưa muốn scale cây

    [Header("Plant Scale Settings")]
    public float drySoilThreshold = 35.0f;
    public Vector3 normalPlantScale = new Vector3(1.0f, 1.0f, 1.0f);
    public Vector3 dryPlantScale = new Vector3(0.8f, 0.8f, 0.8f);

    [Header("UI Colors")]
    public Color normalTextColor = Color.white;
    public Color titleGreenColor = new Color(0.0f, 1.0f, 0.45f);      // xanh lá sáng
    public Color warningYellowColor = new Color(1.0f, 0.9f, 0.0f);   // vàng
    public Color pumpOnGreenColor = new Color(0.0f, 1.0f, 0.3f);      // xanh lá
    public Color alertOrangeColor = new Color(1.0f, 0.45f, 0.1f);    // cam

    void Start()
    {
        // Không cập nhật dữ liệu giả ở đây.
        // Dữ liệu sẽ được cập nhật từ PlantDataJsonTest hoặc MQTT sau này.
    }

    public void UpdateDigitalTwin(
        float temperature,
        float airHumidity,
        float lux,
        float soilMoisture,
        int needWatering,
        string pumpStatus,
        string lightStatus,
        string alert
    )
    {
        // Chuẩn hóa chuỗi trạng thái để tránh lỗi do viết thường/viết hoa
        pumpStatus = NormalizeStatus(pumpStatus);
        lightStatus = NormalizeStatus(lightStatus);

        // =========================
        // 1. Cập nhật dữ liệu cảm biến
        // =========================

        if (temperatureText != null)
        {
            temperatureText.text = "Temperature: " + temperature.ToString("F1") + " °C";
            temperatureText.color = normalTextColor;
        }

        if (airHumidityText != null)
        {
            airHumidityText.text = "Air Humidity: " + airHumidity.ToString("F1") + " %";
            airHumidityText.color = normalTextColor;
        }

        if (luxText != null)
        {
            luxText.text = "Lux: " + lux.ToString("F0");
            luxText.color = normalTextColor;
        }

        if (soilMoistureText != null)
        {
            soilMoistureText.text = "Soil Moisture: " + soilMoisture.ToString("F1") + " %";

            // Nếu đất khô thì đổi dòng Soil Moisture sang màu vàng
            if (soilMoisture < drySoilThreshold)
            {
                soilMoistureText.color = warningYellowColor;
            }
            else
            {
                soilMoistureText.color = normalTextColor;
            }
        }

        // =========================
        // 2. Cập nhật trạng thái cần tưới
        // =========================

        if (needWateringText != null)
        {
            needWateringText.text = "Need Watering: " + needWatering;

            if (needWatering == 1)
            {
                needWateringText.color = warningYellowColor;
            }
            else
            {
                needWateringText.color = normalTextColor;
            }
        }

        // =========================
        // 3. Cập nhật trạng thái bơm
        // =========================

        if (pumpStatusText != null)
        {
            pumpStatusText.text = "Pump Status: " + pumpStatus;

            if (pumpStatus == "ON")
            {
                pumpStatusText.color = pumpOnGreenColor;
            }
            else
            {
                pumpStatusText.color = normalTextColor;
            }
        }

        // =========================
        // 4. Cập nhật cảnh báo
        // =========================

        if (alertText != null)
        {
            alertText.text = "Alert: " + alert;

            if (needWatering == 1 || soilMoisture < drySoilThreshold)
            {
                alertText.color = alertOrangeColor;
            }
            else
            {
                alertText.color = normalTextColor;
            }
        }

        // =========================
        // 5. Bật/tắt hiệu ứng nước
        // =========================

        if (waterEffect != null)
        {
            waterEffect.SetActive(pumpStatus == "ON");
        }

        // =========================
        // 6. Bật/tắt nguồn sáng
        // =========================

        if (growLightSource != null)
        {
            growLightSource.SetActive(lightStatus == "ON");
        }

        // =========================
        // 7. Mô phỏng cây nhỏ/héo khi đất khô
        // =========================
        // Nếu chưa muốn scale cây thì để Plant Object trống trong Inspector.

        if (plantObject != null)
        {
            if (soilMoisture < drySoilThreshold)
            {
                plantObject.transform.localScale = dryPlantScale;
            }
            else
            {
                plantObject.transform.localScale = normalPlantScale;
            }
        }
    }
    public void SetPumpStatusLocal(string pumpStatus)
{
    pumpStatus = NormalizeStatus(pumpStatus);

    if (pumpStatusText != null)
    {
        pumpStatusText.text = "Pump Status: " + pumpStatus;

        if (pumpStatus == "ON")
        {
            pumpStatusText.color = pumpOnGreenColor;
        }
        else
        {
            pumpStatusText.color = normalTextColor;
        }
    }

    if (waterEffect != null)
    {
        waterEffect.SetActive(pumpStatus == "ON");
    }
}

    private string NormalizeStatus(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return "OFF";
        }

        return status.Trim().ToUpper();
    }
}