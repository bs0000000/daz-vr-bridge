#include "scene_bake.h"

#include <QJsonArray>

#include "dzapp.h"
#include "dzbone.h"
#include "dzcamera.h"
#include "dzdistantlight.h"
#include "dzfigure.h"
#include "dzfloatproperty.h"
#include "dzlight.h"
#include "dzmatrix3.h"
#include "dznode.h"
#include "dzobject.h"
#include "dzpointlight.h"
#include "dzquat.h"
#include "dzrotationorder.h"
#include "dzscene.h"
#include "dzskeleton.h"
#include "dzspotlight.h"
#include "dzvec3.h"

#include "bridge_protocol.h"

namespace DazVrBridge {

namespace {

QJsonArray jsonVec3( const DzVec3 &v )
{
	return QJsonArray{ double( v.m_x ), double( v.m_y ), double( v.m_z ) };
}

QJsonArray jsonQuat( const DzQuat &q )
{
	return QJsonArray{ q.m_x, q.m_y, q.m_z, q.m_w };
}

// Stable within a session; the client never needs to parse it.
QString nodeId( const DzNode* node )
{
	return "n_" % QString::number( node->getElementID(), 16 );
}

QJsonObject jsonTransform( const DzNode* node )
{
	DzVec3 pos;
	DzQuat rot;
	DzMatrix3 scale;
	node->getWSTransform( pos, rot, scale );

	QJsonObject t;
	t[ "pos" ] = jsonVec3( pos );
	t[ "rot" ] = jsonQuat( rot );
	// DAZ scale is a full matrix; the diagonal is all a posing preview needs.
	t[ "scale" ] = QJsonArray{ double( scale[0][0] ), double( scale[1][1] ), double( scale[2][2] ) };
	return t;
}

QJsonArray jsonLimits( const DzFloatProperty* prop )
{
	if ( !prop )
	{
		return QJsonArray{ -180.0, 180.0 };
	}
	return QJsonArray{ prop->getMin(), prop->getMax() };
}

double propValue( const DzFloatProperty* prop )
{
	return prop ? prop->getValue() : 0.0;
}

QString guessRig( const DzSkeleton* skel )
{
	const QString asset = skel->getAssetId().toLower();
	if ( asset.contains( "genesis9" ) ) return "genesis9";
	if ( asset.contains( "genesis8" ) ) return "genesis8";
	if ( asset.contains( "genesis3" ) ) return "genesis3";
	return "unknown";
}

QJsonObject jsonBone( const DzBone* bone )
{
	QJsonObject b;
	b[ "id" ] = bone->getName();
	b[ "label" ] = bone->getLabel();

	const DzNode* parent = bone->getNodeParent();
	b[ "parent" ] = qobject_cast<const DzBone*>( parent ) ? QJsonValue( parent->getName() ) : QJsonValue();

	b[ "rot_order" ] = bone->getRotationOrder().toString();

	// Bone geometry in figure space. defaultVal=false so the character's shape
	// morphs (which move joint centers by centimeters) are included; the
	// defaultVal=true values are the base Genesis mesh and are wrong for any
	// morphed character. Orientation is absolute (figure space), not
	// parent-relative: DAZ expresses each bone's pose rotation in this frame.
	b[ "origin" ] = jsonVec3( bone->getOrigin( false ) );
	b[ "end" ] = jsonVec3( bone->getEndPoint( false ) );
	b[ "orient" ] = jsonQuat( bone->getOrientation( false ) );

	// Current world transform. Note ws.rot is the accumulated *pose* rotation
	// (identity at zero pose), not the orientation frame above.
	b[ "ws" ] = jsonTransform( bone );

	const DzFloatProperty* rx = bone->getXRotControl();
	const DzFloatProperty* ry = bone->getYRotControl();
	const DzFloatProperty* rz = bone->getZRotControl();

	QJsonObject limits;
	limits[ "x" ] = jsonLimits( rx );
	limits[ "y" ] = jsonLimits( ry );
	limits[ "z" ] = jsonLimits( rz );
	b[ "limits_deg" ] = limits;
	b[ "clamped" ] = rx ? rx->isClamped() : false;

	// Current local Euler values, in the bone's own rotation order.
	b[ "rot_deg" ] = QJsonArray{ propValue( rx ), propValue( ry ), propValue( rz ) };
	return b;
}

QJsonObject jsonSkeleton( const DzSkeleton* skel )
{
	QJsonArray bones;
	const DzBoneList all = skel->getAllBones();
	for ( const DzBone* bone : all )
	{
		bones.append( jsonBone( bone ) );
	}

	QJsonObject s;
	s[ "bones" ] = bones;
	return s;
}

QString nodeType( const DzNode* node )
{
	// Order matters: DzLight derives from DzCamera, DzFigure from DzSkeleton.
	if ( qobject_cast<const DzLight*>( node ) ) return "light";
	if ( qobject_cast<const DzCamera*>( node ) ) return "camera";
	if ( const DzSkeleton* skel = qobject_cast<const DzSkeleton*>( node ) )
	{
		return skel->getFollowTarget() ? "follower" : "figure";
	}
	if ( node->getObject() ) return "prop";
	return "null";
}

QJsonObject jsonNode( const DzNode* node, const QString &type )
{
	QJsonObject n;
	n[ "id" ] = nodeId( node );
	n[ "name" ] = node->getName();
	n[ "label" ] = node->getLabel();
	n[ "type" ] = type;
	n[ "visible" ] = node->isVisible();
	n[ "asset_id" ] = node->getAssetId();
	n[ "transform" ] = jsonTransform( node );

	// Parent: the nearest non-bone ancestor, plus the bone if parented to one
	// (a prop held in a hand).
	const DzNode* parent = node->getNodeParent();
	QJsonValue parentBone;
	while ( const DzBone* bone = qobject_cast<const DzBone*>( parent ) )
	{
		if ( parentBone.isNull() )
		{
			parentBone = bone->getName();
		}
		parent = bone->getNodeParent();
	}
	n[ "parent" ] = parent ? QJsonValue( nodeId( parent ) ) : QJsonValue();
	n[ "parent_bone" ] = parentBone;

	if ( type == "figure" || type == "follower" )
	{
		const DzSkeleton* skel = static_cast<const DzSkeleton*>( node );
		n[ "rig" ] = guessRig( skel );
		n[ "skeleton" ] = jsonSkeleton( skel );
		if ( const DzSkeleton* target = skel->getFollowTarget() )
		{
			n[ "follower_of" ] = nodeId( target );
		}
	}
	else if ( type == "camera" || type == "light" )
	{
		const DzCamera* cam = static_cast<const DzCamera*>( node );
		n[ "focal_mm" ] = cam->getFocalLength();
		n[ "frame_width_mm" ] = cam->getFrameWidth();
		n[ "aspect" ] = cam->getAspectRatio();

		if ( type == "light" )
		{
			QString kind = "light";
			if ( qobject_cast<const DzSpotLight*>( node ) ) kind = "spot";
			else if ( qobject_cast<const DzPointLight*>( node ) ) kind = "point";
			else if ( qobject_cast<const DzDistantLight*>( node ) ) kind = "distant";
			n[ "kind" ] = kind;
			if ( const DzDistantLight* dl = qobject_cast<const DzDistantLight*>( node ) )
			{
				n[ "intensity" ] = dl->getIntensity();
			}
		}
	}

	// Meshes/materials attach here in Phase 1b as content hashes.
	return n;
}

} // namespace

BakeOptions bakeOptionsFromJson( const QJsonObject &h )
{
	BakeOptions o;
	o.textures = h.value( "textures" ).toString( o.textures );
	o.texMax = h.value( "tex_max" ).toInt( o.texMax );
	o.influences = h.value( "influences" ).toInt( o.influences ) == 8 ? 8 : 4;
	o.includeHidden = h.value( "include_hidden" ).toBool( o.includeHidden );
	return o;
}

QJsonObject buildManifest( const BakeOptions &opts )
{
	QJsonArray nodes;

	const int count = dzScene->getNumNodes();
	for ( int i = 0; i < count; ++i )
	{
		const DzNode* node = dzScene->getNode( i );
		if ( !node || qobject_cast<const DzBone*>( node ) )
		{
			continue; // bones are emitted under their skeleton
		}
		if ( !opts.includeHidden && !node->isVisible() )
		{
			continue;
		}
		nodes.append( jsonNode( node, nodeType( node ) ) );
	}

	QJsonObject m;
	m[ "protocol" ] = kProtocolVersion;
	m[ "scene" ] = dzScene->getFilename();
	m[ "units" ] = "cm";
	m[ "up" ] = "y";
	m[ "handedness" ] = "right";

	QJsonObject bake;
	bake[ "textures" ] = opts.textures;
	bake[ "tex_max" ] = opts.texMax;
	bake[ "influences" ] = opts.influences;
	bake[ "meshes" ] = false; // Phase 1b
	m[ "bake" ] = bake;

	m[ "nodes" ] = nodes;
	m[ "assets" ] = QJsonArray();
	return m;
}

} // namespace DazVrBridge
