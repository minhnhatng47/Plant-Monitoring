using UnityEngine;

public class UnityActuatorVisualController : MonoBehaviour
{
    [Header("Pump / Water Effect")]
    public ParticleSystem waterParticle;
    public AudioSource waterSound;

    [Header("Light Effect")]
    public GameObject growLightObject;
    public Light growLightSource;
    public Renderer growLightRenderer;
    public Material lightOnMaterial;
    public Material lightOffMaterial;

    [Header("Optional Pump Model")]
    public Transform pumpRotor;
    public float rotorSpeed = 360f;

    [Header("Debug")]
    public bool debugLog = true;

    private bool pumpOn = false;
    private bool lightOn = false;

    private void Awake()
    {
        ApplyPumpState(false);
        ApplyLightState(false);
    }

    private void Update()
    {
        if (pumpOn && pumpRotor != null)
        {
            pumpRotor.Rotate(Vector3.forward, rotorSpeed * Time.deltaTime);
        }
    }

    public void ApplyActuatorState(string pumpState, string lightState)
    {
        if (IsKnownState(pumpState))
        {
            ApplyPumpState(IsOn(pumpState));
        }

        if (IsKnownState(lightState))
        {
            ApplyLightState(IsOn(lightState));
        }
    }

    public void ApplyPumpStateFromString(string state)
    {
        if (!IsKnownState(state))
        {
            return;
        }

        ApplyPumpState(IsOn(state));
    }

    public void ApplyLightStateFromString(string state)
    {
        if (!IsKnownState(state))
        {
            return;
        }

        ApplyLightState(IsOn(state));
    }

    public void ApplyPumpState(bool state)
    {
        pumpOn = state;

        if (waterParticle != null)
        {
            if (state)
            {
                if (!waterParticle.isPlaying)
                {
                    waterParticle.Play(true);
                }
            }
            else
            {
                waterParticle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        if (waterSound != null)
        {
            if (state)
            {
                if (!waterSound.isPlaying)
                {
                    waterSound.Play();
                }
            }
            else
            {
                waterSound.Stop();
            }
        }

        if (debugLog)
        {
            Debug.Log("[MODEL] Pump visual = " + (state ? "ON" : "OFF"));
        }
    }

    public void ApplyLightState(bool state)
    {
        lightOn = state;

        if (growLightObject != null)
        {
            growLightObject.SetActive(state);
        }

        if (growLightSource != null)
        {
            growLightSource.enabled = state;
        }

        if (growLightRenderer != null)
        {
            if (state && lightOnMaterial != null)
            {
                growLightRenderer.material = lightOnMaterial;
            }
            else if (!state && lightOffMaterial != null)
            {
                growLightRenderer.material = lightOffMaterial;
            }
        }

        if (debugLog)
        {
            Debug.Log("[MODEL] Light visual = " + (state ? "ON" : "OFF"));
        }
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

    private bool IsOn(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().ToUpperInvariant();

        return value == "ON"
            || value == "1"
            || value == "TRUE"
            || value == "PUMP_ON"
            || value == "LIGHT_ON";
    }
}