using UnityEngine;

public class BenchmarkOrbitMover : MonoBehaviour
{
    public Vector3 center;
    public float radius = 4.0f;
    public float angularSpeed = 15.0f;
    public float phaseDegrees;
    public float verticalAmplitude = 0.25f;
    public float verticalSpeed = 1.3f;

    private float _baseY;

    private void Start()
    {
        _baseY = transform.position.y;
    }

    private void Update()
    {
        float angle = (phaseDegrees + Time.time * angularSpeed) * Mathf.Deg2Rad;
        transform.position = new Vector3(
            center.x + Mathf.Cos(angle) * radius,
            _baseY + Mathf.Sin(Time.time * verticalSpeed + phaseDegrees * Mathf.Deg2Rad) * verticalAmplitude,
            center.z + Mathf.Sin(angle) * radius);
    }
}
