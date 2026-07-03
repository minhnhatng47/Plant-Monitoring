using UnityEngine;

public class SimpleWASDTest : MonoBehaviour
{
    public float speed = 3f;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.W))
            Debug.Log("[INPUT TEST] W pressed");

        if (Input.GetKeyDown(KeyCode.A))
            Debug.Log("[INPUT TEST] A pressed");

        if (Input.GetKeyDown(KeyCode.S))
            Debug.Log("[INPUT TEST] S pressed");

        if (Input.GetKeyDown(KeyCode.D))
            Debug.Log("[INPUT TEST] D pressed");

        Vector3 move = Vector3.zero;

        if (Input.GetKey(KeyCode.W))
            move += Vector3.forward;

        if (Input.GetKey(KeyCode.S))
            move += Vector3.back;

        if (Input.GetKey(KeyCode.A))
            move += Vector3.left;

        if (Input.GetKey(KeyCode.D))
            move += Vector3.right;

        transform.position += move * speed * Time.deltaTime;
    }
}