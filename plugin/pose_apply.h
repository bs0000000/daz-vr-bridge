#pragma once

// Phase 2: poses in both directions.
//
//   pose.commit   client -> Daz   target WORLD rotation per bone (Daz quaternion
//                                 sense); applied parents-first via DzNode::setWSRot
//                                 inside one undo step
//   pose.state    Daz -> client   every bone's world transform for one figure,
//                                 sent (debounced) whenever any bone moves at the desk
//   selftest      both            snapshot Eulers, send pose.state, client echoes it
//                                 as pose.commit, plugin compares and restores

#include <QHash>
#include <QJsonObject>
#include <QObject>
#include <QSet>
#include <QString>
#include <QVector>

class DzCamera;
class DzNode;
class DzSkeleton;
class QTimer;

namespace DazVrBridge {

// "n_<hex element id>" as written by scene_bake -> node, or nullptr.
DzNode*		findNodeById( const QString &id );
QString		nodeIdOf( const DzNode* node );

// pose.state payload for one figure: { figure, bones: [ { id, ws: { pos, rot } } ] }
QJsonObject	poseStateFor( DzSkeleton* figure );

struct CommitResult
{
	bool	ok = false;
	int		applied = 0;
	QString	error;
};

// Applies a pose.commit header. When `undoCaption` is empty nothing is pushed
// onto the undo stack (used by the self-test); otherwise one entry is created.
CommitResult	applyPoseCommit( const QJsonObject &header, const QString &undoCaption );

// Applies a node.transform / camera.set header: world position and rotation
// (Daz quaternion sense); scale untouched. Cameras also take focal_mm.
// Empty undoCaption = no undo entry (preview).
CommitResult	applyNodeTransform( const QJsonObject &header, const QString &undoCaption );

// node.state payload: { node, transform: { pos, rot, scale } } (+ lens for cameras)
QJsonObject	nodeStateFor( DzNode* node );

// Lens fields for a camera: focal_mm, frame_width_mm, fov (Daz's, radians,
// frame-width formula), aspect and render_px — the render's when the camera
// does not use local dimensions, which is the usual case.
void	writeCameraLens( QJsonObject &into, DzCamera* camera );

// Self-test bookkeeping: Euler snapshot of a figure, compared after the echo.
struct EulerSnapshot
{
	QString							figureId;
	QHash<QString, QVector<double>>	rotDeg;	// bone id -> [x, y, z]
};

EulerSnapshot	snapshotEulers( DzSkeleton* figure );
void			restoreEulers( const EulerSnapshot &snap );
// Compares the figure's current Eulers to the snapshot. Returns max abs error in degrees.
double			compareEulers( const EulerSnapshot &snap, QString* worstBone );

// Watches every bone of every figure (-> figureChanged, for pose.state) and
// every prop/camera/light node (-> nodeChanged, for node.state), debounced,
// so the server can broadcast desk-side edits.
class PoseWatcher : public QObject
{
	Q_OBJECT
public:
	explicit PoseWatcher( QObject* parent = nullptr );

	// (Re)connect to the current scene. Cheap to call repeatedly.
	void	rescan();
	// While suppressed, changes are swallowed (bake freeze, commit application).
	void	setSuppressed( bool on );

Q_SIGNALS:
	void	figureChanged( DzSkeleton* figure );
	void	nodeChanged( DzNode* node );

private:
	void	onTransformChanged( DzSkeleton* figure );
	void	onNodeTransformChanged( DzNode* node );
	void	flush();

	QSet<DzNode*>			m_watched;
	QSet<DzSkeleton*>		m_dirty;
	QSet<DzNode*>			m_dirtyNodes;
	QTimer*					m_timer = nullptr;
	bool					m_suppressed = false;
};

} // namespace DazVrBridge
