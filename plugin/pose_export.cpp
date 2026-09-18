#include "pose_export.h"

#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QStringBuilder>
#include <QUrl>

#include "dzapp.h"
#include "dzbone.h"
#include "dzcontentmgr.h"
#include "dzfloatproperty.h"
#include "dzskeleton.h"

namespace DazVrBridge {

namespace {

// The undriven value, the same one the self-test restores with. getValue() includes
// whatever ERC is contributing, and writing that into a preset bakes a controller's
// work into the base so that applying the preset counts it twice.
double raw( const DzFloatProperty* p )
{
	return p ? p->getRawValue() : 0.0;
}

QJsonObject channel( const QString &bone, const char* kind, const char* axis, double value )
{
	QJsonArray key;
	key.append( 0 );
	key.append( value );
	QJsonArray keys;
	keys.append( key );

	// Built as a QString first: QJsonValueRef will not take a QStringBuilder.
	const QString url = QString( "name://@selection/" ) % bone % ":?" % kind % "/" % axis % "/value";

	QJsonObject o;
	o[ "url" ] = url;
	o[ "keys" ] = keys;
	return o;
}

// Daz will happily write a file called "Take 3 / 14:22"; Windows will not.
QString safeName( const QString &name )
{
	QString out;
	for ( const QChar c : name )
	{
		out.append( QString( "\\/:*?\"<>|" ).contains( c ) ? QChar( '-' ) : c );
	}
	out = out.simplified();
	return out.isEmpty() ? QString( "VR pose" ) : out;
}

QString libraryRoot()
{
	DzContentMgr* content = dzApp ? dzApp->getContentMgr() : nullptr;
	if ( content )
	{
		for ( int i = 0; i < content->getNumContentDirectories(); ++i )
		{
			const QString path = content->getContentDirectoryPath( i );
			if ( !path.isEmpty() && QDir( path ).exists() )
			{
				return path;
			}
		}
	}
	// No content directory configured is odd but survivable: put it beside the scene.
	return QDir::homePath();
}

} // namespace

ExportResult exportPosePreset( DzSkeleton* figure, const QString &name )
{
	ExportResult r;
	if ( !figure )
	{
		r.error = "no figure";
		return r;
	}

	QJsonArray animations;
	const DzBoneList bones = figure->getAllBones();
	for ( DzBone* bone : bones )
	{
		const QString id = bone->getName();
		animations.append( channel( id, "rotation", "x", raw( bone->getXRotControl() ) ) );
		animations.append( channel( id, "rotation", "y", raw( bone->getYRotControl() ) ) );
		animations.append( channel( id, "rotation", "z", raw( bone->getZRotControl() ) ) );
	}

	// Translation only where a pose actually uses it: the root bone carries the figure,
	// and a translation on every bone would drag each one off its joint the moment the
	// preset met a figure of a different shape.
	for ( DzBone* bone : bones )
	{
		if ( qobject_cast<DzBone*>( bone->getNodeParent() ) )
		{
			continue;	// not the root of the rig
		}
		const QString id = bone->getName();
		animations.append( channel( id, "translation", "x", raw( bone->getXPosControl() ) ) );
		animations.append( channel( id, "translation", "y", raw( bone->getYPosControl() ) ) );
		animations.append( channel( id, "translation", "z", raw( bone->getZPosControl() ) ) );
		break;
	}

	const QString file = safeName( name ) % ".duf";
	const QString relative = QString( "Poses/VR Bridge/" ) % file;
	const QString folder = libraryRoot() % "/Poses/VR Bridge";
	if ( !QDir().mkpath( folder ) )
	{
		r.error = "could not create " % folder;
		return r;
	}

	QJsonObject contributor;
	contributor[ "author" ] = "Daz VR Bridge";
	contributor[ "email" ] = "";
	contributor[ "website" ] = "";

	QJsonObject info;
	// Url-encoded, with a leading slash, the way Daz writes its own.
	const QString assetId = "/" % QString::fromLatin1( QUrl::toPercentEncoding( relative, "/" ) );
	info[ "id" ] = assetId;
	info[ "type" ] = "preset_pose";
	info[ "contributor" ] = contributor;
	info[ "revision" ] = "1.0";
	info[ "modified" ] = QDateTime::currentDateTimeUtc().toString( Qt::ISODate );

	QJsonObject scene;
	scene[ "animations" ] = animations;

	QJsonObject root;
	root[ "file_version" ] = "0.6.0.0";
	root[ "asset_info" ] = info;
	root[ "scene" ] = scene;

	// Uncompressed. Daz writes these gzipped and reads either, and a preset you can
	// open in a text editor is worth more here than the kilobytes.
	const QString path = folder % "/" % file;
	QFile out( path );
	if ( !out.open( QIODevice::WriteOnly | QIODevice::Truncate ) )
	{
		r.error = "could not write " % path % ": " % out.errorString();
		return r;
	}
	out.write( QJsonDocument( root ).toJson( QJsonDocument::Indented ) );
	out.close();

	r.ok = true;
	r.path = path;
	r.channels = animations.size();
	return r;
}

} // namespace DazVrBridge
