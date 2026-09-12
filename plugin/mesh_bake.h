#pragma once

// Bakes one node's geometry into the binary chunks the client loads:
//
//   mesh  (DZM1)  positions (cm, node-local), uvs, triangles, material groups
//   skin  (DZS1)  per-vertex bone indices + weights, top-N, normalized
//   materials     JSON: per material index, base color constant + map paths
//
// Figures must be at zero pose when baked (see PoseFreeze); the caller is
// responsible for that so a figure and all its followers are baked in the
// same frozen state.

#include <QByteArray>
#include <QJsonObject>
#include <QStringList>
#include <QVector>

class DzNode;
class DzSkeleton;

namespace DazVrBridge {

struct BakeOptions;

struct MeshChunks
{
	QByteArray	mesh;
	QByteArray	skin;		// empty for props
	QJsonObject	materials;
	int			vertices = 0;
	int			triangles = 0;
	QStringList	warnings;
};

// figureBones: bone ids of the skeleton the skin indices refer to (the
// figure's own bones, or the follow target's for a follower). Empty for props.
bool	bakeNodeMesh( DzNode* node, const QStringList &figureBones, const BakeOptions &opts, MeshChunks &out );

// Zeroes every bone's rotation/translation on a figure (and drops SubD to
// level 0 on it and its followers) for the lifetime of the object, without
// touching the undo stack. Restores everything in the destructor.
class PoseFreeze
{
public:
	explicit PoseFreeze( DzSkeleton* figure );
	~PoseFreeze();

	PoseFreeze( const PoseFreeze & ) = delete;
	PoseFreeze&	operator=( const PoseFreeze & ) = delete;

private:
	struct Saved;
	QVector<Saved>	m_saved;
	QVector<DzNode*>	m_nodes;
};

} // namespace DazVrBridge
