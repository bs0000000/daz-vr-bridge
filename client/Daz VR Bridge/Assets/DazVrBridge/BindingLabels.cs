// What each button does, right now, floating beside the controller that has it.
//
// This is the tutorial, and the reason it is not one. A scripted walkthrough teaches the
// bindings once, goes stale the moment one changes, and is never replayed by the person
// who has forgotten a control six weeks later. These are generated from the same state
// the bindings read, so they cannot drift, they are there at the moment the question is
// actually asked, and the question people have is never "how does this work" -- it is
// "what does this button do *here*", which changes with what the hand is holding.
//
// Off by default. Reading while posing is a nuisance; reading while learning is not.

using System.Text;
using TMPro;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BindingLabels : MonoBehaviour
    {
        /// Toggled from the wheel. Static so the wheel does not need to find the instance.
        public static bool Show;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { Show = false; }

        [Tooltip("Metres out from the controller, at life size.")]
        public float offset = 0.09f;

        VrRig _rig;
        PoseTakes _takes;
        readonly TextMeshPro[] _text = new TextMeshPro[2];
        readonly StringBuilder _builder = new StringBuilder(160);
        readonly string[] _last = { "", "" };

        void LateUpdate()
        {
            if (!Show)
            {
                foreach (var t in _text) if (t) t.gameObject.SetActive(false);
                return;
            }
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();
            if (!_rig) return;
            if (!_takes) _takes = FindAnyObjectByType<PoseTakes>();

            Place(0, _rig.Left);
            Place(1, _rig.Right);
        }

        void Place(int index, VrHand hand)
        {
            if (!hand || !hand.IsTracked)
            {
                if (_text[index]) _text[index].gameObject.SetActive(false);
                return;
            }
            var head = Camera.main;
            if (!head) return;

            if (!_text[index]) _text[index] = Build($"bindings {index}");
            var label = _text[index];

            var scale = hand.transform.lossyScale.x;
            // Outward from the body, so the two labels do not overlap in the middle.
            var side = hand.side == VrHand.Side.Left ? -1f : 1f;
            var at = hand.transform.position
                   + head.transform.right * (side * offset * scale)
                   + head.transform.up * (0.03f * scale);
            label.transform.SetPositionAndRotation(at, Quaternion.LookRotation(at - head.transform.position, head.transform.up));
            label.transform.localScale = Vector3.one * scale;
            label.alignment = hand.side == VrHand.Side.Left ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
            label.gameObject.SetActive(true);

            var text = Describe(hand);
            if (_last[index] != text) { _last[index] = text; label.text = text; }
        }

        // The bindings as they stand in this moment. Every line here is read from the same
        // state the input path reads, so a binding that changes changes this with it.
        string Describe(VrHand hand)
        {
            _builder.Clear();
            var holding = hand.HoldingSomething;
            var menu = VrHand.UiBlocked;
            var right = hand.side == VrHand.Side.Right;

            if (menu)
            {
                Line("trigger", right ? "apply, stay open" : "-");
                Line(right ? "B" : "Y", right ? "release to apply" : "-");
                Line("move", right ? "choose; push out to set a value" : "-");
                return _builder.ToString();
            }

            Line("trigger", holding ? "holding - release to place" : "grab a bone or object");
            Line("grip", "move the world; both hands to turn and scale");

            if (holding)
            {
                Line(right ? "A" : "X", "pass through props and bodies");
            }
            else if (right)
            {
                Line("B", "hold for the wheel");
                Line("A", "-");
            }
            else
            {
                Line("Y", "hold to undo, keep holding to go further back");
                Line("X", "hold to redo");
            }

            if (!holding && !right && PoseSync.Draft) Line("draft", "on - nothing is going to Daz");
            return _builder.ToString();
        }

        void Line(string button, string action)
        {
            if (_builder.Length > 0) _builder.Append('\n');
            _builder.Append("<color=#7FD4DE>").Append(button).Append("</color>  ").Append(action);
        }

        TextMeshPro Build(string name)
        {
            var go = new GameObject(name);
            var t = go.AddComponent<TextMeshPro>();
            t.rectTransform.sizeDelta = new Vector2(0.34f, 0.12f);
            t.fontSize = 0.022f;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.color = Color.white;
            t.lineSpacing = 8f;
            // Drawn over the figure, like every other overlay the tool puts in the air.
            var material = t.fontMaterial;
            var zTest = Shader.PropertyToID("_ZTestMode");
            if (material.HasProperty(zTest))
                material.SetFloat(zTest, (float)UnityEngine.Rendering.CompareFunction.Always);
            material.renderQueue = 4000;
            return t;
        }

        void OnDestroy()
        {
            foreach (var t in _text) if (t) Destroy(t.gameObject);
        }
    }
}
