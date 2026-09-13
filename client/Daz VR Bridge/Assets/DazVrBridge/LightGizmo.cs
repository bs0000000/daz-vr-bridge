// A Daz light in VR: a small overlay shape you can grab, plus a Unity light
// of matching kind so the clay preview is lit roughly like the Daz scene.
// Daz lights point down local -Z; after the Z mirror that is Unity's +Z.

using UnityEngine;

namespace DazVrBridge
{
    public sealed class LightGizmo : MonoBehaviour
    {
        public void Init(string kind, float intensity)
        {
            var mat = BoneHandle.OverlayMaterial();
            var color = new Color(1f, 0.95f, 0.6f, 0.9f);

            var body = GameObject.CreatePrimitive(kind == "point" ? PrimitiveType.Sphere : PrimitiveType.Cylinder);
            body.name = "body";
            body.transform.SetParent(transform, false);
            if (kind == "point")
            {
                body.transform.localScale = Vector3.one * 0.08f;
            }
            else
            {
                // Cylinder axis is Y; point it down +Z like the light.
                body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                body.transform.localScale = new Vector3(0.06f, 0.05f, 0.06f);
                body.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            }
            var col = body.GetComponent<Collider>();
            col.isTrigger = true;
            var r = body.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            r.SetPropertyBlock(block);

            // Direction line for spot/distant lights.
            if (kind != "point")
            {
                var line = gameObject.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.widthMultiplier = 0.004f;
                line.sharedMaterial = mat;
                line.positionCount = 2;
                line.SetPositions(new[] { Vector3.zero, new Vector3(0f, 0f, 0.5f) });
            }

            var light = gameObject.AddComponent<Light>();
            light.type = kind == "spot" ? LightType.Spot : kind == "point" ? LightType.Point : LightType.Directional;
            light.intensity = Mathf.Clamp(intensity, 0.2f, 3f);
            light.range = 10f;
            light.spotAngle = 45f;
            light.shadows = LightShadows.None;
        }
    }
}
