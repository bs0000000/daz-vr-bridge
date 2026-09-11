#include "dzapp.h"
#include "dzplugin.h"

#include "bridge_pane.h"
#include "version.h"

// Plugin definition - creates the exports Daz Studio looks for (see dazvrbridge.def)
DZ_PLUGIN_DEFINITION( "DAZ-VR Posing Bridge" );

DZ_PLUGIN_AUTHOR( "Bryan Smee" );

DZ_PLUGIN_VERSION( PLUGIN_MAJOR, PLUGIN_MINOR, PLUGIN_REV, PLUGIN_BUILD );

DZ_PLUGIN_DESCRIPTION( QString(
	"Poses %1 scenes in VR. Runs a small TCP server that a SteamVR client "
	"connects to (on this machine or another on the LAN); the client receives "
	"the scene and sends poses back, which are applied here as undo steps.<br/><br/>"
	"Open the <b>VR Bridge</b> pane from Window &gt; Panes (Tabs) to start the "
	"server and see the address and pairing code." ).arg( DzApp::getDisplayName() )
);

// Every class exported from a plugin needs its own GUID. These were generated
// for this project; do not reuse them elsewhere.
DZ_PLUGIN_CLASS_GUID( DzVrBridgePane,		F8336316-E2A0-490E-88CA-720D1E1A2EA5 );
DZ_PLUGIN_CLASS_GUID( DzVrBridgePaneAction,	79FAD462-09EF-42A5-AA67-60C5E27FB1D5 );
