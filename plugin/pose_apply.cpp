#include "pose_apply.h"

#include <algorithm>
#include <cmath>

#include <QJsonArray>
#include <QStringBuilder>
#include <QTimer>

#include "dzbone.h"
#include "dzcamera.h"
#include "dzfloatproperty.h"
#include "dzmatrix3.h"
#include "dznode.h"
#include "dzquat.h"
#include "dzscene.h"
#include "dzskeleton.h"
#include "dzundostack.h"
#include "dzvec3.h"

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

int boneDepth( const DzNode* bone )
{
	int d = 0;
	for ( const DzNode* p = bone->getNodeParent(); p && qobject_cast<const DzBone*>( p ); p = p->getNodeParent() )
	{
		++d;
	}
	return d;
}

double propValue( const DzFloatProperty* p )
{
	return p ? p->getValue() : 0.0;
}

} // namespace

//////////////////////////////////////////////////////////////////////////
// lookup

DzNode* findNodeById( const QString &id )
{
	if ( !id.startsWith( "n_" ) )
	{
		return nullptr;
	}
	bool ok = false;
	const quint64 elementId = id.mid( 2 ).toULongLong( &ok, 16 );
	return ok ? dzScene->findNodeByElementID( elementId ) : nullptr;
}

QString nodeIdOf( const DzNode* node )
{
	return "n_" % QString::number( node->getElementID(), 16 );
}

//////////////////////////////////////////////////////////////////////////
// pose.state

QJsonObject poseStateFor( DzSkeleton* figure )
{
	QJsonArray bones;
	const DzBoneList all = figure->getAllBones();
	for ( const DzBone* bone : all )
	{
		DzVec3 pos;
		DzQuat rot;
		DzMatrix3 scale;
		bone->getWSTransform( pos, rot, scale );

		QJsonObject ws;
		ws[ "pos" ] = jsonVec3( pos );
		ws[ "rot" ] = jsonQuat( rot );

		QJsonObject b;
		b[ "id" ] = bone->getName();
		b[ "ws" ] = ws;
		bones.append( b );
	}

	QJsonObject s;
	s[ "figure" ] = nodeIdOf( figure );
	s[ "bones" ] = bones;
	return s;
}

//////////////////////////////////////////////////////////////////////////
// pose.commit

CommitResult applyPoseCommit( const QJsonObject &header, const QString &undoCaption )
{
	CommitResult r;

	const QString figureId = header.value( "figure" ).toString();
	DzSkeleton* figure = qobject_cast<DzSkeleton*>( findNodeById( figureId ) );
	if ( !figure )
	{
		r.error = "figure not found: " % figureId;
		return r;
	}

	struct Target { DzBone* bone; DzQuat rot; int depth; };
	QVector<Target> targets;

	const QJsonArray bones = header.value( "bones" ).toArray();
	for ( const QJsonValue &v : bones )
	{
		const QJsonArray e = v.toArray();
		if ( e.size() != 5 )
		{
			r.error = "bone entry must be [id, x, y, z, w]";
			return r;
		}
		DzBone* bone = figure->findBone( e[0].toString() );
		if ( !bone )
		{
			r.error = "unknown bone: " % e[0].toString();
			return r;
		}
		DzQuat q;
		q.m_x = e[1].toDouble();
		q.m_y = e[2].toDouble();
		q.m_z = e[3].toDouble();
		q.m_w = e[4].toDouble();
		targets.append( Target{ bone, q, boneDepth( bone ) } );
	}

	// Parents first: a child's world rotation only means something once its
	// parent's is final.
	std::stable_sort( targets.begin(), targets.end(),
		[]( const Target &a, const Target &b ) { return a.depth < b.depth; } );

	if ( undoCaption.isEmpty() )
	{
		DzUndoStackLock lock;
		for ( const Target &t : targets )
		{
			t.bone->setWSRot( t.rot );
		}
	}
	else
	{
		DzUndoStackHold hold;
		for ( const Target &t : targets )
		{
			t.bone->setWSRot( t.rot );
		}
		hold.accept( undoCaption );
	}

	r.ok = true;
	r.applied = targets.size();
	return r;
}

//////////////////////////////////////////////////////////////////////////
// node.transform / camera.set / node.state

CommitResult applyNodeTransform( const QJsonObject &header, const QString &undoCaption )
{
	CommitResult r;

	const QString id = header.contains( "camera" ) ? header.value( "camera" ).toString() : header.value( "node" ).toString();
	DzNode* node = findNodeById( id );
	if ( !node )
	{
		r.error = "node not found: " % id;
		return r;
	}
	if ( qobject_cast<DzBone*>( node ) )
	{
		r.error = "bones are posed with pose.commit, not node.transform";
		return r;
	}

	const QJsonArray p = header.value( "pos" ).toArray();
	const QJsonArray q = header.value( "rot" ).toArray();
	if ( p.size() != 3 || q.size() != 4 )
	{
		r.error = "pos must be [x, y, z] and rot [x, y, z, w]";
		return r;
	}
	const DzVec3 pos( float( p[0].toDouble() ), float( p[1].toDouble() ), float( p[2].toDouble() ) );
	DzQuat rot;
	rot.m_x = q[0].toDouble();
	rot.m_y = q[1].toDouble();
	rot.m_z = q[2].toDouble();
	rot.m_w = q[3].toDouble();

	DzCamera* camera = qobject_cast<DzCamera*>( node );
	const bool hasFocal = camera && header.contains( "focal_mm" );
	const double focal = header.value( "focal_mm" ).toDouble();

	auto apply = [&]()
	{
		node->setWSPos( pos );
		node->setWSRot( rot );
		if ( hasFocal )
		{
			camera->setFocalLength( focal );
		}
	};

	if ( undoCaption.isEmpty() )
	{
		DzUndoStackLock lock;
		apply();
	}
	else
	{
		DzUndoStackHold hold;
		apply();
		hold.accept( undoCaption );
	}

	r.ok = true;
	r.applied = 1;
	return r;
}

QJsonObject nodeStateFor( DzNode* node )
{
	DzVec3 pos;
	DzQuat rot;
	DzMatrix3 scale;
	node->getWSTransform( pos, rot, scale );

	QJsonObject t;
	t[ "pos" ] = jsonVec3( pos );
	t[ "rot" ] = jsonQuat( rot );
	t[ "scale" ] = QJsonArray{ double( scale[0][0] ), double( scale[1][1] ), double( scale[2][2] ) };

	QJsonObject s;
	s[ "node" ] = nodeIdOf( node );
	s[ "transform" ] = t;
	if ( const DzCamera* camera = qobject_cast<const DzCamera*>( node ) )
	{
		s[ "focal_mm" ] = camera->getFocalLength();
	}
	return s;
}

//////////////////////////////////////////////////////////////////////////
// self-test

EulerSnapshot snapshotEulers( DzSkeleton* figure )
{
	EulerSnapshot s;
	s.figureId = nodeIdOf( figure );
	const DzBoneList all = figure->getAllBones();
	for ( const DzBone* bone : all )
	{
		s.rotDeg.insert( bone->getName(), QVector<double>{
			propValue( bone->getXRotControl() ),
			propValue( bone->getYRotControl() ),
			propValue( bone->getZRotControl() ) } );
	}
	return s;
}

void restoreEulers( const EulerSnapshot &snap )
{
	DzSkeleton* figure = qobject_cast<DzSkeleton*>( findNodeById( snap.figureId ) );
	if ( !figure )
	{
		return;
	}
	DzUndoStackLock lock;
	for ( auto it = snap.rotDeg.cbegin(); it != snap.rotDeg.cend(); ++it )
	{
		DzBone* bone = figure->findBone( it.key() );
		if ( !bone )
		{
			continue;
		}
		if ( DzFloatProperty* p = bone->getXRotControl() ) p->setValue( float( it.value()[0] ) );
		if ( DzFloatProperty* p = bone->getYRotControl() ) p->setValue( float( it.value()[1] ) );
		if ( DzFloatProperty* p = bone->getZRotControl() ) p->setValue( float( it.value()[2] ) );
	}
}

double compareEulers( const EulerSnapshot &snap, QString* worstBone )
{
	DzSkeleton* figure = qobject_cast<DzSkeleton*>( findNodeById( snap.figureId ) );
	if ( !figure )
	{
		return 1e9;
	}
	double worst = 0.0;
	for ( auto it = snap.rotDeg.cbegin(); it != snap.rotDeg.cend(); ++it )
	{
		const DzBone* bone = figure->findBone( it.key() );
		if ( !bone )
		{
			continue;
		}
		const double now[3] = {
			propValue( bone->getXRotControl() ),
			propValue( bone->getYRotControl() ),
			propValue( bone->getZRotControl() ) };
		for ( int a = 0; a < 3; ++a )
		{
			// Angles are periodic; 359.99 vs -0.01 is not an error.
			double d = std::fabs( now[a] - it.value()[a] );
			d = std::fmod( d, 360.0 );
			d = std::min( d, 360.0 - d );
			if ( d > worst )
			{
				worst = d;
				if ( worstBone )
				{
					*worstBone = it.key() % "." % QString( "xyz"[a] );
				}
			}
		}
	}
	return worst;
}

//////////////////////////////////////////////////////////////////////////
// PoseWatcher

PoseWatcher::PoseWatcher( QObject* parent ) :
	QObject( parent )
{
	m_timer = new QTimer( this );
	m_timer->setSingleShot( true );
	m_timer->setInterval( 100 ); // desk edits arrive as bursts; coalesce them
	connect( m_timer, &QTimer::timeout, this, &PoseWatcher::flush );

	connect( dzScene, &DzScene::sceneLoaded, this, &PoseWatcher::rescan );
	connect( dzScene, &DzScene::sceneCleared, this, &PoseWatcher::rescan );
	connect( dzScene, &DzScene::skeletonListChanged, this, &PoseWatcher::rescan );
	connect( dzScene, &DzScene::nodeListChanged, this, &PoseWatcher::rescan );
}

void PoseWatcher::rescan()
{
	// Props, cameras, lights: their own transform is the whole story.
	const int nodes = dzScene->getNumNodes();
	for ( int i = 0; i < nodes; ++i )
	{
		DzNode* node = dzScene->getNode( i );
		if ( !node || qobject_cast<DzBone*>( node ) || qobject_cast<DzSkeleton*>( node ) || m_watched.contains( node ) )
		{
			continue;
		}
		m_watched.insert( node );
		connect( node, &DzNode::transformChanged, this, [this, node]() { onNodeTransformChanged( node ); } );
		connect( node, &QObject::destroyed, this, [this, node]() { m_watched.remove( node ); m_dirtyNodes.remove( node ); } );
	}

	const int count = dzScene->getNumSkeletons();
	for ( int i = 0; i < count; ++i )
	{
		DzSkeleton* figure = dzScene->getSkeleton( i );
		if ( !figure || figure->getFollowTarget() )
		{
			continue; // followers move with their figure
		}
		const DzBoneList all = figure->getAllBones();
		for ( DzBone* bone : all )
		{
			if ( m_watched.contains( bone ) )
			{
				continue;
			}
			m_watched.insert( bone );
			connect( bone, &DzNode::transformChanged, this, [this, figure]() { onTransformChanged( figure ); } );
			connect( bone, &QObject::destroyed, this, [this, bone]() { m_watched.remove( bone ); } );
		}
		if ( !m_watched.contains( figure ) )
		{
			m_watched.insert( figure );
			connect( figure, &DzNode::transformChanged, this, [this, figure]() { onTransformChanged( figure ); } );
			connect( figure, &QObject::destroyed, this, [this, figure]() { m_watched.remove( figure ); m_dirty.remove( figure ); } );
		}
	}
}

void PoseWatcher::setSuppressed( bool on )
{
	m_suppressed = on;
	if ( on )
	{
		m_dirty.clear();
		m_dirtyNodes.clear();
		m_timer->stop();
	}
}

void PoseWatcher::onTransformChanged( DzSkeleton* figure )
{
	if ( m_suppressed )
	{
		return;
	}
	m_dirty.insert( figure );
	m_timer->start();
}

void PoseWatcher::onNodeTransformChanged( DzNode* node )
{
	if ( m_suppressed )
	{
		return;
	}
	m_dirtyNodes.insert( node );
	m_timer->start();
}

void PoseWatcher::flush()
{
	const QSet<DzSkeleton*> dirty = m_dirty;
	const QSet<DzNode*> dirtyNodes = m_dirtyNodes;
	m_dirty.clear();
	m_dirtyNodes.clear();
	for ( DzSkeleton* figure : dirty )
	{
		emit figureChanged( figure );
	}
	for ( DzNode* node : dirtyNodes )
	{
		emit nodeChanged( node );
	}
}

} // namespace DazVrBridge

#include "moc_pose_apply.cpp"
