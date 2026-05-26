using UnityEngine;

public class FakeStructurePart : MonoBehaviour
{
    public enum JointType { Root, Standard, CutPoint }

    public JointType type = JointType.Standard;
    public Bounds localColliderBounds;
}
