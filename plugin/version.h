#pragma once

#include "dzversion.h"

// Plugin version. Bump PLUGIN_BUILD on every build you hand to someone else;
// the client compares protocol versions, not this, so this is for humans.
#define PLUGIN_MAJOR	0
#define PLUGIN_MINOR	1
#define PLUGIN_REV		0
#define PLUGIN_BUILD	2

#define PLUGIN_VERSION	DZ_MAKE_VERSION( PLUGIN_MAJOR, PLUGIN_MINOR, PLUGIN_REV, PLUGIN_BUILD )
