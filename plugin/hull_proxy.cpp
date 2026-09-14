#include "hull_proxy.h"

#include <QDataStream>
#include <QIODevice>
#include <QVector>

#include "dzbone.h"
#include "dzfacetshape.h"
#include "dznode.h"
#include "dzobject.h"
#include "dzshape.h"
#include "dzskeleton.h"
#include "dzvertexmesh.h"

#include "mesh_bake.h"
#include "scene_bake.h"

namespace DazVrBridge {

namespace {

// Matches the writer in mesh_bake.cpp; a proxy is an ordinary mesh as far as the
// client is concerned, which is the point -- nothing on that side needs to know.
const char* c_meshMagic = "DZM1";
const char* c_skinMagic = "DZS1";

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
void accumulate( QVector<float> &radius, QVector<bool> &seen, const QVector<DzVec3> &points, const DzVec3 &centre )
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
		seen[ cell( ring, segment ) ] = true;
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

// Whichever of the figure's bones the hull sits nearest. For hair that is the head,
// and binding to it is what puts the hull where the hair is: a follower's mesh is
// parented under the figure and skinned with its bones, so an unskinned one lands
// wherever the root bone happens to be -- a metre out, in the case of a head.
int nearestBone( DzSkeleton* skeleton, const QStringList &figureBones, const DzVec3 &worldPoint )
{
	int best = 0;
	double bestDistance = -1.0;
	for ( int i = 0; i < figureBones.size(); ++i )
	{
		DzBone* bone = skeleton->findBone( figureBones.at( i ) );
		if ( !bone )
		{
			continue;
		}
		const DzVec3 p = bone->getWSPos();
		const double dx = p.m_x - worldPoint.m_x;
		const double dy = p.m_y - worldPoint.m_y;
		const double dz = p.m_z - worldPoint.m_z;
		const double d = dx * dx + dy * dy + dz * dz;
		if ( bestDistance < 0.0 || d < bestDistance ) { bestDistance = d; best = i; }
	}
	return best;
}

} // namespace

bool bakeHullProxy( DzNode* node, const DzNode* space, const QStringList &figureBones,
	DzSkeleton* skeleton, int influences, MeshChunks &out )
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
	DzVec3 worldCentre( 0.0f, 0.0f, 0.0f );
	if ( DzVertexMesh* mesh = obj->getCachedGeom() )
	{
		const int count = mesh->getNumVertices();
		points.reserve( count );
		for ( int i = 0; i < count; ++i )
		{
			// Kept in world as well as node-local: which bone this hull belongs to is a
			// world-space question, and carrying the centroid along answers it without
			// transforming anything back afterwards.
			const DzVec3 world = mesh->getVertex( i );
			worldCentre.m_x += world.m_x; worldCentre.m_y += world.m_y; worldCentre.m_z += world.m_z;
			points.append( worldToNodeLocal( space, world ) );
		}
		if ( count > 0 )
		{
			worldCentre.m_x /= count; worldCentre.m_y /= count; worldCentre.m_z /= count;
		}
	}
	if ( points.size() < 32 )
	{
		out.warnings << QString( "no geometry to approximate (%1 vertices)" ).arg( points.size() );
		return false;
	}

	// A geometry shell is a copy of its figure's mesh, offset outward. It has no shape
	// of its own, so it lands here exactly as strand hair does -- and hulling it wraps
	// the character in a faceted body-shaped shell with the arms merged into the torso.
	// Nothing is gained by approximating a silhouette that is already the figure's, and
	// a shell is nearly invisible in Daz anyway; matching vertex counts is what says so.
	if ( space && space != node )
	{
		if ( DzObject* target = const_cast<DzNode*>( space )->getObject() )
		{
			if ( DzVertexMesh* targetMesh = target->getCachedGeom() )
			{
				if ( targetMesh->getNumVertices() == points.size() )
				{
					out.warnings << QString( "not approximated: %1 vertices matches its figure, so this is a shell of it" )
						.arg( points.size() );
					return false;
				}
			}
		}
	}

	DzVec3 centre( 0.0f, 0.0f, 0.0f );
	for ( const DzVec3 &p : points )
	{
		centre.m_x += p.m_x; centre.m_y += p.m_y; centre.m_z += p.m_z;
	}
	centre.m_x /= points.size(); centre.m_y /= points.size(); centre.m_z /= points.size();

	QVector<float> radius( c_rings * c_segments, 0.0f );
	// Which directions any hair actually went in. A hull measured outward from one
	// centre is star-shaped, so it spans its own concavities -- and the largest
	// concavity in a head of hair is the face. The filled radii keep the surface smooth
	// where it exists; this says where it exists at all.
	QVector<bool> seen( c_rings * c_segments, false );
	accumulate( radius, seen, points, centre );
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
			// Leave a hole where no strand pointed. Three corners of four, tested against
			// real hair: two left the shell ragged where it thinned, and it is the
			// difference between hair and a helmet.
			const int s1 = ( segment + 1 ) % c_segments;
			const int covered = ( seen[ cell( ring, segment ) ] ? 1 : 0 )
				+ ( seen[ cell( ring, s1 ) ] ? 1 : 0 )
				+ ( seen[ cell( ring + 1, segment ) ] ? 1 : 0 )
				+ ( seen[ cell( ring + 1, s1 ) ] ? 1 : 0 );
			if ( covered < 3 )
			{
				continue;
			}

			const quint32 a = quint32( ring * stride + segment );
			const quint32 b = quint32( a + 1 );
			const quint32 c = quint32( ( ring + 1 ) * stride + segment );
			const quint32 d = quint32( c + 1 );
			// Wound so the outside faces out. The first attempt had it inverted, which is
			// why the head showed through the front and the far side showed its interior.
			indices << a << b << c;
			indices << b << d << c;
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

	// --- skin chunk: every vertex rigidly on one bone
	if ( !figureBones.isEmpty() && skeleton )
	{
		const quint16 bone = quint16( nearestBone( skeleton, figureBones, worldCentre ) );

		QDataStream ds( &out.skin, QIODevice::WriteOnly );
		ds.setByteOrder( QDataStream::LittleEndian );
		ds.setFloatingPointPrecision( QDataStream::SinglePrecision );

		ds.writeRawData( c_skinMagic, 4 );
		ds << quint32( verts.size() );
		ds << quint8( influences ) << quint8( 0 ) << quint8( 0 ) << quint8( 0 );
		for ( int v = 0; v < verts.size(); ++v )
		{
			for ( int i = 0; i < influences; ++i ) { ds << ( i == 0 ? bone : quint16( 0 ) ); }
			for ( int i = 0; i < influences; ++i ) { ds << ( i == 0 ? 1.0f : 0.0f ); }
		}
		out.warnings << QString( "bound to %1" ).arg( figureBones.at( bone ) );
	}

	out.vertices = verts.size();
	out.triangles = indices.size() / 3;
	out.warnings << QString( "approximated from %1 points" ).arg( points.size() );
	return true;
}

} // namespace DazVrBridge
