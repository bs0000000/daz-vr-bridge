// Which Daz node a GameObject is. On every node the scene builds, grabbable or not.
//
// The grab handle used to be the only way back from a collider to a node, which meant
// anything without one was invisible to the pointer as well as to the hand: a sofa too
// big to be picked up could not even be asked about. Pointing at a thing and being told
// nothing at all is the worst answer a tool can give, so the tag goes on everything and
// the panel can always say what you are looking at -- including why it does not move.

using UnityEngine;

namespace DazVrBridge
{
    public sealed class NodeTag : MonoBehaviour
    {
        public SceneLoader.LoadedNode Node;
    }
}
