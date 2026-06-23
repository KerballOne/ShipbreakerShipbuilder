using UnityEngine;

/// Marks a light GO with an authored color for the BayLights mod to apply at runtime.
/// Add this component to the scene GO that parents the light (e.g. PRF_Light_LowSodium_Science).
/// The mod finds it via GetComponentInChildren and applies both light.color and the emissive MPB.
public class BayLightsColor : MonoBehaviour
{
    public Color color = Color.white;
}
