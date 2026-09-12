#include "bridge_server.h"

#include <QCoreApplication>
#include <QHostAddress>
#include <QJsonArray>
#include <QNetworkInterface>
#include <QRandomGenerator>
#include <QStringBuilder>
#include <QTcpServer>
#include <QTcpSocket>
#include <QUuid>

#include "dzapp.h"
#include "dzscene.h"

#include "scene_bake.h"
#include "version.h"

namespace DazVrBridge {

namespace {

const char* c_logCategory = "DazVrBridge";

QString pluginVersionString()
{
	return QString( "%1.%2.%3.%4" ).arg( PLUGIN_MAJOR ).arg( PLUGIN_MINOR ).arg( PLUGIN_REV ).arg( PLUGIN_BUILD );
}

QString peerString( const QTcpSocket* socket )
{
	// Dual-stack listeners report IPv4 peers as ::ffff:a.b.c.d; show the IPv4 form.
	QHostAddress addr = socket->peerAddress();
	bool isV4 = false;
	const quint32 v4 = addr.toIPv4Address( &isV4 );
	if ( isV4 )
	{
		addr = QHostAddress( v4 );
	}
	return addr.toString() % ":" % QString::number( socket->peerPort() );
}

} // namespace

Server* Server::instance()
{
	static Server* s_instance = nullptr;
	if ( !s_instance )
	{
		s_instance = new Server();
		// Close sockets cleanly before Qt tears down networking on exit.
		QObject::connect( QCoreApplication::instance(), &QCoreApplication::aboutToQuit,
			s_instance, &Server::stop );
	}
	return s_instance;
}

Server::Server( QObject* parent ) :
	QObject( parent )
{
	m_server = new QTcpServer( this );
	connect( m_server, &QTcpServer::newConnection, this, &Server::onNewConnection );

	// Scene-level events the client wants to hear about even in Phase 0.
	connect( dzScene, &DzScene::sceneLoaded, this, [this]() { onSceneChanged( "loaded" ); } );
	connect( dzScene, &DzScene::sceneCleared, this, [this]() { onSceneChanged( "cleared" ); } );
	connect( dzScene, &DzScene::sceneFilenameChanged, this, [this]( const QString & ) { onSceneChanged( "renamed" ); } );
}

bool Server::start( quint16 port, QString* errorOut )
{
	if ( m_server->isListening() )
	{
		if ( m_server->serverPort() == port )
		{
			return true;
		}
		stop();
	}

	if ( !m_server->listen( QHostAddress::Any, port ) )
	{
		if ( errorOut )
		{
			*errorOut = m_server->errorString();
		}
		log( QString( "Could not listen on port %1: %2" ).arg( port ).arg( m_server->errorString() ) );
		return false;
	}

	regeneratePairingCode();
	log( QString( "Listening on port %1 (pairing code %2)" ).arg( m_server->serverPort() ).arg( m_pairingCode ) );
	emit listeningChanged( true );
	return true;
}

void Server::stop()
{
	if ( !m_server->isListening() && m_connections.isEmpty() )
	{
		return;
	}

	m_server->close();

	const QList<QTcpSocket*> sockets = m_connections.keys();
	for ( QTcpSocket* socket : sockets )
	{
		socket->disconnect( this );
		socket->close();
		socket->deleteLater();
	}
	m_connections.clear();

	log( "Stopped" );
	emit connectionCountChanged( 0 );
	emit listeningChanged( false );
}

bool Server::isListening() const
{
	return m_server->isListening();
}

quint16 Server::port() const
{
	return m_server->serverPort();
}

QStringList Server::lanAddresses()
{
	QStringList out;
	const QList<QHostAddress> all = QNetworkInterface::allAddresses();
	for ( const QHostAddress &addr : all )
	{
		if ( addr.protocol() == QAbstractSocket::IPv4Protocol && !addr.isLoopback() )
		{
			out << addr.toString();
		}
	}
	return out;
}

//////////////////////////////////////////////////////////////////////////
// connections

void Server::onNewConnection()
{
	while ( QTcpSocket* socket = m_server->nextPendingConnection() )
	{
		socket->setSocketOption( QAbstractSocket::LowDelayOption, 1 ); // no Nagle on control traffic

		Connection c;
		c.socket = socket;
		m_connections.insert( socket, c );

		connect( socket, &QTcpSocket::readyRead, this, [this, socket]() { onReadyRead( socket ); } );
		connect( socket, &QTcpSocket::disconnected, this, [this, socket]() { onDisconnected( socket ); } );

		log( "Connection from " % peerString( socket ) );
		emit connectionCountChanged( m_connections.size() );
	}
}

void Server::onReadyRead( QTcpSocket* socket )
{
	auto it = m_connections.find( socket );
	if ( it == m_connections.end() )
	{
		return;
	}

	Connection &c = it.value();
	c.decoder.feed( socket->readAll() );

	Frame frame;
	while ( c.decoder.next( frame ) )
	{
		handleFrame( c, frame );
		if ( !m_connections.contains( socket ) )
		{
			return; // handler closed the connection
		}
	}

	if ( c.decoder.hasError() )
	{
		log( "Protocol error from " % peerString( socket ) % ": " % c.decoder.error() );
		socket->close();
	}
}

void Server::onDisconnected( QTcpSocket* socket )
{
	auto it = m_connections.find( socket );
	if ( it == m_connections.end() )
	{
		return;
	}

	log( QString( "%1 disconnected (%2)" ).arg( peerString( socket ), it->role.isEmpty() ? QString( "no hello" ) : it->role ) );
	m_connections.erase( it );
	socket->deleteLater();
	emit connectionCountChanged( m_connections.size() );
}

//////////////////////////////////////////////////////////////////////////
// messages

void Server::handleFrame( Connection &c, const Frame &f )
{
	const QString type = f.type();

	if ( !c.ready )
	{
		if ( type == "hello" )
		{
			handleHello( c, f );
		}
		else
		{
			sendError( c, f, "hello_required", "first frame must be hello" );
			c.socket->close();
		}
		return;
	}

	if ( type == "ping" )
	{
		QJsonObject h;
		h[ "t" ] = "pong";
		h[ "seq" ] = m_seq++;
		h[ "ref_seq" ] = f.seq();
		send( c.socket, h );
		return;
	}

	if ( type == "scene.request" )
	{
		handleSceneRequest( c, f );
		return;
	}

	if ( type == "asset.request" )
	{
		handleAssetRequest( c, f );
		return;
	}

	// Phase 2+ : posing
	if ( type == "pose.commit" || type == "select" )
	{
		sendError( c, f, "not_implemented", type % " arrives in Phase 2" );
		return;
	}

	// Phase 3+ : scene editing
	if ( type == "node.transform" || type == "camera.set" )
	{
		sendError( c, f, "not_implemented", type % " arrives in Phase 3" );
		return;
	}

	// Reserved for v2
	if ( type == "pose.preview" )
	{
		sendError( c, f, "deferred_v2", "pose.preview is not part of protocol v1" );
		return;
	}

	sendError( c, f, "unknown_type", "unknown message type '" % type % "'" );
}

void Server::handleHello( Connection &c, const Frame &f )
{
	const QJsonObject &h = f.header;

	const int protocol = h.value( "protocol" ).toInt( 0 );
	if ( protocol != kProtocolVersion )
	{
		sendError( c, f, "protocol_mismatch",
			QString( "client speaks protocol %1, plugin speaks %2" ).arg( protocol ).arg( kProtocolVersion ) );
		c.socket->close();
		return;
	}

	const QString role = h.value( "role" ).toString();
	if ( role != "control" && role != "bulk" )
	{
		sendError( c, f, "bad_role", "role must be 'control' or 'bulk'" );
		c.socket->close();
		return;
	}

	const bool remote = !c.socket->peerAddress().isLoopback();
	if ( remote && m_pairingRequired && h.value( "code" ).toString() != m_pairingCode )
	{
		sendError( c, f, "bad_pairing_code", "pairing code missing or wrong; read it from the VR Bridge pane" );
		log( "Rejected " % peerString( c.socket ) % ": wrong pairing code" );
		c.socket->close();
		return;
	}

	if ( role == "control" )
	{
		c.session = QUuid::createUuid().toString( QUuid::WithoutBraces );
	}
	else
	{
		c.session = h.value( "session" ).toString();
		if ( !sessionExists( c.session ) )
		{
			sendError( c, f, "unknown_session", "open a control connection first and pass its session id" );
			c.socket->close();
			return;
		}
	}

	c.role = role;
	c.ready = true;

	QJsonObject w;
	w[ "t" ] = "welcome";
	w[ "seq" ] = m_seq++;
	w[ "ref_seq" ] = f.seq();
	w[ "protocol" ] = kProtocolVersion;
	w[ "session" ] = c.session;
	w[ "daz_version" ] = DzApp::getVersionString();
	w[ "plugin_version" ] = pluginVersionString();
	w[ "scene" ] = sceneSummary();
	send( c.socket, w );

	log( QString( "%1 joined as %2 (%3, session %4)" )
		.arg( h.value( "client" ).toString( "client" ), role, peerString( c.socket ), c.session.left( 8 ) ) );
}

void Server::handleSceneRequest( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", "scene.request belongs on the control connection" );
		return;
	}

	const BakeOptions opts = bakeOptionsFromJson( f.header );
	log( QString( "Baking scene (textures=%1, influences=%2, meshes=%3)" )
		.arg( opts.textures ).arg( opts.influences ).arg( opts.meshes ? "yes" : "no" ) );

	BakeResult result = bakeScene( opts );
	for ( const QString &line : result.log )
	{
		log( "  " % line );
	}

	// Replace the asset store wholesale: hashes from an older bake that are
	// still valid are re-listed by the new manifest anyway.
	m_assets.clear();
	for ( const BakedAsset &a : result.assets )
	{
		m_assets.insert( a.hash, a );
	}

	QJsonObject h;
	h[ "t" ] = "scene.manifest";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	h[ "manifest" ] = result.manifest;
	send( c.socket, h );

	const QJsonObject bake = result.manifest.value( "bake" ).toObject();
	log( QString( "Manifest sent: %1 nodes, %2 assets (%3 MB) in %4 ms" )
		.arg( result.manifest.value( "nodes" ).toArray().size() )
		.arg( result.assets.size() )
		.arg( bake.value( "asset_bytes" ).toDouble() / ( 1024.0 * 1024.0 ), 0, 'f', 1 )
		.arg( bake.value( "ms" ).toDouble(), 0, 'f', 0 ) );
}

void Server::handleAssetRequest( Connection &c, const Frame &f )
{
	if ( c.role != "bulk" )
	{
		sendError( c, f, "wrong_connection", "asset.request belongs on the bulk connection" );
		return;
	}

	const QJsonArray hashes = f.header.value( "hashes" ).toArray();
	int sent = 0;
	qint64 bytes = 0;
	for ( const QJsonValue &v : hashes )
	{
		const QString hash = v.toString();
		auto it = m_assets.constFind( hash );
		if ( it == m_assets.constEnd() )
		{
			QJsonObject h;
			h[ "t" ] = "error";
			h[ "seq" ] = m_seq++;
			h[ "ref_seq" ] = f.seq();
			h[ "code" ] = "asset_unknown";
			h[ "msg" ] = QString( "no such asset in the last bake: " % hash );
			h[ "hash" ] = hash;
			send( c.socket, h );
			continue;
		}

		QJsonObject h;
		h[ "t" ] = "asset.data";
		h[ "seq" ] = m_seq++;
		h[ "ref_seq" ] = f.seq();
		h[ "hash" ] = it->hash;
		h[ "kind" ] = it->kind;
		h[ "size" ] = it->bytes.size();
		send( c.socket, h, it->bytes );
		++sent;
		bytes += it->bytes.size();
	}

	log( QString( "Sent %1 of %2 requested assets (%3 MB)" )
		.arg( sent ).arg( hashes.size() ).arg( bytes / ( 1024.0 * 1024.0 ), 0, 'f', 1 ) );
}

//////////////////////////////////////////////////////////////////////////
// helpers

void Server::send( QTcpSocket* socket, const QJsonObject &header, const QByteArray &payload )
{
	socket->write( encodeFrame( header, payload ) );
}

void Server::sendError( Connection &c, const Frame &ref, const QString &code, const QString &msg )
{
	QJsonObject h;
	h[ "t" ] = "error";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = ref.seq();
	h[ "code" ] = code;
	h[ "msg" ] = msg;
	send( c.socket, h );
}

void Server::broadcastControl( const QJsonObject &header )
{
	const QByteArray bytes = encodeFrame( header );
	for ( auto it = m_connections.begin(); it != m_connections.end(); ++it )
	{
		if ( it->ready && it->role == "control" )
		{
			it->socket->write( bytes );
		}
	}
}

void Server::onSceneChanged( const QString &reason )
{
	if ( m_connections.isEmpty() )
	{
		return;
	}

	QJsonObject h;
	h[ "t" ] = "scene.changed";
	h[ "seq" ] = m_seq++;
	h[ "reason" ] = reason;
	h[ "scene" ] = sceneSummary();
	broadcastControl( h );
}

QJsonObject Server::sceneSummary() const
{
	QJsonObject s;
	s[ "path" ] = dzScene->getFilename();
	s[ "nodes" ] = dzScene->getNumNodes();
	return s;
}

bool Server::sessionExists( const QString &session ) const
{
	if ( session.isEmpty() )
	{
		return false;
	}
	for ( auto it = m_connections.cbegin(); it != m_connections.cend(); ++it )
	{
		if ( it->ready && it->role == "control" && it->session == session )
		{
			return true;
		}
	}
	return false;
}

void Server::regeneratePairingCode()
{
	m_pairingCode = QString::number( QRandomGenerator::global()->bounded( 100000, 1000000 ) );
}

void Server::log( const QString &line )
{
	dzApp->log( line, c_logCategory );
	emit logMessage( line );
}

} // namespace DazVrBridge

// The SDK builds moc with -i (no header include), so each source pulls in its own moc output.
#include "moc_bridge_server.cpp"
