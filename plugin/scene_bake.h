#pragma once

// Builds the scene manifest the VR client loads: every node with its type,
// world transform and (for figures/followers) the full skeleton with per-bone
// rotation orders, limits and rest geometry.
//
// Phase 1a: node graph + skeletons only. Meshes, skin weights and materials
// (the content-addressed assets) come in Phase 1b.

#include <QJsonObject>
#include <QString>

namespace DazVrBridge {

struct BakeOptions
{
	QString	textures = "opacity";	// none | opacity | full
	int		texMax = 1024;
	int		influences = 4;			// skin influences per vertex, 4 or 8
	bool	includeHidden = false;
};

BakeOptions	bakeOptionsFromJson( const QJsonObject &header );
QJsonObject	buildManifest( const BakeOptions &opts );

} // namespace DazVrBridge
