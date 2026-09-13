// A grab point on a bone. BoneHandles spawns one per grabbable bone; VrHand
// finds them by overlap and drives the bone through them.

using UnityEngine;

namespace DazVrBridge
{
    public sealed class BoneHandle : MonoBehaviour
    {
        public enum State { Idle, Hover, Grabbed }

        public SceneLoader.LoadedFigure Figure;
        public int BoneIndex;
        public string BoneId;
        public Transform Bone => Figure.Bones[BoneIndex];

        public State Current { get; private set; } = State.Idle;

        Renderer _renderer;
        static readonly Color IdleColor = new Color(0.55f, 0.65f, 0.85f, 1f);
        static readonly Color HoverColor = new Color(1.0f, 0.85f, 0.2f, 1f);
        static readonly Color GrabbedColor = new Color(0.3f, 1.0f, 0.4f, 1f);

        public void Init(SceneLoader.LoadedFigure figure, int boneIndex, float radius)
        {
            Figure = figure;
            BoneIndex = boneIndex;
            BoneId = figure.BoneJson[boneIndex].Value<string>("id");

            var col = gameObject.AddComponent<SphereCollider>();
            col.radius = radius;
            col.isTrigger = true;

            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "vis";
            Destroy(vis.GetComponent<Collider>());
            vis.transform.SetParent(transform, false);
            vis.transform.localScale = Vector3.one * radius * 2f; // primitive sphere scale is its diameter
            _renderer = vis.GetComponent<Renderer>();
            _renderer.sharedMaterial = HandleMaterial();
            SetState(State.Idle);
        }

        public void SetState(State s)
        {
            Current = s;
            if (!_renderer) return;
            var block = new MaterialPropertyBlock();
            var c = s == State.Grabbed ? GrabbedColor : s == State.Hover ? HoverColor : IdleColor;
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            _renderer.SetPropertyBlock(block);
        }

        static Material _handleMaterial;
        static Material HandleMaterial()
        {
            if (_handleMaterial) return _handleMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _handleMaterial = new Material(shader) { name = "BoneHandle" };
            return _handleMaterial;
        }
    }
}
