#pragma once

// Writing a pose out of VR as a Daz pose preset.
//
// A take that only exists inside the headset is a screenshot of a session. A .duf in
// the content library is a thing you can apply to another figure next week, which is
// what "save the pose" has to mean for this to be worth doing at all.
//
// The file is written directly rather than through DzAssetIOMgr's save filters: those
// want a DzFileIOSettings nobody here can fill in honestly, and the ones that take
// defaults put a dialog on the screen -- which, with the headset on, is a hang. The
// format is small, stable and was read off Daz's own presets rather than guessed:
//
//   { "file_version": "0.6.0.0",
//     "asset_info": { "id": <url-encoded path>, "type": "preset_pose", ... },
//     "scene": { "animations": [
//        { "url": "name://@selection/lForearmBend:?rotation/x/value", "keys": [[0, 12.5]] },
//        ... ] } }
//
// `@selection` is why a preset is reusable: it applies to whatever is selected when it
// is loaded, not to the figure it came from.

#include <QString>
#include <QStringList>

class DzSkeleton;

namespace DazVrBridge {

struct ExportResult
{
	bool	ok = false;
	QString	path;		// where it landed
	QString	error;
	int		channels = 0;
};

// Writes `figure`'s current pose. `name` becomes the file name; anything a file name
// cannot hold is replaced. The file goes under <first content directory>/Poses/VR
// Bridge/, so it appears in the Content Library where poses are looked for.
ExportResult	exportPosePreset( DzSkeleton* figure, const QString &name );

} // namespace DazVrBridge
