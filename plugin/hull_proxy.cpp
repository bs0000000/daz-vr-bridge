#include "hull_proxy.h"

#include <QDataStream>
#include <QIODevice>
#include <QVector>

#include "dzfacetshape.h"
#include "dznode.h"
#include "dzobject.h"
#include "dzshape.h"
#include "dzvertexmesh.h"

#include "mesh_bake.h"
#include "scene_bake.h"

namespace DazVrBridge {

namespace {

// Matches the writer in mesh_bake.cpp; a proxy is an ordinary mesh as far as the
// client is concerned, which is the point -- nothing on that side needs to know.
const char* c_meshMagic = "DZM1";

// Coarse enough to stay a silhouette rather than pretend to be strands, fine enough
// that a ponytail is a bulge and not a sphere.
const int c_rings = 16;		// latitude bands, pole to pole
const int c_segments = 28;	// longitude divisions

int cell( int ring, int segment )
{
	return ring * c_segments + segment;
}

DzVec3 direction( int ring, int segment )
{
	const double phi = M_PI * ( ring / double( c_rings - 1 ) );			// 0..pi, +Y down to -Y
	const double theta = 2.0 * M_PI * ( segment / double( c_segments ) );
	return DzVec3( float( sin( phi ) * cos( theta ) ), float( cos( phi ) ), float( sin( phi ) * sin( theta ) ) );
}

// Every point votes for the grid cell nearest its own direction, keeping the furthest
// it reaches. One pass over the points rather than one per cell, which matters when
// the cloud is a hundred thousand strand vertices.
void accumulate( QVector<float> &radius, const QVector<DzVec3> &points, const DzVec3 &centre )
{
	for ( const DzVec3 &p : points )
	{
		const DzVec3 d( p.m_x - centre.m_x, p.m_y - centre.m_y, p.m_z - centre.m_z );
		const float length = sqrtf( d.m_x * d.m_x + d.m_y * d.m_y + d.m_z * d.m_z );
		if ( length < 1e-5f )
		{
			continue;
		}
		const float ny = qBound( -1.0f, d.m_y / length, 1.0f );
		const int ring = qBound( 0, int( acosf( ny ) / float( M_PI ) * ( c_rings - 1 ) + 0.5f ), c_rings - 1 );
		double theta = atan2( d.m_z, d.m_x );
		if ( theta < 0.0 ) theta += 2.0 * M_PI;
		const int segment = qBound( 0, int( theta / ( 2.0 * M_PI ) * c_segments + 0.5 ) % c_segments, c_segments - 1 );
		float &r = radius[ cell( ring, segment ) ];
		r = qMax( r, length );
	}
}

// A cell no point landed in takes the average of the cells around it, repeatedly, so
// gaps close from their edges instead of collapsing to the centre.
void fillAndSmooth( QVector<float> &radius )
{
	for ( int pass = 0; pass < 4; ++pass )
	{
		QVector<float> next = radius;
		for ( int ring = 0; ring < c_rings; ++ring )
		{
			for ( int segment = 0; segment < c_segments; ++segment )
			{
				float sum = 0.0f;
				int count = 0;
				for ( int dr = -1; dr <= 1; ++dr )
				{
					const int r = ring + dr;
					if ( r < 0 || r >= c_rings ) continue;
					for ( int ds = -1; ds <= 1; ++ds )
					{
						const int s = ( segment + ds + c_segments ) % c_segments;
						const float value = radius[ cell( r, s ) ];
						if ( value > 0.0f ) { sum += value; ++count; }
					}
				}
				if ( count == 0 ) continue;
				const float average = sum / count;
				float &target = next[ cell( ring, segment ) ];
				// Empty cells take the neighbourhood outright; filled ones are only
				// eased toward it, so a real bulge survives the smoothing.
				target = radius[ cell( ring, segment ) ] <= 0.0f ? average
					: radius[ cell( ring, segment ) ] * 0.6f + average * 0.4f;
			}
		}
		radius = next;
	}
}

} // namespace

bool bakeHullProxy( DzNode* node, const DzNode* space, MeshChunks &out )
{
	DzObject* obj = node->getObject();
	if ( !obj )
	{
		return false;
	}

	// Whatever geometry exists, facets or not. Strand hair has no facet mesh to bake,
	// but it does have vertices, and the shape of the cloud they make is the whole
	// point here: the silhouette is what is missing from the headset, not the strands.
	QVector<DzVec3> points;
	if ( DzVertexMesh* mesh = obj->getCachedGeom() )
	{
		const int count = mesh->getNumVertices();
		points.reserve( count );
		for ( int i = 0; i < count; ++i )
		{
			points.append( worldToNodeLocal( space, mesh->getVertex( i ) ) );
		}
	}
	if ( points.size() < 32 )
	{
		out.warnings << QString( "no geometry to approximate (%1 vertices)" ).arg( points.size() );
		return false;
	}

	DzVec3 centre( 0.0f, 0.0f, 0.0f );
	for ( const DzVec3 &p : points )
	{
		centre.m_x += p.m_x; centre.m_y += p.m_y; centre.m_z += p.m_z;
	}
	centre.m_x /= points.size(); centre.m_y /= points.size(); centre.m_z /= points.size();

	QVector<float> radius( c_rings * c_segments, 0.0f );
	accumulate( radius, points, centre );
	fillAndSmooth( radius );

	// --- a sphere whose radius varies per direction
	struct Vertex { float x, y, z, u, v; };
	QVector<Vertex> verts;
	verts.reserve( c_rings * ( c_segments + 1 ) );
	for ( int ring = 0; ring < c_rings; ++ring )
	{
		// The seam column is duplicated so its UVs can run to 1 instead of back to 0.
		for ( int segment = 0; segment <= c_segments; ++segment )
		{
			const int s = segment % c_segments;
			const DzVec3 d = direction( ring, s );
			const float r = radius[ cell( ring, s ) ];
			verts.append( Vertex{
				centre.m_x + d.m_x * r, centre.m_y + d.m_y * r, centre.m_z + d.m_z * r,
				segment / float( c_segments ), 1.0f - ring / float( c_rings - 1 ) } );
		}
	}

	QVector<quint32> indices;
	indices.reserve( ( c_rings - 1 ) * c_segments * 6 );
	const int stride = c_segments + 1;
	for ( int ring = 0; ring + 1 < c_rings; ++ring )
	{
		for ( int segment = 0; segment < c_segments; ++segment )
		{
			const quint32 a = quint32( ring * stride + segment );
			const quint32 b = quint32( a + 1 );
			const quint32 c = quint32( ( ring + 1 ) * stride + segment );
			const quint32 d = quint32( c + 1 );
			indices << a << c << b;
			indices << b << c << d;
		}
	}

	{
		QDataStream ds( &out.mesh, QIODevice::WriteOnly );
		ds.setByteOrder( QDataStream::LittleEndian );
		ds.setFloatingPointPrecision( QDataStream::SinglePrecision );

		ds.writeRawData( c_meshMagic, 4 );
		ds << quint32( verts.size() ) << quint32( indices.size() / 3 );
		ds << quint32( 1 );	// uvs present
		ds << quint32( 1 );	// one material group

		for ( const Vertex &v : verts ) { ds << v.x << v.y << v.z; }
		for ( const Vertex &v : verts ) { ds << v.u << v.v; }
		for ( quint32 i : indices ) { ds << i; }
		ds << quint32( 0 ) << quint32( indices.size() / 3 ) << quint16( 0 ) << quint16( 0 );
	}

	out.vertices = verts.size();
	out.triangles = indices.size() / 3;
	out.warnings << QString( "approximated from %1 points" ).arg( points.size() );
	return true;
}

} // namespace DazVrBridge
