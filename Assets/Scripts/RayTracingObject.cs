using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshFilter))]
public class RayTracingObject : MonoBehaviour
{
    private void OnEnable()
    {
        GameManager.RegisterObject(this);
    }

    private void OnDisable()
    {
        GameManager.UnregisterObject(this);
    }
}