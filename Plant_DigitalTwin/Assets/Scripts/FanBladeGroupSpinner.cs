using UnityEngine;

public class FanPivotSpinner : MonoBehaviour
{
    [Header("Fan Spin")]
    public bool spinOnPlay = true;

    [Tooltip("Tốc độ quay của cánh quạt.")]
    public float rotationSpeed = 1200f;

    [Tooltip("Trục quay local của Fan_Blade_Pivot. Thử X/Y/Z nếu chưa đúng.")]
    public Vector3 localAxis = Vector3.forward;

    public bool reverseDirection = false;

    private void Update()
    {
        if (!spinOnPlay)
        {
            return;
        }

        Vector3 axis = localAxis;

        if (axis == Vector3.zero)
        {
            axis = Vector3.forward;
        }

        float direction = reverseDirection ? -1f : 1f;
        float angle = rotationSpeed * direction * Time.deltaTime;

        transform.Rotate(axis.normalized, angle, Space.Self);
    }

    public void SetSpin(bool value)
    {
        spinOnPlay = value;
    }

    public void SetSpeed(float speed)
    {
        rotationSpeed = Mathf.Max(0f, speed);
    }
}