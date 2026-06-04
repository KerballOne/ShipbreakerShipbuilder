using UnityEngine;

/// Stores the original addressable part name on a baked prefab so tools can
/// look up JSA and other metadata without relying on the GameObject's scene name.
public class BakedPartTag : MonoBehaviour
{
    public string sourcePartName;
}
