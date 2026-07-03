using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class InteractableObject : MonoBehaviour
{
    [Header("Interaction")]
    public string promptText = "Press E to interact";

    [Header("Action")]
    public UnityEvent onInteract;

    [Header("Visual Feedback")]
    public Color normalColor = Color.white;
    public Color hoverColor = Color.yellow;
    public Color pressedColor = Color.green;

    public float hoverScaleMultiplier = 1.08f;
    public float pressMoveDistance = 0.05f;
    public float pressDuration = 0.12f;

    [Header("Optional 3D Label")]
    public TMP_Text worldLabel;

    private Renderer[] renderers;
    private MaterialPropertyBlock propertyBlock;

    private Vector3 originalScale;
    private Vector3 originalLocalPosition;

    private bool isHighlighted = false;
    private bool isPressing = false;

    void Awake()
    {
        // CHỈ lấy Renderer trên chính GameObject này, KHÔNG dùng GetComponentsInChildren
        // để tránh override màu của các object con như Plant_Stage_1..4
        renderers = GetComponents<Renderer>();
        propertyBlock = new MaterialPropertyBlock();

        originalScale = transform.localScale;
        originalLocalPosition = transform.localPosition;

        ApplyColor(normalColor);

        if (worldLabel != null)
        {
            worldLabel.gameObject.SetActive(true);
        }
    }

    public void SetHighlighted(bool highlighted)
    {
        if (isPressing)
        {
            return;
        }

        isHighlighted = highlighted;

        if (highlighted)
        {
            transform.localScale = originalScale * hoverScaleMultiplier;
            ApplyColor(hoverColor);
        }
        else
        {
            transform.localScale = originalScale;
            ApplyColor(normalColor);
        }
    }

    public void Interact()
    {
        Debug.Log("[INTERACT] " + gameObject.name);

        if (onInteract != null)
        {
            onInteract.Invoke();
        }

        if (!isPressing)
        {
            StartCoroutine(PressEffect());
        }
    }

    private IEnumerator PressEffect()
    {
        isPressing = true;

        transform.localScale = originalScale;
        ApplyColor(pressedColor);

        Vector3 pressedPosition = originalLocalPosition - new Vector3(0f, pressMoveDistance, 0f);
        transform.localPosition = pressedPosition;

        yield return new WaitForSeconds(pressDuration);

        transform.localPosition = originalLocalPosition;

        if (isHighlighted)
        {
            transform.localScale = originalScale * hoverScaleMultiplier;
            ApplyColor(hoverColor);
        }
        else
        {
            transform.localScale = originalScale;
            ApplyColor(normalColor);
        }

        isPressing = false;
    }

    private void ApplyColor(Color color)
    {
        if (renderers == null)
        {
            return;
        }

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            renderer.GetPropertyBlock(propertyBlock);

            propertyBlock.SetColor("_Color", color);
            propertyBlock.SetColor("_BaseColor", color);

            renderer.SetPropertyBlock(propertyBlock);
        }
    }
}