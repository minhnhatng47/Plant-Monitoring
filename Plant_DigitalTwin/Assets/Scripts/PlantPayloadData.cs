using System;

[Serializable]
public class SensorData
{
    public float temperature;
    public float air_humidity;
    public float lux;
    public float soil_moisture;
}

[Serializable]
public class AIData
{
    public int need_watering;
    public string action;
}

[Serializable]
public class PumpData
{
    public string command;
    public string status;
}

[Serializable]
public class LightData
{
    public string status;
}

[Serializable]
public class PlantPayload
{
    public string node_id;
    public string timestamp;
    public SensorData sensor;
    public AIData ai;
    public PumpData pump;
    public LightData light;
    public string alert;
}