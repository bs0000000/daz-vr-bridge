// A Daz camera in VR: a body you can grab, a frustum showing what it frames,
// and a picture-in-picture panel rendering the VR scene through it at the
// camera's own focal length and aspect. Daz cameras look down local -Z; the
// Z mirror on import turns that into Unity's +Z, so a Camera component on the
// node with identity local rotation frames the same shot.

using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DazVrBridge
{
    public sealed class CameraView : MonoBehaviour
    {
        public float FocalMm { get; private set; } = 65f;
        public float FrameWidthMm { get; private set; } = 36f;
        public float Aspect { get; private set; } = 16f / 9f;
        // Daz's getFieldOfView() is 2*atan(frame_width/(2*focal)) in radians, independent
        // of the render aspect. Measured against Daz's viewport at 65 mm / 1.667: the
        // head fills ~31° vertically, so that angle is the VERTICAL field of view and
        // the horizontal one follows from the aspect. (Portrait renders unverified.)
        public bool fovIsHorizontal = false;
        public float VerticalFovDeg { get; private set; } = 30f;

        public float pipWidth = 0.40f;      // meters
        public float pipHeightAbove = 0.30f;
        public int pipPixels = 640;

        Camera _cam;
        RenderTexture _rt;
        Transform _pip;
        LineRenderer _frustum;

        public void Init(float focalMm, float frameWidthMm, float aspect, float? dazFov)
        {
            FocalMm = focalMm > 0 ? focalMm : 65f;
            FrameWidthMm = frameWidthMm > 0 ? frameWidthMm : 36f;
            Aspect = aspect > 0 ? aspect : 16f / 9f;
            VerticalFovDeg = ResolveFov(dazFov);

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
            _cam.cullingMask &= ~(1 << 5); // session menu belongs only in the headset
            // URP has no stereoTargetEye; XR rendering is switched off per camera on its
            // URP data, so this renders one flat image while OpenXR drives the main camera.
            var urp = _cam.GetUniversalAdditionalCameraData();
            if (urp) urp.allowXRRendering = false;
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

        public void SetLens(float focalMm, float? frameWidthMm, float? aspect, float? dazFov)
        {
            if (focalMm > 0) FocalMm = focalMm;
            if (frameWidthMm.HasValue && frameWidthMm > 0) FrameWidthMm = frameWidthMm.Value;
            if (aspect.HasValue && aspect > 0)
            {
                var changed = !Mathf.Approximately(Aspect, aspect.Value);
                Aspect = aspect.Value;
                if (changed) RebuildTarget();
            }
            VerticalFovDeg = ResolveFov(dazFov); // after Aspect: the conversion depends on it
            ApplyLens();
            BuildFrustum();
        }

        float ResolveFov(float? dazFov)
        {
            float deg;
            if (dazFov.HasValue && dazFov.Value > 0f)
            {
                var v = dazFov.Value;
                deg = v < 3.2f ? v * Mathf.Rad2Deg : v; // radians if it cannot be degrees
            }
            else
            {
                deg = 2f * Mathf.Atan(FrameWidthMm * 0.5f / FocalMm) * Mathf.Rad2Deg;
            }
            if (!fovIsHorizontal) return deg;
            // horizontal -> vertical through the render aspect
            return 2f * Mathf.Atan(Mathf.Tan(deg * 0.5f * Mathf.Deg2Rad) / Aspect) * Mathf.Rad2Deg;
        }

        void ApplyLens()
        {
            if (!_cam) return;
            _cam.usePhysicalProperties = false;
            _cam.fieldOfView = VerticalFovDeg;
        }

        void RebuildTarget()
        {
            if (!_cam || !_rt) return;
            _rt.Release();
            _rt = new RenderTexture(pipPixels, Mathf.Max(1, Mathf.RoundToInt(pipPixels / Aspect)), 24) { name = "pip" };
            _cam.targetTexture = _rt;
            _cam.cullingMask &= ~(1 << 5); // session menu belongs only in the headset
            if (_pip)
            {
                _pip.localScale = new Vector3(pipWidth, pipWidth / Aspect, 1f);
                _pip.GetComponent<Renderer>().sharedMaterial.mainTexture = _rt;
            }
        }

        void BuildFrustum()
        {
            var halfH = Mathf.Tan(VerticalFovDeg * 0.5f * Mathf.Deg2Rad);
            var halfW = halfH * Aspect;
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

        // Orient the camera node from Daz's own view geometry: forward = focal point - position,
        // up = the Y axis of the camera's world rotation as Daz computes it. Independent of any
        // assumption about which local axis a Daz camera looks along.
        public static void OrientFromDaz(Transform camNode, Transform root, JToken lens, JToken position)
        {
            var fp = lens["focal_point"];
            var axes = lens["axes"];
            if (fp == null || axes == null || position == null) return;
            var forward = DazSpace.Dir(fp) - DazSpace.Dir(position); // both world Daz -> Unity dir space
            if (forward.sqrMagnitude < 1e-8f) return;
            var up = DazSpace.Dir(axes["y"]);
            camNode.rotation = root.rotation * Quaternion.LookRotation(forward.normalized, up.normalized);
        }

        // Where a world point lands in this camera's frame: (0,0) bottom-left, (1,1) top-right,
        // z = distance in front (negative = behind).
        public Vector3 FramePoint(Vector3 world)
        {
            return _cam ? _cam.WorldToViewportPoint(world) : Vector3.zero;
        }

        public string Describe()
        {
            var f = transform.forward;
            var pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            return $"pos={transform.position:F3} forward={f:F3} pitch={pitch:F1}° vFOV={VerticalFovDeg:F1}° aspect={Aspect:F3}";
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
