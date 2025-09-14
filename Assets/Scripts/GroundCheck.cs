using UnityEngine;

public class GroundCheck : MonoBehaviour
{
    public bool isGrounded;

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Ground"))
            isGrounded = true;
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Ground"))
            isGrounded = false;
    }
}