// Grab-and-move for a whole Daz node: prop, camera or light. The node follows
// the hand rigidly (position and rotation) while held; release commits it to
// Daz as node.transform / camera.set. A small overlay marker shows on hover.

using UnityEngine;

namespace DazVrBridge
{
    public sealed class NodeHandle : MonoBehaviour, IGrabbable
    {
        public SceneLoader.LoadedNode Node;
        public Collider GrabCollider;
        [Tooltip("Added to the surface distance so bone handles win when both are in reach.")]
        public float pickBias = 0.03f;

        public bool IsGrabbed { get; private set; }

        Vector3 _offsetPos;
        Quaternion _offsetRot;
        GameObject _marker;
        static NodeSync _nodeSync;

        public void Init(SceneLoader.LoadedNode node, Collider grabCollider)
        {
            Node = node;
            GrabCollider = grabCollider;

            _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _marker.name = "hover";
            Destroy(_marker.GetComponent<Collider>());
            _marker.transform.SetParent(transform, false);
            _marker.transform.localScale = Vector3.one * 0.05f;
            var r = _marker.GetComponent<Renderer>();
            r.sharedMaterial = BoneHandle.OverlayMaterial();
            _marker.SetActive(false);
        }

        public float DistanceTo(Vector3 world)
        {
            if (!GrabCollider) return Vector3.Distance(world, transform.position);
            return Vector3.Distance(world, GrabCollider.ClosestPoint(world)) + pickBias;
        }

        public void SetHover(bool on)
        {
            if (IsGrabbed) return;
            Tint(on ? new Color(1f, 0.85f, 0.2f, 0.9f) : Color.clear);
        }

        public void BeginGrab(Transform hand)
        {
            IsGrabbed = true;
            _offsetPos = Quaternion.Inverse(hand.rotation) * (transform.position - hand.position);
            _offsetRot = Quaternion.Inverse(hand.rotation) * transform.rotation;
            Tint(new Color(0.3f, 1f, 0.4f, 0.9f));
            if (!_nodeSync) _nodeSync = FindAnyObjectByType<NodeSync>();
            _nodeSync?.SetGrabbed(Node.Id, true);
        }

        public void UpdateGrab(Transform hand)
        {
            transform.rotation = hand.rotation * _offsetRot;
            transform.position = hand.position + hand.rotation * _offsetPos;
        }

        public void EndGrab(Transform hand)
        {
            IsGrabbed = false;
            Tint(Color.clear);
            _nodeSync?.SetGrabbed(Node.Id, false);
            _nodeSync?.Commit(Node, $"VR move: {Node.Label}");
        }

        void Tint(Color c)
        {
            if (!_marker) return;
            _marker.SetActive(c.a > 0.01f);
            if (!_marker.activeSelf) return;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            _marker.GetComponent<Renderer>().SetPropertyBlock(block);
        }
    }
}
