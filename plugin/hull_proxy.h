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

namespace DazVrBridge {

struct MeshChunks;

bool	bakeHullProxy( DzNode* node, const DzNode* space, MeshChunks &out );

} // namespace DazVrBridge
