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

    private void OnDrawGizmos()
    {
        var sphereCollider = GetComponent<SphereCollider>();
        if (sphereCollider == null)
        {
            return;
        }

        var rayLight = GetComponent<RayLight>();
        if (rayLight != null)
        {
            DrawSphereGizmo(rayLight.Color, 1.0f, sphereCollider, true);
            return;
        }

        var rayMaterial = GetComponent<RayMaterial>();
        if (rayMaterial != null)
        {
            DrawSphereGizmo(rayMaterial.Color, rayMaterial.Opacity, sphereCollider, false);
        }
    }

    private void DrawSphereGizmo(Color color, float opacity, SphereCollider sphereCollider, bool isLight)
    {
        var position = transform.TransformPoint(sphereCollider.center);
        float radius = sphereCollider.radius * GetLargestAxisScale(transform.lossyScale);
        opacity = Mathf.Clamp01(opacity);

        color.a = opacity;
        Gizmos.color = color;
        Gizmos.DrawSphere(position, radius);

        color.a = opacity;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(position, radius);

        if (isLight)
        {
            Gizmos.DrawWireSphere(position, radius * 1.35f);
        }
    }

    private static float GetLargestAxisScale(Vector3 scale)
    {
        return Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
    }
}
