#pragma once

// A rough stand-in for geometry the mesh bake cannot use.
//
// Strand-based hair has no facet mesh, so it bakes to nothing and the headset shows a
// bald figure -- which reads as broken rather than as simplified. It does have
// vertices, though, and the shape of the cloud they make is exactly what is missing:
// the silhouette, not the strands. This measures how far that cloud reaches in each
// direction from its own centre and builds a sphere with those radii, which keeps a
// bob a bob and a ponytail a ponytail while being a few hundred triangles.
//
// The result is written as an ordinary DZM1 mesh with one material group, so nothing
// on the client knows or needs to know that it is an approximation.

class DzNode;
class DzSkeleton;

#include <QStringList>

namespace DazVrBridge {

struct MeshChunks;

// `figureBones` are the bone ids the skin indices refer to, as for bakeNodeMesh, and
// `skeleton` the figure those bones belong to. A hull is bound rigidly to whichever of
// them it sits nearest -- the head, for hair -- which puts it in the right place and
// carries it when that bone moves. Pass an empty list for a prop, and it ships
// unskinned in its own space.
bool	bakeHullProxy( DzNode* node, const DzNode* space, const QStringList &figureBones,
			DzSkeleton* skeleton, int influences, MeshChunks &out );

} // namespace DazVrBridge
