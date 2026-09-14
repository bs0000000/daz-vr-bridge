#pragma once

// Textures, described cheaply at bake time and produced only when asked for.
//
// A texture asset's hash is taken from the SOURCE's identity -- absolute path,
// modification time, byte size, and the settings it would be converted under --
// rather than from the converted bytes. That is the whole reason the manifest can
// still go out in a quarter of a second: describing a texture costs one image
// header read, while producing one costs a full decode of a 4096-square JPEG plus
// a mip chain and block compression. The client learns every texture's hash, size
// and dimensions up front, skips whatever its disk cache already holds, and asks
// for the rest over the bulk connection while posing carries on.
//
// What ships is GPU-ready: BC1 (or BC3 where alpha matters) with a full mip chain,
// laid out the way Texture2D.LoadRawTextureData wants it. The headset does a
// memcpy and an upload -- no JPEG decode and no runtime compression, which on the
// main thread at 4096 square is exactly the hitch this design exists to avoid.

#include <QByteArray>
#include <QString>

namespace DazVrBridge {

// One texture per material rather than one per map. A Daz opacity map is a
// greyscale JPEG with no alpha channel of its own, so shipping it as-is would send
// a fully opaque BC3 and clip nothing; its luminance has to become the alpha of the
// colour map it belongs with. Merging here also means the client binds one map and
// the GPU samples one map.
struct TextureRef
{
	QString	colorPath;		// absolute, on the Daz machine; may be empty
	QString	opacityPath;	// absolute; empty when the surface is not a cutout
	QString	hash;			// "sha1:<hex>" of the sources' identity, not their bytes
	int		width = 0;		// after the tex_max clamp, a power of two
	int		height = 0;
	bool	alpha = false;	// BC3 when true (there is an opacity map), BC1 when not
	int		mips = 0;
	qint64	size = 0;		// exactly what produceTexture will return

	bool	isValid() const { return !hash.isEmpty() && width > 0 && height > 0; }
	QString	label() const;	// for logs
};

// Reads image headers only. Returns an invalid ref when neither map exists or
// neither can be read, which the caller should treat as "no texture" rather than as
// an error: a scene referencing a map the user has since moved is ordinary.
TextureRef	describeTexture( const QString &colorPath, const QString &opacityPath, int texMax );

// Decodes, scales, builds the mip chain and compresses. Seconds of work for a
// large map, so this belongs on an asset request and never in a bake.
QByteArray	produceTexture( const TextureRef &ref, QString* errorOut = nullptr );

} // namespace DazVrBridge
