# DAZ-VR Posing Bridge - Daz Studio 6 plugin
#
# This is not a standalone project. It is pulled in by the Daz Studio 6.x SDK's
# top-level CMakeLists.txt (via its "My Plugins" hook or an add_subdirectory
# line), which defines the dzcore / dzsdkmemory imported targets, the
# DZSDK_QT_*_TARGET variables, DZ_PLUGIN_PREFIX, and forces /MD.

if( NOT TARGET dzcore )
	message( FATAL_ERROR
		"daz-vr-bridge/plugin must be built from the Daz Studio SDK root. "
		"Open the SDK folder in Visual Studio and add this directory via "
		"add_subdirectory( \"<path>/daz-vr-bridge/plugin\" dazvrbridge )." )
endif()

set( CMAKE_AUTOMOC ON )
set( CMAKE_INCLUDE_CURRENT_DIR ON )

set( DZ_PLUGIN_TGT_NAME dazvrbridge )
set( DZ_PLUGIN_PROJECT_NAME "DAZ-VR Posing Bridge" )

if( WIN32 )
	set( OS_SOURCES
		DazVrBridge.rc
		dazvrbridge.def
		"${DAZ_SDK_INCLUDE_DIR}/dzsdkmemorydefines.cpp"
	)
elseif( APPLE )
	set( OS_SOURCES "" )
endif( WIN32 )

add_library( ${DZ_PLUGIN_TGT_NAME} SHARED
	bridge_protocol.cpp
	bridge_protocol.h
	bridge_server.cpp
	bridge_server.h
	bridge_pane.cpp
	bridge_pane.h
	scene_bake.cpp
	scene_bake.h
	pluginmain.cpp
	version.h
	${OS_SOURCES}
)

target_link_libraries( ${DZ_PLUGIN_TGT_NAME}
	PRIVATE
	dzcore
	${DZSDK_QT_CORE_TARGET}
	${DZSDK_QT_GUI_TARGET}
	${DZSDK_QT_SCRIPT_TARGET}
	${DZSDK_QT_NETWORK_TARGET}
)

if( WIN32 )
	target_link_libraries( ${DZ_PLUGIN_TGT_NAME}
		PRIVATE
		dzsdkmemory
	)
endif( WIN32 )

set_target_properties( ${DZ_PLUGIN_TGT_NAME}
	PROPERTIES
	FOLDER "My Plugins"
	PROJECT_LABEL ${DZ_PLUGIN_PROJECT_NAME}
	OUTPUT_NAME "${DZ_PLUGIN_PREFIX}${DZ_PLUGIN_TGT_NAME}"
)

# The SDK only routes its own samples into Daz Studio's plugins folder
# (that logic lives in samples/CMakeLists.txt), so do it for ourselves here.
if( DAZ_STUDIO_EXE_DIR )
	set( DZ_PLUGIN_OUT_DIR "${DAZ_STUDIO_EXE_DIR}/plugins" )
	set_target_properties( ${DZ_PLUGIN_TGT_NAME}
		PROPERTIES
		RUNTIME_OUTPUT_DIRECTORY "${DZ_PLUGIN_OUT_DIR}"
		LIBRARY_OUTPUT_DIRECTORY "${DZ_PLUGIN_OUT_DIR}"
	)
	foreach( curConfig ${CMAKE_CONFIGURATION_TYPES} )
		string( TOUPPER ${curConfig} curConfig )
		set_target_properties( ${DZ_PLUGIN_TGT_NAME}
			PROPERTIES
			RUNTIME_OUTPUT_DIRECTORY_${curConfig} "${DZ_PLUGIN_OUT_DIR}"
			LIBRARY_OUTPUT_DIRECTORY_${curConfig} "${DZ_PLUGIN_OUT_DIR}"
		)
	endforeach()
endif( DAZ_STUDIO_EXE_DIR )

source_group( "Resource Files" REGULAR_EXPRESSION ".*\\.(rc|def)" )
source_group( "Source Files\\generated\\moc" REGULAR_EXPRESSION "/(moc|mocs)_.*\\.c(xx|pp)$" )
