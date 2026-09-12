#include "mesh_bake.h"

#include <algorithm>

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
#include "dzmaterial.h"
#include "dznode.h"
#include "dzobject.h"
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

QJsonObject bakeMaterials( const DzShape* shape, const BakeOptions &opts )
{
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

		// Paths only; texture bytes ship in a later phase as their own assets.
		if ( opts.textures != "none" )
		{
			const QString opacity = textureFile( mat->getOpacityMap() );
			m[ "opacity_map" ] = opacity.isEmpty() ? QJsonValue() : QJsonValue( opacity );
		}
		if ( opts.textures == "full" )
		{
			const QString color = textureFile( mat->getColorMap() );
			m[ "color_map" ] = color.isEmpty() ? QJsonValue() : QJsonValue( color );
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

	auto freezeShape = [this]( DzNode* node )
	{
		DzObject* obj = node->getObject();
		DzFacetShape* shape = obj ? qobject_cast<DzFacetShape*>( obj->getCurrentShape() ) : nullptr;
		if ( shape && shape->isSubDivisionActive() )
		{
			if ( DzIntProperty* lvl = shape->getSubDLevelControl() )
			{
				m_saved.append( Saved{ nullptr, 0.0, lvl, lvl->getValue() } );
				lvl->setValue( 0 );
			}
		}
		m_nodes.append( node );
	};

	auto zero = [this]( DzFloatProperty* p )
	{
		if ( p )
		{
			m_saved.append( Saved{ p, p->getValue(), nullptr, 0 } );
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
// bakeNodeMesh

bool bakeNodeMesh( DzNode* node, const QStringList &figureBones, const BakeOptions &opts, MeshChunks &out )
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
	const DzPnt3* positions = base->getVerticesPtr();
	if ( const DzVertexMesh* cached = obj->getCachedGeom() )
	{
		if ( cached->getNumVertices() == vertexCount )
		{
			positions = cached->getVerticesPtr();
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

	out.materials = bakeMaterials( shape, opts );
	return true;
}

} // namespace DazVrBridge
