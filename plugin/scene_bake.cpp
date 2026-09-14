#include "scene_bake.h"

#include <QCryptographicHash>
#include <QElapsedTimer>
#include <QJsonArray>
#include <QJsonDocument>
#include <QStringBuilder>

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
#include "hull_proxy.h"
#include "mesh_bake.h"
#include "pose_apply.h"

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

	// Current local pose rotation as DAZ computes it from the Euler controls.
	b[ "q_local" ] = jsonQuat( bone->getLocalRot() );

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

QJsonObject jsonSkeleton( const DzSkeleton* skel, QStringList &boneIds )
{
	QJsonArray bones;
	const DzBoneList all = skel->getAllBones();
	for ( const DzBone* bone : all )
	{
		bones.append( jsonBone( bone ) );
		boneIds << bone->getName();
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

QJsonObject jsonNode( const DzNode* node, const QString &type, QStringList &boneIds )
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
		n[ "skeleton" ] = jsonSkeleton( skel, boneIds );
		if ( const DzSkeleton* target = skel->getFollowTarget() )
		{
			n[ "follower_of" ] = nodeId( target );
		}
	}
	else if ( type == "camera" || type == "light" )
	{
		DzCamera* cam = const_cast<DzCamera*>( static_cast<const DzCamera*>( node ) );
		writeCameraLens( n, cam );

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

	return n;
}

// Rewrites each bone's origin/end with the pivot's ACTUAL zero-pose position.
// Must run inside a PoseFreeze. Daz's getOrigin() is the untranslated center
// point, but shape morphs can drive bone translations through ERC (a hip lift
// keeping longer legs on the floor) that survive zeroing the controls; the
// zero-pose mesh includes them, so the pivots the client binds to must too.
void writeRestPivots( QJsonObject &entry, const DzSkeleton* skel )
{
	QJsonObject skeleton = entry.value( "skeleton" ).toObject();
	QJsonArray bones = skeleton.value( "bones" ).toArray();
	const DzBoneList all = skel->getAllBones();

	for ( int i = 0; i < all.size() && i < bones.size(); ++i )
	{
		const DzBone* bone = all[ i ];
		QJsonObject b = bones[ i ].toObject();
		if ( b.value( "id" ).toString() != bone->getName() )
		{
			continue;
		}
		const DzVec3 restOrigin = bone->getOrigin( false );
		const DzVec3 restEnd = bone->getEndPoint( false );
		const DzVec3 pivot = worldToNodeLocal( skel, bone->getWSPos() );
		const DzVec3 endLocal( pivot.m_x + ( restEnd.m_x - restOrigin.m_x ),
			pivot.m_y + ( restEnd.m_y - restOrigin.m_y ),
			pivot.m_z + ( restEnd.m_z - restOrigin.m_z ) );

		b[ "origin" ] = jsonVec3( pivot );
		b[ "end" ] = jsonVec3( endLocal );
		b[ "rest_origin" ] = jsonVec3( restOrigin );
		bones[ i ] = b;
	}

	skeleton[ "bones" ] = bones;
	entry[ "skeleton" ] = skeleton;
}

// The centre a region is measured from: whatever the request named, else whatever is
// selected in Daz. Selecting the figure you are about to pose and asking for a slice
// around it is the workflow this is for.
DzVec3 regionCentre( const BakeOptions &opts )
{
	if ( opts.hasRegionCentre )
	{
		return DzVec3( float( opts.regionCentre[ 0 ] ), float( opts.regionCentre[ 1 ] ), float( opts.regionCentre[ 2 ] ) );
	}
	if ( DzNode* selected = dzScene->getPrimarySelection() )
	{
		return selected->getWSPos();
	}
	return DzVec3( 0.0f, 0.0f, 0.0f );
}

// Distance from the centre to the node's world box, zero when the centre is inside it.
double distanceToRegion( DzNode* node, const DzVec3 &centre )
{
	const DzBox3 box = node->getWSBoundingBox();
	const float bx = qBound( box.getMinX(), centre.m_x, box.getMaxX() );
	const float by = qBound( box.getMinY(), centre.m_y, box.getMaxY() );
	const float bz = qBound( box.getMinZ(), centre.m_z, box.getMaxZ() );
	const double dx = bx - centre.m_x, dy = by - centre.m_y, dz = bz - centre.m_z;
	return sqrt( dx * dx + dy * dy + dz * dz );
}

QString sha1( const QByteArray &bytes )
{
	return "sha1:" % QString::fromLatin1( QCryptographicHash::hash( bytes, QCryptographicHash::Sha1 ).toHex() );
}

void addAsset( BakeResult &result, QJsonObject &node, const QString &key, const QString &kind, const QByteArray &bytes )
{
	if ( bytes.isEmpty() )
	{
		return;
	}
	const QString hash = sha1( bytes );
	node[ key ] = hash;
	for ( const BakedAsset &a : result.assets )
	{
		if ( a.hash == hash )
		{
			return; // identical content already listed (e.g. two of the same prop)
		}
	}
	BakedAsset asset;
	asset.hash = hash;
	asset.kind = kind;
	asset.bytes = bytes;
	asset.size = bytes.size();
	result.assets.append( asset );
}

// A texture is listed, not produced: the manifest carries its hash, dimensions and
// exact byte count so the client can skip whatever its cache already holds and ask
// for the rest, and only then does anything get decoded.
void addTextureAssets( BakeResult &result, const QList<TextureRef> &textures )
{
	for ( const TextureRef &ref : textures )
	{
		bool known = false;
		for ( const BakedAsset &a : result.assets )
		{
			if ( a.hash == ref.hash ) { known = true; break; }
		}
		if ( known )
		{
			continue;
		}
		BakedAsset asset;
		asset.hash = ref.hash;
		asset.kind = "texture";
		asset.texture = ref;
		asset.size = ref.size;
		result.assets.append( asset );
	}
}

// Bakes one node's mesh and attaches the asset hashes to its manifest entry.
// `space` is the node whose local frame the positions use (see bakeNodeMesh).
void bakeMeshInto( DzNode* node, const DzNode* space, QJsonObject &entry, const QStringList &figureBones,
	const BakeOptions &opts, BakeResult &result, DzSkeleton* skeleton = nullptr )
{
	MeshChunks chunks;
	bool ok = bakeNodeMesh( node, space, figureBones, opts, chunks );
	if ( !ok && opts.hulls )
	{
		// Strand hair has no facet mesh, so it used to bake to nothing and the headset
		// showed a bald figure -- which reads as broken rather than as simplified. Its
		// vertices still describe a silhouette, and a silhouette is what was missing.
		ok = bakeHullProxy( node, space, figureBones, skeleton, opts.influences, chunks );
		if ( ok )
		{
			entry[ "approximate" ] = true;
			// A hull's UVs are spherical and mean nothing to a map drawn for strands, so
			// it keeps the surface's colour and drops its textures rather than smearing
			// them across a blob.
			chunks.textures.clear();
			QJsonArray mats = chunks.materials.value( "materials" ).toArray();
			for ( int i = 0; i < mats.size(); ++i )
			{
				QJsonObject m = mats.at( i ).toObject();
				m.remove( "base_tex" );
				m.remove( "normal_tex" );
				m.remove( "cutout" );
				// A hull is an open shell -- it has a hole where the face is -- so the
				// inside of its far side has to draw, or you look through the opening
				// into nothing.
				m[ "two_sided" ] = true;
				mats.replace( i, m );
			}
			chunks.materials[ "materials" ] = mats;
		}
	}
	for ( const QString &w : chunks.warnings )
	{
		result.log << node->getLabel() % ": " % w;
	}
	if ( !ok )
	{
		// Whatever is left really has nothing to show; tell the client why so it can
		// report the node as "not baked" rather than silently missing.
		entry[ "mesh_skipped" ] = chunks.warnings.isEmpty() ? QString( "no geometry" ) : chunks.warnings.join( "; " );
		return;
	}

	addAsset( result, entry, "mesh", "mesh", chunks.mesh );
	addAsset( result, entry, "skin", "skin", chunks.skin );
	addAsset( result, entry, "materials", "materials", QJsonDocument( chunks.materials ).toJson( QJsonDocument::Compact ) );
	addTextureAssets( result, chunks.textures );
	entry[ "vertices" ] = chunks.vertices;
	entry[ "triangles" ] = chunks.triangles;
}

} // namespace

BakeOptions bakeOptionsFromJson( const QJsonObject &h )
{
	BakeOptions o;
	o.textures = h.value( "textures" ).toString( o.textures );
	o.texMax = h.value( "tex_max" ).toInt( o.texMax );
	o.influences = h.value( "influences" ).toInt( o.influences ) == 8 ? 8 : 4;
	o.includeHidden = h.value( "include_hidden" ).toBool( o.includeHidden );
	o.meshes = h.value( "meshes" ).toBool( o.meshes );
	o.hulls = h.value( "hulls" ).toBool( o.hulls );
	o.regionRadius = h.value( "region_radius" ).toDouble( o.regionRadius );
	const QJsonArray centre = h.value( "region_center" ).toArray();
	if ( centre.size() == 3 )
	{
		o.hasRegionCentre = true;
		for ( int i = 0; i < 3; ++i ) { o.regionCentre[ i ] = centre.at( i ).toDouble(); }
	}
	return o;
}

BakeResult bakeScene( const BakeOptions &opts )
{
	BakeResult result;
	QElapsedTimer timer;
	timer.start();

	// Pass 1: collect nodes and figures. Bones are emitted under their skeleton.
	QList<DzNode*> nodes;
	const int count = dzScene->getNumNodes();
	for ( int i = 0; i < count; ++i )
	{
		DzNode* node = dzScene->getNode( i );
		if ( !node || qobject_cast<DzBone*>( node ) )
		{
			continue;
		}
		if ( !opts.includeHidden && !node->isVisible() )
		{
			continue;
		}
		nodes.append( node );
	}

	// Pass 2: manifest entries. Figures are baked at zero pose together with
	// their followers so both share one frozen state; skeleton entries are
	// written while frozen so ws/origin reflect the bind pose.
	QHash<DzNode*, QJsonObject> entries;
	QHash<DzNode*, QStringList> figureBones; // figure -> bone ids, in manifest order

	auto bakeFigureGroup = [&]( DzSkeleton* figure )
	{
		// Manifest entries first, with the *current* pose (ws, q_local, rot_deg),
		// so the client can show the figure as it stands in Daz Studio.
		QStringList bones;
		QJsonObject fe = jsonNode( figure, "figure", bones );
		figureBones.insert( figure, bones );

		QList<DzSkeleton*> followers;
		QHash<DzSkeleton*, QJsonObject> followerEntries;
		for ( DzNode* node : nodes )
		{
			DzSkeleton* s = qobject_cast<DzSkeleton*>( node );
			if ( s && s->getFollowTarget() == figure )
			{
				QStringList ownBones;
				followers.append( s );
				followerEntries.insert( s, jsonNode( s, "follower", ownBones ) );
			}
		}

		// Then, with the figure (and therefore its followers) at zero pose: the
		// pivots as they actually are at rest, and the bind meshes in figure space.
		{
			PoseFreeze freeze( figure );
			writeRestPivots( fe, figure );
			for ( DzSkeleton* s : followers )
			{
				writeRestPivots( followerEntries[ s ], s );
			}
			if ( opts.meshes )
			{
				// Followers are expressed in the figure's space: the client parents
				// them under the figure and skins them with the figure's bones.
				bakeMeshInto( figure, figure, fe, bones, opts, result, figure );
				for ( DzSkeleton* s : followers )
				{
					bakeMeshInto( s, figure, followerEntries[ s ], bones, opts, result, figure );
				}
			}
		}

		entries.insert( figure, fe );
		for ( DzSkeleton* s : followers )
		{
			entries.insert( s, followerEntries.value( s ) );
		}
	};

	const DzVec3 centre = regionCentre( opts );
	int outsideRegion = 0;
	for ( DzNode* node : nodes )
	{
		const QString type = nodeType( node );
		if ( type == "figure" )
		{
			bakeFigureGroup( static_cast<DzSkeleton*>( node ) );
		}
		else if ( type == "follower" )
		{
			// Handled with its figure. A follower whose target is hidden or
			// missing from the node list falls through to a plain entry below.
			if ( !entries.contains( node ) )
			{
				DzSkeleton* target = static_cast<DzSkeleton*>( node )->getFollowTarget();
				if ( !target || !nodes.contains( target ) )
				{
					QStringList bones;
					QJsonObject e = jsonNode( node, "follower", bones );
					entries.insert( node, e );
					result.log << node->getLabel() % ": follower without a visible target, mesh skipped";
				}
			}
		}
		else
		{
			QStringList none;
			QJsonObject e = jsonNode( node, type, none );
			if ( opts.meshes && type == "prop" )
			{
				// Outside the region a prop still appears in the manifest, with its
				// transform and its place in the tree, so the scene stays legible and
				// asking again with a bigger radius fills it in. Only its geometry is
				// withheld -- which is the part that costs a bake, a transfer and a draw.
				if ( opts.regionRadius > 0.0 && distanceToRegion( node, centre ) > opts.regionRadius )
				{
					e[ "mesh_skipped" ] = "outside the requested region";
					++outsideRegion;
				}
				else
				{
					bakeMeshInto( node, node, e, none, opts, result );
				}
			}
			entries.insert( node, e );
		}
	}

	QJsonArray nodeArray;
	for ( DzNode* node : nodes )
	{
		if ( entries.contains( node ) )
		{
			nodeArray.append( entries.value( node ) );
		}
	}

	QJsonArray assets;
	qint64 totalBytes = 0;
	for ( const BakedAsset &a : result.assets )
	{
		QJsonObject o;
		o[ "hash" ] = a.hash;
		o[ "kind" ] = a.kind;
		o[ "size" ] = double( a.size );
		if ( a.kind == "texture" )
		{
			o[ "w" ] = a.texture.width;
			o[ "h" ] = a.texture.height;
			o[ "mips" ] = a.texture.mips;
			o[ "format" ] = a.texture.alpha ? "bc3" : "bc1";
		}
		assets.append( o );
		totalBytes += a.size;
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
	bake[ "meshes" ] = opts.meshes;
	bake[ "hulls" ] = opts.hulls;
	if ( opts.regionRadius > 0.0 )
	{
		bake[ "region_radius" ] = opts.regionRadius;
		bake[ "region_center" ] = QJsonArray{ centre.m_x, centre.m_y, centre.m_z };
		bake[ "outside_region" ] = outsideRegion;
	}
	bake[ "ms" ] = double( timer.elapsed() );
	bake[ "asset_bytes" ] = double( totalBytes );
	m[ "bake" ] = bake;

	m[ "nodes" ] = nodeArray;
	m[ "assets" ] = assets;
	result.manifest = m;
	return result;
}

} // namespace DazVrBridge
