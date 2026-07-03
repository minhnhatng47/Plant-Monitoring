using TMPro;
using UnityEngine;

public class FirstPersonInteractor : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 3f;
    public float mouseSensitivity = 2f;
    public float gravity = -9.81f;

    [Header("Camera")]
    public Camera playerCamera;

    [Header("Interaction")]
    public float interactDistance = 4f;
    public LayerMask interactMask = ~0;
    public TMP_Text interactionPromptText;

    [Header("Crosshair UI")]
    public TMP_Text crosshairDotText;
    public Color normalCrosshairColor = Color.white;
    public Color interactCrosshairColor = Color.yellow;
    public float normalCrosshairSize = 24f;
    public float interactCrosshairSize = 34f;

    private float cameraPitch = 0f;
    private InteractableObject currentInteractable;

    private void Start()
    {
        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>();
        }

        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }

        if (interactionPromptText != null)
        {
            interactionPromptText.gameObject.SetActive(false);
        }

        SetCrosshair(false);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        Debug.Log("[FPS] FirstPersonInteractor started. WASD movement uses Transform, not CharacterController.");
    }

    private void Update()
    {
        HandleCursor();
        HandleMouseLook();
        HandleMovement();
        HandleInteractionRaycast();
        HandleInteractionInput();
    }

    private void HandleCursor()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void HandleMouseLook()
    {
        if (playerCamera == null) return;

        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        transform.Rotate(Vector3.up * mouseX);

        cameraPitch -= mouseY;
        cameraPitch = Mathf.Clamp(cameraPitch, -80f, 80f);

        playerCamera.transform.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
    }

    private void HandleMovement()
    {
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;

        forward.y = 0f;
        right.y = 0f;

        forward.Normalize();
        right.Normalize();

        Vector3 move = Vector3.zero;

        if (Input.GetKey(KeyCode.W))
        {
            move += forward;
        }

        if (Input.GetKey(KeyCode.S))
        {
            move -= forward;
        }

        if (Input.GetKey(KeyCode.A))
        {
            move -= right;
        }

        if (Input.GetKey(KeyCode.D))
        {
            move += right;
        }

        if (move.magnitude > 1f)
        {
            move.Normalize();
        }

        transform.position += move * moveSpeed * Time.deltaTime;
    }

    private void HandleInteractionRaycast()
    {
        if (playerCamera == null) return;

        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);

        InteractableObject detectedInteractable = null;

        if (Physics.Raycast(ray, out RaycastHit hit, interactDistance, interactMask))
        {
            detectedInteractable = hit.collider.GetComponentInParent<InteractableObject>();
        }

        if (detectedInteractable != currentInteractable)
        {
            if (currentInteractable != null)
            {
                currentInteractable.SendMessage("SetHighlighted", false, SendMessageOptions.DontRequireReceiver);
            }

            currentInteractable = detectedInteractable;

            if (currentInteractable != null)
            {
                currentInteractable.SendMessage("SetHighlighted", true, SendMessageOptions.DontRequireReceiver);
            }
        }

        UpdatePrompt();
        SetCrosshair(currentInteractable != null);
    }

    private void HandleInteractionInput()
    {
        if (currentInteractable == null) return;

        if (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0))
        {
            Debug.Log("[INTERACT] " + currentInteractable.gameObject.name);
            currentInteractable.SendMessage("Interact", SendMessageOptions.DontRequireReceiver);
        }
    }

    private void UpdatePrompt()
    {
        if (interactionPromptText == null) return;

        if (currentInteractable == null)
        {
            interactionPromptText.gameObject.SetActive(false);
            return;
        }

        interactionPromptText.gameObject.SetActive(true);
        interactionPromptText.text = currentInteractable.promptText;
    }

    private void SetCrosshair(bool canInteract)
    {
        if (crosshairDotText == null) return;

        crosshairDotText.color = canInteract ? interactCrosshairColor : normalCrosshairColor;
        crosshairDotText.fontSize = canInteract ? interactCrosshairSize : normalCrosshairSize;
    }
}