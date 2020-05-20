using UnityEngine;

// [RequireComponent(typeof(MeshRenderer))]
// [RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(SphereCollider))]
public class RayTracingObject : MonoBehaviour
{
    private void OnEnable()
    {
        GetComponentInParent<GameManager>().RegisterObject(this);
    }

    private void OnDisable()
    {
        var gameManager = GetComponentInParent<GameManager>();
        if (gameManager != null)
        {
            gameManager.UnregisterObject(this);
        }
    }
}