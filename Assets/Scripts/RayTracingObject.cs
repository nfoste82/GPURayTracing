using UnityEngine;

public class RayTracingObject : MonoBehaviour
{
    private void OnEnable()
    {
        var meshPrimitive = GetComponent<RayMeshPrimitive>();
        if (meshPrimitive != null)
        {
            meshPrimitive.EnsureMesh();
        }

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
