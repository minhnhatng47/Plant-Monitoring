using UnityEngine;

public class WaterEffectController : MonoBehaviour
{
    [Header("Water Particle")]
    public ParticleSystem waterParticle;

    private void Start()
    {
        StopWater();
    }

    public void PlayWater()
    {
        if (waterParticle == null)
        {
            Debug.LogWarning("[WATER] Chưa gắn WaterParticle vào WaterEffectController.");
            return;
        }

        waterParticle.gameObject.SetActive(true);
        waterParticle.Clear();
        waterParticle.Play();

        Debug.Log("[WATER] Play water effect");
    }

    public void StopWater()
    {
        if (waterParticle == null)
        {
            return;
        }

        waterParticle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        waterParticle.Clear();
        waterParticle.gameObject.SetActive(false);

        Debug.Log("[WATER] Stop water effect");
    }
}