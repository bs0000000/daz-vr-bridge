#include "mesh_bake.h"

#include <algorithm>
#include <cmath>
#include <vector>

#include <QDataStream>
#include <QHash>
#include <QIODevice>
#include <QJsonArray>
#include <QPair>

#include "dzbone.h"
#include "dzbonebinding.h"
#include "dzface.h"
#include "dzfacetmesh.h"
#include "dzfacetshape.h"
#include "dzfigure.h"
#include "dzfloatproperty.h"
#include "dzintproperty.h"
#include "dzmap.h"
#include "dzdefaultmaterial.h"
#include "dzimageproperty.h"
#include "dzmaterial.h"
#include "dznumericproperty.h"
#include "dzmatrix3.h"
#include "dznode.h"
#include "dzobject.h"
#include "dzquat.h"
#include "dzscene.h"
#include "dzskeleton.h"
#include "dzskinbinding.h"
#include "dztexture.h"
#include "dzundostack.h"
#include "dzvertexmesh.h"
#include "dzweightmap.h"

#include "scene_bake.h"

namespace DazVrBridge {

namespace {

const char* c_meshMagic = "DZM1";
const char* c_skinMagic = "DZS1";

// One output vertex per distinct (position index, uv index) pair, so UVs can
// be per-vertex on the client. Most vertices map 1:1; seams duplicate.
struct SplitVertex
{
	int	srcVert;
	int	srcUv;
};

struct Influence
{
	quint16	bone;
	float	weight;
};

QString textureFile( const DzTexture* tex )
{
	return tex ? tex->getFilename() : QString();
}

// DzMaterial only promises a colour map and an opacity map; a normal map lives in the
// derived type. The classic surfaces expose it directly, while Iray Uber carries it as
// a named property whose value is a number and whose map is the texture -- so try the
// typed accessor first and fall back to the label, which is what Daz shows in Surfaces.
DzTexture* normalMapOf( const DzMaterial* mat, double &strength )
{
	strength = 1.0;
	if ( const DzDefaultMaterial* def = qobject_cast<const DzDefaultMaterial*>( mat ) )
	{
		if ( DzTexture* map = def->getNormalValueMap() )
		{
			return map;
		}
	}

	static const char* labels[] = { "Normal Map", "Detail Normal Map" };
	for ( const char* label : labels )
	{
		DzProperty* prop = mat->findPropertyByLabel( label );
		if ( !prop )
		{
			continue;
		}
		if ( DzImageProperty* image = qobject_cast<DzImageProperty*>( prop ) )
		{
			if ( DzTexture* map = image->getValue() )
			{
				return map;
			}
		}
		if ( DzNumericProperty* numeric = qobject_cast<DzNumericProperty*>( prop ) )
		{
			if ( DzTexture* map = numeric->getMapValue() )
			{
				strength = numeric->getDoubleValue();
				return map;
			}
		}
	}
	return nullptr;
}

// Records every map it finds into `textures` and writes the asset hash beside the
// path. Describing a texture is one header read, so this stays cheap enough to run
// inside the bake; the decode and the block compression wait for a request.
QJsonObject bakeMaterials( const DzShape* shape, const BakeOptions &opts, QList<TextureRef> &textures )
{
	const auto reference = [&textures, &opts]( const QString &color, const QString &opacity ) -> QJsonValue
	{
		const TextureRef ref = describeTexture( color, opacity, opts.texMax );
		if ( !ref.isValid() )
		{
			return QJsonValue();
		}
		bool known = false;
		for ( const TextureRef &t : textures )
		{
			if ( t.hash == ref.hash ) { known = true; break; }
		}
		if ( !known )
		{
			textures.append( ref );
		}
		return QJsonValue( ref.hash );
	};

	QJsonArray mats;
	const int count = shape->getNumMaterials();
	for ( int i = 0; i < count; ++i )
	{
		const DzMaterial* mat = shape->getMaterial( i );
		QJsonObject m;
		m[ "index" ] = i;
		if ( !mat )
		{
			m[ "name" ] = "missing";
			m[ "base_color" ] = QJsonArray{ 0.6, 0.6, 0.6 };
			mats.append( m );
			continue;
		}

		m[ "name" ] = mat->getName();
		const QColor c = mat->getDiffuseColor().getValue();
		m[ "base_color" ] = QJsonArray{ c.redF(), c.greenF(), c.blueF() };

		// Paths stay for diagnostics; the hash is what the client asks for. Colour and
		// opacity merge into one texture, because Daz keeps opacity in a separate
		// greyscale file and a GPU wants it as the colour map's alpha.
		const QString opacity = opts.textures != "none" ? textureFile( mat->getOpacityMap() ) : QString();
		const QString color = opts.textures == "full" ? textureFile( mat->getColorMap() ) : QString();
		if ( !opacity.isEmpty() ) m[ "opacity_map" ] = opacity;
		if ( !color.isEmpty() ) m[ "color_map" ] = color;
		m[ "base_tex" ] = reference( color, opacity );
		if ( !opacity.isEmpty() ) { m[ "cutout" ] = true; m[ "cutoff" ] = kAlphaCutoff; }

		// Normal maps ride on "full", since without colour they would be the only thing
		// breaking up a clay surface and would read as dirt.
		if ( opts.textures == "full" )
		{
			double strength = 1.0;
			const QString normal = textureFile( normalMapOf( mat, strength ) );
			if ( !normal.isEmpty() )
			{
				const TextureRef ref = describeNormal( normal, opts.texMax );
				if ( ref.isValid() )
				{
					bool known = false;
					for ( const TextureRef &t : textures )
					{
						if ( t.hash == ref.hash ) { known = true; break; }
					}
					if ( !known ) textures.append( ref );
					m[ "normal_map" ] = normal;
					m[ "normal_tex" ] = ref.hash;
					m[ "normal_scale" ] = strength;
				}
			}
		}
		mats.append( m );
	}

	QJsonObject out;
	out[ "materials" ] = mats;
	return out;
}

// Per source vertex, the influences from the figure's bones, top-N and normalized.
QVector<QVector<Influence>> gatherWeights( DzFigure* figure, const QStringList &figureBones,
	int vertexCount, int maxInfluences, QStringList &warnings )
{
	QVector<QVector<Influence>> per( vertexCount );

	DzSkinBinding* binding = figure->getSkinBinding();
	if ( !binding )
	{
		warnings << "no skin binding";
		return per;
	}

	QHash<QString, int> boneIndex;
	for ( int i = 0; i < figureBones.size(); ++i )
	{
		boneIndex.insert( figureBones[ i ], i );
	}

	int unmapped = 0;
	const int bindings = binding->getNumBoneBindings();
	for ( int b = 0; b < bindings; ++b )
	{
		const DzBoneBinding* bb = binding->getBoneBinding( b );
		const DzWeightMap* map = bb ? bb->getWeights() : nullptr;
		DzNode* bone = bb ? bb->getBone() : nullptr;
		if ( !map || !bone )
		{
			continue;
		}

		// Follower-only bones (Genesis 9 Mouth's tongue chain) fold into the
		// nearest ancestor the figure does have.
		int target = -1;
		for ( DzNode* n = bone; n && target < 0; n = n->getNodeParent() )
		{
			target = boneIndex.value( n->getName(), -1 );
		}
		if ( target < 0 )
		{
			++unmapped;
			continue;
		}

		const int n = std::min( vertexCount, map->getNumWeights() );
		for ( int v = 0; v < n; ++v )
		{
			const float w = map->getFloatWeight( v );
			if ( w > 0.0f )
			{
				per[ v ].append( Influence{ quint16( target ), w } );
			}
		}
	}

	if ( unmapped > 0 )
	{
		warnings << QString( "%1 bone binding(s) had no ancestor in the figure skeleton" ).arg( unmapped );
	}

	for ( QVector<Influence> &inf : per )
	{
		std::sort( inf.begin(), inf.end(), []( const Influence &a, const Influence &b ) { return a.weight > b.weight; } );
		if ( inf.size() > maxInfluences )
		{
			inf.resize( maxInfluences );
		}
		float sum = 0.0f;
		for ( const Influence &i : inf ) sum += i.weight;
		if ( sum > 0.0f )
		{
			for ( Influence &i : inf ) i.weight /= sum;
		}
	}
	return per;
}

} // namespace

//////////////////////////////////////////////////////////////////////////
// PoseFreeze

struct PoseFreeze::Saved
{
	DzFloatProperty*	prop;
	double				value;
	DzIntProperty*		intProp;
	int					intValue;
};

PoseFreeze::PoseFreeze( DzSkeleton* figure )
{
	// Everything below happens with the undo stack locked so the user never
	// sees "zero pose" entries, and nothing here is undoable by accident.
	DzUndoStackLock lock;

	// Snapshot with getRawValue(), never getValue(): getValue() is the value *after*
	// ERC controllers, while setValue() writes the raw one. Snapshotting the combined
	// value and restoring it as raw bakes the controller's contribution into the base,
	// and it accumulates on every bake — Genesis 9's shape-driven hip lift was moving
	// the figure up ~6.9 cm per scene load.
	auto freezeShape = [this]( DzNode* node )
	{
		DzObject* obj = node->getObject();
		DzFacetShape* shape = obj ? qobject_cast<DzFacetShape*>( obj->getCurrentShape() ) : nullptr;
		if ( shape && shape->isSubDivisionActive() )
		{
			if ( DzIntProperty* lvl = shape->getSubDLevelControl() )
			{
				m_saved.append( Saved{ nullptr, 0.0, lvl, lvl->getRawValue() } );
				lvl->setValue( 0 );
			}
		}
		m_nodes.append( node );
	};

	auto zero = [this]( DzFloatProperty* p )
	{
		if ( p )
		{
			m_saved.append( Saved{ p, p->getRawValue(), nullptr, 0 } );
			p->setValue( 0.0f );
		}
	};

	const DzBoneList bones = figure->getAllBones();
	for ( DzBone* bone : bones )
	{
		zero( bone->getXRotControl() );
		zero( bone->getYRotControl() );
		zero( bone->getZRotControl() );
		zero( bone->getXPosControl() );
		zero( bone->getYPosControl() );
		zero( bone->getZPosControl() );
	}

	freezeShape( figure );

	// Followers are driven by the figure; they just need SubD dropped and a refresh.
	const int count = dzScene->getNumSkeletons();
	for ( int i = 0; i < count; ++i )
	{
		DzSkeleton* s = dzScene->getSkeleton( i );
		if ( s && s->getFollowTarget() == figure )
		{
			freezeShape( s );
		}
	}

	for ( DzNode* n : m_nodes )
	{
		n->update();
		n->finalize();
	}
}

PoseFreeze::~PoseFreeze()
{
	DzUndoStackLock lock;

	// Restore in reverse so SubD comes back after the pose values.
	for ( int i = m_saved.size() - 1; i >= 0; --i )
	{
		const Saved &s = m_saved[ i ];
		if ( s.prop ) s.prop->setValue( float( s.value ) );
		if ( s.intProp ) s.intProp->setValue( s.intValue );
	}
	for ( DzNode* n : m_nodes )
	{
		n->update();
		n->finalize();
	}
}

//////////////////////////////////////////////////////////////////////////
// spaces

DzVec3 worldToNodeLocal( const DzNode* node, const DzVec3 &world )
{
	DzVec3 pos;
	DzQuat rot;
	DzMatrix3 scale;
	node->getWSTransform( pos, rot, scale );

	const DzVec3 d( world.m_x - pos.m_x, world.m_y - pos.m_y, world.m_z - pos.m_z );
	const DzVec3 r = rot.inverse().multVec( d );
	const float s = scale[0][0] > 1e-6f ? scale[0][0] : 1.0f;
	return DzVec3( r.m_x / s, r.m_y / s, r.m_z / s );
}

//////////////////////////////////////////////////////////////////////////
// bakeNodeMesh

bool bakeNodeMesh( DzNode* node, const DzNode* space, const QStringList &figureBones, const BakeOptions &opts, MeshChunks &out )
{
	DzObject* obj = node->getObject();
	DzFacetShape* shape = obj ? qobject_cast<DzFacetShape*>( obj->getCurrentShape() ) : nullptr;
	DzFacetMesh* base = shape ? shape->getFacetMesh() : nullptr;
	if ( !base )
	{
		out.warnings << "no facet mesh";
		return false;
	}

	const int vertexCount = base->getNumVertices();
	const int facetCount = base->getNumFacets();
	if ( vertexCount == 0 || facetCount == 0 )
	{
		out.warnings << "empty mesh";
		return false;
	}

	// Positions: deformed (shape morphs applied, pose zeroed by the caller)
	// when the cache is at base resolution, else the undeformed base mesh.
	// The cache is WORLD space; bring it into `space`'s local frame so the
	// client can place the node once. The base mesh is already local.
	std::vector<float> localized; // DzPnt3 is float[3]; keep a flat buffer
	const DzPnt3* positions = base->getVerticesPtr();
	if ( const DzVertexMesh* cached = obj->getCachedGeom() )
	{
		if ( cached->getNumVertices() == vertexCount )
		{
			const DzPnt3* world = cached->getVerticesPtr();
			localized.resize( size_t( vertexCount ) * 3 );
			double dx = 0, dy = 0, dz = 0;
			for ( int v = 0; v < vertexCount; ++v )
			{
				const DzVec3 w( world[ v ][0], world[ v ][1], world[ v ][2] );
				const DzVec3 l = worldToNodeLocal( space, w );
				localized[ size_t( v ) * 3 + 0 ] = l.m_x;
				localized[ size_t( v ) * 3 + 1 ] = l.m_y;
				localized[ size_t( v ) * 3 + 2 ] = l.m_z;
				dx += w.m_x - l.m_x; dy += w.m_y - l.m_y; dz += w.m_z - l.m_z;
			}
			positions = reinterpret_cast<const DzPnt3*>( localized.data() );
			const double n = double( vertexCount );
			if ( std::fabs( dx / n ) + std::fabs( dy / n ) + std::fabs( dz / n ) > 0.5 )
			{
				out.warnings << QString( "cached geometry localized by (%1, %2, %3) cm" )
					.arg( dx / n, 0, 'f', 1 ).arg( dy / n, 0, 'f', 1 ).arg( dz / n, 0, 'f', 1 );
			}
		}
		else
		{
			out.warnings << QString( "cached geometry has %1 vertices, base has %2; using undeformed base mesh" )
				.arg( cached->getNumVertices() ).arg( vertexCount );
		}
	}

	const DzMap* uvMap = base->getUVs();
	const DzPnt2* uvs = uvMap ? uvMap->getPnt2ArrayPtr() : nullptr;
	const int uvCount = uvMap ? uvMap->getNumValues() : 0;

	// --- split vertices on (vertex, uv) and triangulate
	QHash<QPair<int, int>, int> splitIndex;
	QVector<SplitVertex> split;
	split.reserve( vertexCount + vertexCount / 8 );

	auto emitVertex = [&]( int v, int uv ) -> quint32
	{
		const QPair<int, int> key( v, uvs ? uv : -1 );
		auto it = splitIndex.find( key );
		if ( it != splitIndex.end() )
		{
			return quint32( it.value() );
		}
		splitIndex.insert( key, split.size() );
		split.append( SplitVertex{ v, uv } );
		return quint32( split.size() - 1 );
	};

	struct Tri { quint32 a, b, c; quint16 mat; };
	QVector<Tri> tris;
	tris.reserve( facetCount * 2 );

	const DzFacet* facets = base->getFacetsPtr();
	for ( int f = 0; f < facetCount; ++f )
	{
		const DzFacet &fc = facets[ f ];
		const quint16 mat = fc.m_materialIdx;
		const quint32 i0 = emitVertex( fc.m_vertIdx[0], fc.m_uvwIdx[0] );
		const quint32 i1 = emitVertex( fc.m_vertIdx[1], fc.m_uvwIdx[1] );
		const quint32 i2 = emitVertex( fc.m_vertIdx[2], fc.m_uvwIdx[2] );
		tris.append( Tri{ i0, i1, i2, mat } );
		if ( fc.m_vertIdx[3] >= 0 )
		{
			const quint32 i3 = emitVertex( fc.m_vertIdx[3], fc.m_uvwIdx[3] );
			tris.append( Tri{ i0, i2, i3, mat } );
		}
	}

	// Group triangles by material so the client gets one submesh per material.
	std::stable_sort( tris.begin(), tris.end(), []( const Tri &a, const Tri &b ) { return a.mat < b.mat; } );

	// --- mesh chunk
	{
		QDataStream ds( &out.mesh, QIODevice::WriteOnly );
		ds.setByteOrder( QDataStream::LittleEndian );
		ds.setFloatingPointPrecision( QDataStream::SinglePrecision );

		ds.writeRawData( c_meshMagic, 4 );
		ds << quint32( split.size() ) << quint32( tris.size() );
		ds << quint32( uvs ? 1u : 0u ); // flags: bit0 = uvs present

		QVector<QPair<quint16, QPair<quint32, quint32>>> groups; // mat -> (start, count)
		for ( int i = 0; i < tris.size(); ++i )
		{
			if ( groups.isEmpty() || groups.last().first != tris[ i ].mat )
			{
				groups.append( qMakePair( tris[ i ].mat, qMakePair( quint32( i ), quint32( 0 ) ) ) );
			}
			groups.last().second.second++;
		}
		ds << quint32( groups.size() );

		for ( const SplitVertex &sv : split )
		{
			const float* p = positions[ sv.srcVert ];
			ds << p[0] << p[1] << p[2];
		}
		if ( uvs )
		{
			for ( const SplitVertex &sv : split )
			{
				if ( sv.srcUv >= 0 && sv.srcUv < uvCount )
				{
					ds << uvs[ sv.srcUv ][0] << uvs[ sv.srcUv ][1];
				}
				else
				{
					ds << 0.0f << 0.0f;
				}
			}
		}
		for ( const Tri &t : tris )
		{
			ds << t.a << t.b << t.c;
		}
		for ( const auto &g : groups )
		{
			ds << g.second.first << g.second.second << g.first << quint16( 0 );
		}
	}

	out.vertices = split.size();
	out.triangles = tris.size();

	// --- skin chunk (figures and followers)
	if ( !figureBones.isEmpty() )
	{
		if ( DzFigure* figure = qobject_cast<DzFigure*>( node ) )
		{
			const QVector<QVector<Influence>> weights =
				gatherWeights( figure, figureBones, vertexCount, opts.influences, out.warnings );

			QDataStream ds( &out.skin, QIODevice::WriteOnly );
			ds.setByteOrder( QDataStream::LittleEndian );
			ds.setFloatingPointPrecision( QDataStream::SinglePrecision );

			ds.writeRawData( c_skinMagic, 4 );
			ds << quint32( split.size() );
			ds << quint8( opts.influences ) << quint8( 0 ) << quint8( 0 ) << quint8( 0 );

			for ( const SplitVertex &sv : split )
			{
				const QVector<Influence> &inf = weights[ sv.srcVert ];
				for ( int i = 0; i < opts.influences; ++i )
				{
					ds << ( i < inf.size() ? inf[ i ].bone : quint16( 0 ) );
				}
				for ( int i = 0; i < opts.influences; ++i )
				{
					ds << ( i < inf.size() ? inf[ i ].weight : 0.0f );
				}
			}
		}
		else
		{
			out.warnings << "skinned node is not a DzFigure";
		}
	}

	out.materials = bakeMaterials( shape, opts, out.textures );
	return true;
}

} // namespace DazVrBridge
