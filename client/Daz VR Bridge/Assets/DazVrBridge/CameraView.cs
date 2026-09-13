// A Daz camera in VR: a body you can grab, a frustum showing what it frames,
// and a picture-in-picture panel rendering the VR scene through it at the
// camera's own focal length and aspect. Daz cameras look down local -Z; the
// Z mirror on import turns that into Unity's +Z, so a Camera component on the
// node with identity local rotation frames the same shot.

using UnityEngine;

namespace DazVrBridge
{
    public sealed class CameraView : MonoBehaviour
    {
        public float FocalMm { get; private set; } = 65f;
        public float FrameWidthMm { get; private set; } = 36f;
        public float Aspect { get; private set; } = 16f / 9f;

        public float pipWidth = 0.40f;      // meters
        public float pipHeightAbove = 0.30f;
        public int pipPixels = 640;

        Camera _cam;
        RenderTexture _rt;
        Transform _pip;
        LineRenderer _frustum;

        public void Init(float focalMm, float frameWidthMm, float aspect)
        {
            FocalMm = focalMm > 0 ? focalMm : 65f;
            FrameWidthMm = frameWidthMm > 0 ? frameWidthMm : 36f;
            Aspect = aspect > 0 ? aspect : 16f / 9f;

            // Body: a box the size of a small camera, the grab target.
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "body";
            body.transform.SetParent(transform, false);
            body.transform.localScale = new Vector3(0.10f, 0.08f, 0.06f);
            body.transform.localPosition = new Vector3(0f, 0f, -0.03f);
            var bodyCol = body.GetComponent<BoxCollider>();
            bodyCol.isTrigger = true;
            var br = body.GetComponent<Renderer>();
            br.sharedMaterial = BoneHandle.OverlayMaterial();
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.25f, 0.95f));
            br.SetPropertyBlock(block);

            // Frustum: a wireframe from the lens out to 1.5 m.
            _frustum = gameObject.AddComponent<LineRenderer>();
            _frustum.useWorldSpace = false;
            _frustum.widthMultiplier = 0.003f;
            _frustum.sharedMaterial = BoneHandle.OverlayMaterial();
            _frustum.loop = false;
            BuildFrustum();

            // Picture-in-picture: a Unity camera with the same physical lens, drawing
            // into a texture on a panel floating above the body.
            _rt = new RenderTexture(pipPixels, Mathf.RoundToInt(pipPixels / Aspect), 24) { name = "pip" };
            var camGo = new GameObject("pipCamera");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 200f;
            _cam.depth = -10;
            ApplyLens();

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "pip";
            Destroy(quad.GetComponent<Collider>());
            _pip = quad.transform;
            _pip.SetParent(transform, false);
            _pip.localScale = new Vector3(pipWidth, pipWidth / Aspect, 1f);
            _pip.localPosition = new Vector3(0f, pipHeightAbove, 0f);
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            var mat = new Material(shader) { name = "pip", mainTexture = _rt };
            quad.GetComponent<Renderer>().sharedMaterial = mat;
        }

        public void SetFocal(float focalMm)
        {
            FocalMm = focalMm;
            ApplyLens();
            BuildFrustum();
        }

        void ApplyLens()
        {
            if (!_cam) return;
            _cam.usePhysicalProperties = true;
            _cam.focalLength = FocalMm;
            _cam.sensorSize = new Vector2(FrameWidthMm, FrameWidthMm / Aspect);
            _cam.gateFit = Camera.GateFitMode.Horizontal;
        }

        void BuildFrustum()
        {
            var halfW = FrameWidthMm * 0.5f / FocalMm; // tan(hfov/2)
            var halfH = halfW / Aspect;
            const float near = 0.15f, far = 1.5f;
            Vector3 P(float d, float sx, float sy) => new Vector3(sx * d * halfW, sy * d * halfH, d);
            var pts = new[]
            {
                Vector3.zero, P(far, -1, 1), P(far, 1, 1), Vector3.zero, P(far, 1, -1), P(far, -1, -1), Vector3.zero,
                P(near, -1, 1), P(near, 1, 1), P(near, 1, -1), P(near, -1, -1), P(near, -1, 1),
                P(far, -1, 1), P(far, 1, 1), P(far, 1, -1), P(far, -1, -1), P(far, -1, 1),
            };
            _frustum.positionCount = pts.Length;
            _frustum.SetPositions(pts);
        }

        void LateUpdate()
        {
            // Keep the panel facing the viewer.
            var head = Camera.main;
            if (_pip && head) _pip.rotation = Quaternion.LookRotation(_pip.position - head.transform.position, Vector3.up);
        }

        void OnDestroy()
        {
            if (_rt) _rt.Release();
        }
    }
}
