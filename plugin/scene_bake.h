#pragma once

// Builds the scene manifest the VR client loads: every node with its type,
// world transform and (for figures/followers) the full skeleton with per-bone
// rotation orders, limits and rest geometry — plus, in Phase 1b, the
// content-addressed mesh/skin/material assets each node references.

#include <QByteArray>
#include <QJsonObject>
#include <QList>
#include <QString>

#include "texture_bake.h"

namespace DazVrBridge {

struct BakeOptions
{
	QString	textures = "opacity";	// none | opacity | full
	int		texMax = 1024;
	int		influences = 4;			// skin influences per vertex, 4 or 8
	bool	includeHidden = false;
	bool	meshes = true;			// false: manifest only (skeletons, transforms)
	bool	hulls = true;			// approximate geometry the bake cannot use (strand hair)
};

struct BakedAsset
{
	QString		hash;	// "sha1:<hex>"
	QString		kind;	// mesh | skin | materials | texture
	QByteArray	bytes;	// empty for a texture until something asks for it
	// Set when kind == "texture": how to make those bytes, and how many there
	// will be. Described at bake time, produced on request. See texture_bake.h.
	TextureRef	texture;
	qint64		size = 0;
};

struct BakeResult
{
	QJsonObject			manifest;
	QList<BakedAsset>	assets;
	QStringList			log;
};

BakeOptions	bakeOptionsFromJson( const QJsonObject &header );
BakeResult	bakeScene( const BakeOptions &opts );

} // namespace DazVrBridge
