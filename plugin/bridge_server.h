#pragma once

// TCP server that the VR client talks to. Lives for the whole Daz Studio
// session (singleton) so closing the pane does not drop connections.
//
// Phase 0 scope: hello/welcome handshake with session + pairing, ping/pong,
// and scene.changed broadcasts so the headset HUD can show the scene name.
// Every other message type is acknowledged with an error until its phase lands.

#include <QHash>
#include <QObject>
#include <QStringList>

#include "bridge_protocol.h"
#include "pose_apply.h"
#include "scene_bake.h"

class DzSkeleton;
class QTcpServer;
class QTcpSocket;
class QTimer;

namespace DazVrBridge {

class Server : public QObject
{
	Q_OBJECT
public:
	static Server*	instance();

	bool	start( quint16 port, QString* errorOut = nullptr );
	void	stop();
	bool	isListening() const;
	quint16	port() const;

	// Six-digit code a client on another machine must send in hello.
	// Regenerated on every start(). Loopback clients never need it.
	QString	pairingCode() const { return m_pairingCode; }
	void	setPairingRequired( bool on ) { m_pairingRequired = on; }
	bool	pairingRequired() const { return m_pairingRequired; }

	int		connectionCount() const { return m_connections.size(); }

	// IPv4 addresses of this machine's non-loopback interfaces, for the pane.
	static QStringList	lanAddresses();

Q_SIGNALS:
	void	listeningChanged( bool listening );
	void	connectionCountChanged( int count );
	void	logMessage( const QString &line );

private:
	explicit Server( QObject* parent = nullptr );

	struct Connection
	{
		QTcpSocket*		socket = nullptr;
		FrameDecoder	decoder;
		QString			role;		// "control" or "bulk" once hello succeeds
		QString			session;
		bool			ready = false;
	};

	void	onNewConnection();
	void	onReadyRead( QTcpSocket* socket );
	void	onDisconnected( QTcpSocket* socket );

	void	handleFrame( Connection &c, const Frame &f );
	void	handleHello( Connection &c, const Frame &f );
	void	handleSceneRequest( Connection &c, const Frame &f );
	void	handleAssetRequest( Connection &c, const Frame &f );
	void	handlePoseCommit( Connection &c, const Frame &f );
	void	handleSelfTestBegin( Connection &c, const Frame &f );
	void	handleNodeTransform( Connection &c, const Frame &f );
	void	handleEdit( Connection &c, const Frame &f );
	void	handleNodeCommand( Connection &c, const Frame &f );
	void	handleSelect( Connection &c, const Frame &f );
	void	handleStateRequest( Connection &c, const Frame &f );
	void	handleRender( Connection &c, const Frame &f );
	void	broadcastRenderState( bool rendering );
	void	onFigureChanged( DzSkeleton* figure );
	void	onNodeChanged( DzNode* node );
	void	onNodeListChanged();

	void	send( QTcpSocket* socket, const QJsonObject &header, const QByteArray &payload = QByteArray() );
	void	sendError( Connection &c, const Frame &ref, const QString &code, const QString &msg );
	void	broadcastControl( const QJsonObject &header );

	void	onSceneChanged( const QString &reason );
	QJsonObject	sceneSummary() const;
	// Undo/redo availability and captions, so VR can label its buttons.
	QJsonObject	undoSummary() const;
	void	broadcastEditState();
	bool	sessionExists( const QString &session ) const;
	void	regeneratePairingCode();
	void	log( const QString &line );

	QTcpServer*						m_server = nullptr;
	QHash<QTcpSocket*, Connection>	m_connections;
	QHash<QString, BakedAsset>		m_assets;	// last bake, by content hash
	PoseWatcher*					m_poseWatcher = nullptr;
	QTimer*							m_nodeListTimer = nullptr;	// coalesces nodeListChanged storms
	QTimer*							m_editStateTimer = nullptr;	// coalesces the four undo-stack signals
	bool							m_sceneLoading = false;
	bool							m_sceneClearing = false;
	bool							m_sceneBusy = false;		// between load/clear start and finish
	EulerSnapshot					m_selfTest;	// pending self-test, empty figureId when none
	QString							m_pairingCode;
	bool							m_pairingRequired = true;
	qint64							m_seq = 0;
};

} // namespace DazVrBridge
