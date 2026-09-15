#include "bridge_server.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QElapsedTimer>
#include <QFileInfo>
#include <QHostAddress>
#include <QJsonArray>
#include <QJsonDocument>
#include <QNetworkInterface>
#include <QRandomGenerator>
#include <QStringBuilder>
#include <QTcpServer>
#include <QTcpSocket>
#include <QUdpSocket>
#include <QHostInfo>
#include <QTimer>
#include <QUuid>

#include "dzapp.h"
#include "dzscene.h"
#include "dzskeleton.h"
#include "dz3dviewport.h"
#include "dzbone.h"
#include "dzcamera.h"
#include "dzmainwindow.h"
#include "dzrendermgr.h"
#include "dzundostack.h"
#include "dzviewport.h"
#include "dzviewportmgr.h"

#include "crypto.h"
#include "scene_bake.h"
#include "texture_bake.h"
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
	connect( dzScene, &DzScene::sceneLoadStarting, this, [this]() { m_sceneLoading = true; m_sceneBusy = true; if (m_nodeListTimer) m_nodeListTimer->stop(); } );
	connect( dzScene, &DzScene::sceneClearStarting, this, [this]() { m_sceneClearing = true; m_sceneBusy = true; if (m_nodeListTimer) m_nodeListTimer->stop(); } );
	connect( dzScene, &DzScene::sceneLoaded, this, [this]()
	{
		m_sceneLoading = false;
		m_sceneBusy = m_sceneClearing;
		if ( !m_sceneBusy ) onSceneChanged( "loaded" );
	} );
	connect( dzScene, &DzScene::sceneCleared, this, [this]()
	{
		m_sceneClearing = false;
		m_sceneBusy = m_sceneLoading;
		if ( !m_sceneBusy ) onSceneChanged( "cleared" );
	} );
	connect( dzScene, &DzScene::sceneFilenameChanged, this, [this]( const QString & ) { onSceneChanged( "renamed" ); } );

	// Props and figures added or deleted at the desk. nodeListChanged fires once per
	// node, so a .duf drop is a storm of them; coalesce into one scene.changed and one
	// watcher rescan, and stay quiet during a load (sceneLoaded already covers it).
	m_nodeListTimer = new QTimer( this );
	m_nodeListTimer->setSingleShot( true );
	m_nodeListTimer->setInterval( 400 );
	connect( m_nodeListTimer, &QTimer::timeout, this, [this]()
	{
		if ( m_sceneBusy ) return;
		m_poseWatcher->rescan();
		onSceneChanged( "nodes" );
	} );
	connect( dzScene, &DzScene::nodeListChanged, this, &Server::onNodeListChanged );

	// Undo availability, so a VR button can show what it would undo (or grey out).
	// The four signals fire together for one action; coalesce them into one broadcast.
	m_editStateTimer = new QTimer( this );
	m_editStateTimer->setSingleShot( true );
	m_editStateTimer->setInterval( 50 );
	connect( m_editStateTimer, &QTimer::timeout, this, &Server::broadcastEditState );
	const auto editStateDirty = [this]() { if ( !m_connections.isEmpty() ) m_editStateTimer->start(); };
	connect( dzUndoStack, &DzUndoStack::undoAvailable, this, editStateDirty );
	connect( dzUndoStack, &DzUndoStack::redoAvailable, this, editStateDirty );
	connect( dzUndoStack, &DzUndoStack::undoCaptionChanged, this, editStateDirty );
	connect( dzUndoStack, &DzUndoStack::redoCaptionChanged, this, editStateDirty );

	// A render locks Daz against edits, so the client has to know when one starts and
	// stops rather than discovering it through refused commits.
	connect( dzScene, &DzScene::aboutToRender, this, [this]( DzRenderer* ) { broadcastRenderState( true ); } );
	connect( dzScene, &DzScene::renderFinished, this, [this]( DzRenderer* ) { broadcastRenderState( false ); } );

	// Desk-side pose edits -> pose.state to every control connection.
	m_poseWatcher = new PoseWatcher( this );
	connect( m_poseWatcher, &PoseWatcher::figureChanged, this, &Server::onFigureChanged );
	connect( m_poseWatcher, &PoseWatcher::nodeChanged, this, &Server::onNodeChanged );
	m_poseWatcher->rescan();
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
	if ( m_tokenSecret.size() < 32 )
	{
		m_tokenSecret = randomBytes( 32 );
	}
	openBeacon();
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
	closeBeacon();

	const QList<QTcpSocket*> sockets = m_connections.keys();
	for ( QTcpSocket* socket : sockets )
	{
		delete m_connections[ socket ].key;
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
	while ( c.decoder.next( frame, &c.channel ) )
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
	delete it->key;
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

	if ( type == "secure.key" )
	{
		handleSecureKey( c, f );
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

	if ( type == "pose.commit" )
	{
		handlePoseCommit( c, f );
		return;
	}

	if ( type == "selftest.begin" )
	{
		handleSelfTestBegin( c, f );
		return;
	}

	if ( type == "edit.undo" || type == "edit.redo" )
	{
		handleEdit( c, f );
		return;
	}

	if ( type == "select" )
	{
		handleSelect( c, f );
		return;
	}

	if ( type == "pose.request" || type == "node.request" )
	{
		handleStateRequest( c, f );
		return;
	}

	if ( type == "render.begin" )
	{
		handleRender( c, f );
		return;
	}

	if ( type == "node.transform" || type == "camera.set" )
	{
		handleNodeTransform( c, f );
		return;
	}

	if ( type == "node.visible" || type == "node.delete" )
	{
		handleNodeCommand( c, f );
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

// Finding the plugin without being told where it is.
//
// The headset broadcasts one short line and every plugin that hears it answers with
// its address, its port and whether it will want a pairing code. Answering a probe
// rather than shouting a beacon means an idle Daz puts nothing on the network at
// all, and a machine that should not be found simply does not answer.
void Server::openBeacon()
{
	closeBeacon();
	if ( !m_discoverable || !m_server )
	{
		return;
	}
	m_beacon = new QUdpSocket( this );
	// ShareAddress: several Daz instances on one machine may all want to answer.
	if ( !m_beacon->bind( QHostAddress::AnyIPv4, port(),
			QUdpSocket::ShareAddress | QUdpSocket::ReuseAddressHint ) )
	{
		log( "Discovery: could not bind UDP " % QString::number( port() ) % "; the headset will need the address typed in" );
		delete m_beacon;
		m_beacon = nullptr;
		return;
	}
	connect( m_beacon, &QUdpSocket::readyRead, this, &Server::onProbe );
}

void Server::closeBeacon()
{
	if ( !m_beacon )
	{
		return;
	}
	m_beacon->close();
	m_beacon->deleteLater();
	m_beacon = nullptr;
}

void Server::setDiscoverable( bool on )
{
	if ( m_discoverable == on )
	{
		return;
	}
	m_discoverable = on;
	if ( isListening() )
	{
		on ? openBeacon() : closeBeacon();
	}
}

void Server::onProbe()
{
	while ( m_beacon && m_beacon->hasPendingDatagrams() )
	{
		QByteArray datagram;
		datagram.resize( int( m_beacon->pendingDatagramSize() ) );
		QHostAddress from;
		quint16 fromPort = 0;
		m_beacon->readDatagram( datagram.data(), datagram.size(), &from, &fromPort );
		if ( !datagram.startsWith( "DAZVRBRIDGE?1" ) )
		{
			continue;
		}

		QJsonObject o;
		o[ "host" ] = QHostInfo::localHostName();
		o[ "port" ] = int( port() );
		o[ "plugin" ] = pluginVersionString();
		o[ "protocol" ] = kProtocolVersion;
		o[ "pairing" ] = m_pairingRequired;
		o[ "scene" ] = QFileInfo( dzScene->getFilename() ).completeBaseName();
		o[ "clients" ] = m_connections.size();
		const QByteArray reply = QByteArray( "DAZVRBRIDGE!1 " ) +
			QJsonDocument( o ).toJson( QJsonDocument::Compact );
		m_beacon->writeDatagram( reply, from, fromPort );
	}
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

	// Who is this, and what does the key exchange get bound to?
	//
	// Two credentials are accepted: the six digits from the pane, and a token this
	// plugin signed earlier for a headset that has already been through the digits
	// once. Whichever is used also becomes the secret that authenticates the
	// ephemeral key below, so a listener who knows neither cannot stand in the middle.
	const bool remote = !c.socket->peerAddress().isLoopback();
	const QString code = h.value( "code" ).toString();
	const QByteArray token = h.value( "token" ).toString().toUtf8();

	QString tokenError;
	if ( !token.isEmpty() )
	{
		QJsonObject claims;
		if ( verifyToken( m_tokenSecret, token, claims, tokenError ) )
		{
			c.authenticated = true;
			c.bindSecret = token;
		}
	}
	if ( !c.authenticated && !code.isEmpty() && code == m_pairingCode )
	{
		c.authenticated = true;
		c.bindSecret = code.toUtf8();
	}

	if ( !c.authenticated && remote && m_pairingRequired )
	{
		const bool stale = !token.isEmpty();
		sendError( c, f, stale ? "bad_token" : "bad_pairing_code",
			stale ? "saved session rejected (" % tokenError % "); enter the pairing code again"
				  : QString( "pairing code missing or wrong; read it from the VR Bridge pane" ) );
		log( "Rejected " % peerString( c.socket ) % ( stale ? ": " % tokenError : QString( ": wrong pairing code" ) ) );
		c.socket->close();
		return;
	}

	// Encryption. The client says whether it can; this side says whether it must.
	const bool offered = h.value( "crypto" ).toString() == "v1";
	if ( m_encrypt && !offered && remote )
	{
		sendError( c, f, "encryption_required",
			"this Daz only accepts encrypted connections; update the VR client or turn encryption off in the VR Bridge pane" );
		log( "Rejected " % peerString( c.socket ) % ": will not encrypt" );
		c.socket->close();
		return;
	}
	c.wantsCrypto = m_encrypt && offered;
	if ( c.wantsCrypto )
	{
		c.key = new RsaKey();
		c.serverNonce = randomBytes( 16 );
		if ( !c.key->generate( 2048 ) || c.serverNonce.size() != 16 )
		{
			delete c.key;
			c.key = nullptr;
			c.wantsCrypto = false;
			log( "Could not generate a key for " % peerString( c.socket ) % "; this connection stays in the clear" );
		}
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
	if ( role == "control" )
	{
		w[ "edit" ] = undoSummary();
	}
	if ( c.wantsCrypto )
	{
		// The ephemeral public key, this side's nonce, and a MAC over both under the
		// credential. The MAC is what stops a man in the middle: anyone can offer a
		// key, but only this plugin and this headset can prove which key was meant.
		w[ "crypto" ] = "v1";
		// Which credential the MAC below was taken under. The client sends one and
		// the plugin may have ignored it -- an old code left in the field while
		// pairing is switched off -- and a client that guessed wrong would read the
		// mismatch as a man in the middle, which is the one alarm that must not cry wolf.
		w[ "bound" ] = c.authenticated ? ( c.bindSecret == code.toUtf8() ? "code" : "token" ) : "none";
		w[ "key_n" ] = QString::fromLatin1( c.key->modulus().toBase64() );
		w[ "key_e" ] = QString::fromLatin1( c.key->exponent().toBase64() );
		w[ "nonce_s" ] = QString::fromLatin1( c.serverNonce.toBase64() );
		w[ "key_mac" ] = QString::fromLatin1( hmacSha256( c.bindSecret,
			QByteArray( "dazvrbridge key v1" ) + c.key->modulus() + c.key->exponent() + c.serverNonce ).toBase64() );
	}
	send( c.socket, w );

	log( QString( "%1 joined as %2 (%3, session %4)" )
		.arg( h.value( "client" ).toString( "client" ), role, peerString( c.socket ), c.session.left( 8 ) ) );
}

// The client's half of the key exchange: a premaster encrypted to the ephemeral key,
// and the nonce it contributed. Both nonces go into the derivation, so neither side
// alone decides the keys and a captured handshake cannot be replayed at a server that
// has since picked a new one.
//
// Everything after this frame is encrypted in both directions, including the session
// token -- which is a bearer credential and so is never sent in the clear.
void Server::handleSecureKey( Connection &c, const Frame &f )
{
	if ( !c.wantsCrypto || !c.key )
	{
		sendError( c, f, "no_crypto", "this connection did not agree to encryption" );
		return;
	}

	const QByteArray cipher = QByteArray::fromBase64( f.header.value( "k" ).toString().toLatin1() );
	const QByteArray clientNonce = QByteArray::fromBase64( f.header.value( "nonce_c" ).toString().toLatin1() );

	QByteArray premaster;
	if ( clientNonce.size() != 16 || !c.key->decryptOaep( cipher, premaster ) || premaster.size() != 32 )
	{
		sendError( c, f, "bad_key", "the key exchange did not decrypt" );
		log( "Key exchange failed with " % peerString( c.socket ) % "; closing" );
		c.socket->close();
		return;
	}

	c.channel.arm( premaster, clientNonce, c.serverNonce, true );
	delete c.key;					// its work is done; nothing can decrypt this session later
	c.key = nullptr;
	premaster.fill( 0 );

	QJsonObject h;
	h[ "t" ] = "secure.ready";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	h[ "cipher" ] = "aes-256-cbc+hmac-sha256";

	// A token only for a client that proved who it was, only on the control
	// connection, and only now -- inside the channel it just helped set up.
	if ( c.role == "control" && m_tokenDays > 0 && c.authenticated )
	{
		const QByteArray token = issueToken( f.header.value( "client" ).toString( "headset" ) );
		if ( !token.isEmpty() )
		{
			h[ "session_token" ] = QString::fromLatin1( token );
			h[ "token_days" ] = m_tokenDays;
		}
	}
	send( c.socket, h );

	log( "Secured " % peerString( c.socket ) % " (" % c.role % ")" );
}

QByteArray Server::issueToken( const QString &client ) const
{
	if ( m_tokenSecret.size() < 32 )
	{
		return QByteArray();
	}
	const qint64 now = QDateTime::currentSecsSinceEpoch();
	QJsonObject claims;
	claims[ "iss" ] = "daz-vr-bridge";
	claims[ "sub" ] = client;
	claims[ "iat" ] = double( now );
	claims[ "exp" ] = double( now + qint64( m_tokenDays ) * 24 * 60 * 60 );
	claims[ "jti" ] = QUuid::createUuid().toString( QUuid::WithoutBraces );
	return signToken( m_tokenSecret, claims );
}

void Server::forgetPairedClients()
{
	m_tokenSecret = randomBytes( 32 );
	log( "Forgot every paired headset; they will be asked for the pairing code again" );
}

void Server::handleSceneRequest( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", "scene.request belongs on the control connection" );
		return;
	}

	if ( m_sceneBusy || !m_selfTest.figureId.isEmpty() || dzUndoStack->isInUndoRedo() )
	{
		sendError( c, f, "busy", "Daz is busy; retry the scene request shortly" );
		return;
	}
	const BakeOptions opts = bakeOptionsFromJson( f.header );
	log( QString( "Baking scene (textures=%1, influences=%2, meshes=%3)" )
		.arg( opts.textures ).arg( opts.influences ).arg( opts.meshes ? "yes" : "no" ) );

	// The bake's zero-pose freeze fires transformChanged on every bone; that is
	// not a pose the client should hear about.
	m_poseWatcher->setSuppressed( true );
	BakeResult result = bakeScene( opts );
	m_poseWatcher->setSuppressed( false );
	m_poseWatcher->rescan();
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
		auto it = m_assets.find( hash );
		if ( it == m_assets.end() )
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

		// A texture is only described until the first client wants it. Decoding a
		// 4096-square map and compressing a mip chain is seconds of work, which is
		// exactly why it does not happen during the bake -- and why the result is
		// kept, so the second headset asking pays nothing.
		if ( it->kind == "texture" && it->bytes.isEmpty() )
		{
			QElapsedTimer timer;
			timer.start();
			QString error;
			it->bytes = produceTexture( it->texture, &error );
			if ( it->bytes.isEmpty() )
			{
				QJsonObject h;
				h[ "t" ] = "error";
				h[ "seq" ] = m_seq++;
				h[ "ref_seq" ] = f.seq();
				h[ "code" ] = "asset_failed";
				h[ "msg" ] = error.isEmpty() ? QString( "could not produce texture" ) : error;
				h[ "hash" ] = hash;
				send( c.socket, h );
				log( "  texture failed: " % error );
				continue;
			}
			log( QString( "  %1 -> %2x%3 %4, %5 KB in %6 ms" )
				.arg( it->texture.label() )
				.arg( it->texture.width ).arg( it->texture.height )
				.arg( it->texture.alpha ? "BC3" : "BC1" )
				.arg( it->bytes.size() / 1024 ).arg( timer.elapsed() ) );
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

void Server::handlePoseCommit( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", "pose.commit belongs on the control connection" );
		return;
	}

	const bool selfTest = f.header.value( "selftest" ).toBool( false );
	if ( selfTest )
	{
		// Echo of a selftest.begin: apply without undo, compare, restore, report.
		if ( m_selfTest.figureId.isEmpty() || m_selfTest.figureId != f.header.value( "figure" ).toString() )
		{
			sendError( c, f, "selftest_state", "no self-test pending for that figure" );
			return;
		}
		m_poseWatcher->setSuppressed( true );
		const CommitResult r = applyPoseCommit( f.header, QString() );
		QString worst;
		const double maxErr = r.ok ? compareEulers( m_selfTest, &worst ) : 1e9;
		restoreEulers( m_selfTest );
		m_poseWatcher->setSuppressed( false );

		const bool pass = r.ok && maxErr < 0.01;
		QJsonObject h;
		h[ "t" ] = "selftest.result";
		h[ "seq" ] = m_seq++;
		h[ "ref_seq" ] = f.seq();
		h[ "figure" ] = m_selfTest.figureId;
		h[ "pass" ] = pass;
		h[ "bones" ] = r.applied;
		h[ "max_error_deg" ] = maxErr;
		h[ "worst" ] = worst;
		if ( !r.ok ) h[ "error" ] = r.error;
		send( c.socket, h );

		log( QString( "Self-test %1: %2 bones, max error %3 deg (%4)" )
			.arg( pass ? "PASS" : "FAIL" ).arg( r.applied ).arg( maxErr, 0, 'f', 4 ).arg( worst ) );
		m_selfTest = EulerSnapshot();
		return;
	}

	const QString label = f.header.value( "label" ).toString( "VR pose" );
	const CommitResult r = applyPoseCommit( f.header, label );
	if ( !r.ok )
	{
		sendError( c, f, "commit_failed", r.error );
		return;
	}
	log( QString( "Applied pose: %1 bones (\"%2\")" ).arg( r.applied ).arg( label ) );
	// The watcher broadcasts pose.state for this figure ~100 ms later; that is
	// the client's confirmation and carries whatever limits clamped.
}

void Server::handleSelfTestBegin( Connection &c, const Frame &f )
{
	DzSkeleton* figure = qobject_cast<DzSkeleton*>( findNodeById( f.header.value( "figure" ).toString() ) );
	if ( !figure )
	{
		sendError( c, f, "commit_failed", "figure not found" );
		return;
	}

	m_selfTest = snapshotEulers( figure );

	QJsonObject h = poseStateFor( figure );
	h[ "t" ] = "pose.state";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	h[ "selftest" ] = true;
	send( c.socket, h );

	log( QString( "Self-test started on %1 (%2 bones)" ).arg( figure->getLabel() ).arg( m_selfTest.rotDeg.size() ) );
}

void Server::handleNodeTransform( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", f.type() % " belongs on the control connection" );
		return;
	}

	const bool commit = f.header.value( "commit" ).toBool( true );
	const QString label = f.header.value( "label" ).toString( "VR move" );
	const CommitResult r = applyNodeTransform( f.header, commit ? label : QString() );
	if ( !r.ok )
	{
		sendError( c, f, "commit_failed", r.error );
		return;
	}
	if ( commit )
	{
		log( QString( "Moved node (\"%1\")" ).arg( label ) );
	}
	// The watcher broadcasts node.state ~100 ms later as confirmation.
}

// The two edits a panel in VR can ask for that are not a transform: hide something
// that is standing in the way, and delete something that should not be in the scene.
//
// Both go onto Daz's own undo stack, which is the only reason they are safe to offer
// from inside a headset at all: the left hand undoes either without taking it off.
// Deleting also changes the node list, so the watcher's scene.changed follows by
// itself and the client refetches -- nothing here has to tell it what it now has.
void Server::handleNodeCommand( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", f.type() % " belongs on the control connection" );
		return;
	}
	if ( m_sceneBusy || !m_selfTest.figureId.isEmpty() || dzUndoStack->isInUndoRedo() )
	{
		sendError( c, f, "busy", "Daz is busy loading, testing, or undoing" );
		return;
	}

	const QString id = f.header.value( "node" ).toString();
	DzNode* node = findNodeById( id );
	if ( !node )
	{
		sendError( c, f, "unknown_node", "no such node: " % id );
		return;
	}
	if ( qobject_cast<DzBone*>( node ) )
	{
		sendError( c, f, "bad_target", "a bone is part of its figure, not a node of its own" );
		return;
	}

	const bool remove = f.type() == "node.delete";
	const QString label = node->getLabel();
	bool ok = true;

	if ( remove )
	{
		DzUndoStackHold hold;
		ok = dzScene->removeNode( node );
		if ( ok )
		{
			hold.accept( "VR delete: " % label );
		}
		else
		{
			hold.cancel();
		}
	}
	else
	{
		const bool visible = f.header.value( "visible" ).toBool( true );
		DzUndoStackHold hold;
		node->setVisible( visible );
		hold.accept( ( visible ? "VR show: " : "VR hide: " ) % label );
	}

	QJsonObject h;
	h[ "t" ] = "node.result";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	h[ "node" ] = id;
	h[ "action" ] = remove ? "delete" : "visible";
	h[ "ok" ] = ok;
	send( c.socket, h );

	log( QString( "%1 from VR: %2%3" ).arg(
		remove ? "Delete" : "Visibility",
		label,
		ok ? QString() : QString( " (refused)" ) ) );
}

// Undo and redo are Daz's own stack, not a VR-only one: the step this pops is the
// same step Ctrl+Z at the desk would pop, which is the only way the two sides stay
// in agreement about what happened. The caption travels back so VR can say what it
// just undid without the user reading a HUD.
//
// The rollback moves transforms, which PoseWatcher sees and broadcasts as pose.state
// / node.state a moment later; that is the client's real confirmation. Nothing here
// suppresses the watcher, on purpose.
void Server::handleEdit( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", f.type() % " belongs on the control connection" );
		return;
	}

	const bool redo = f.type() == "edit.redo";

	// Re-entering the stack from inside its own undo would corrupt it.
	if ( m_sceneBusy || !m_selfTest.figureId.isEmpty() || dzUndoStack->isInUndoRedo() )
	{
		sendError( c, f, "busy", "Daz is busy loading, testing, or undoing" );
		return;
	}

	const QString caption = redo ? dzUndoStack->getRedoCaption() : dzUndoStack->getUndoCaption();
	const bool available = redo ? dzUndoStack->canRedo() : dzUndoStack->canUndo();
	const bool ok = available && ( redo ? dzUndoStack->redo() : dzUndoStack->undo() );

	QJsonObject h = undoSummary();
	h[ "t" ] = "edit.result";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	h[ "action" ] = redo ? "redo" : "undo";
	h[ "ok" ] = ok;
	h[ "caption" ] = caption;	// what was undone, not what is next
	send( c.socket, h );

	if ( ok )
	{
		log( QString( "%1 from VR: %2" ).arg( redo ? "Redo" : "Undo", caption.isEmpty() ? QString( "(unnamed step)" ) : caption ) );
	}
}

QJsonObject Server::undoSummary() const
{
	QJsonObject e;
	e[ "can_undo" ] = dzUndoStack->canUndo();
	e[ "can_redo" ] = dzUndoStack->canRedo();
	e[ "undo" ] = dzUndoStack->getUndoCaption();
	e[ "redo" ] = dzUndoStack->getRedoCaption();
	return e;
}

void Server::broadcastEditState()
{
	if ( m_connections.isEmpty() )
	{
		return;
	}
	QJsonObject h = undoSummary();
	h[ "t" ] = "edit.state";
	h[ "seq" ] = m_seq++;
	broadcastControl( h );
}

// Mirrors a VR grab into Daz's own selection, so the desk shows what the headset is
// holding. Bones select through their figure: Daz's primary selection is a node, and a
// bone is one, so the same call serves both.
void Server::handleSelect( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", "select belongs on the control connection" );
		return;
	}

	const QString nodeId = f.header.value( "node" ).toString();
	const QString boneId = f.header.value( "bone" ).toString();
	DzNode* node = findNodeById( nodeId );
	if ( !node )
	{
		sendError( c, f, "unknown_node", "no such node: " % nodeId );
		return;
	}

	DzNode* target = node;
	if ( !boneId.isEmpty() )
	{
		if ( DzSkeleton* skeleton = qobject_cast<DzSkeleton*>( node ) )
		{
			if ( DzBone* bone = skeleton->findBone( boneId ) )
			{
				target = bone;
			}
		}
	}

	// Selecting is not an edit and does not belong on the undo stack.
	dzScene->selectAllNodes( false );
	target->select( true );
	dzScene->setPrimarySelection( target );
}

// One node's or figure's current state, on request. The watcher already broadcasts
// changes; this is for a client that suspects it has drifted -- after a draft session,
// or a reconnection -- and wants the truth without re-baking the whole scene.
void Server::handleStateRequest( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", f.type() % " belongs on the control connection" );
		return;
	}

	const bool wantsPose = f.type() == "pose.request";
	const QString id = f.header.value( wantsPose ? "figure" : "node" ).toString();
	DzNode* node = findNodeById( id );
	if ( !node )
	{
		sendError( c, f, "unknown_node", "no such node: " % id );
		return;
	}

	QJsonObject h;
	if ( wantsPose )
	{
		DzSkeleton* skeleton = qobject_cast<DzSkeleton*>( node );
		if ( !skeleton )
		{
			sendError( c, f, "unknown_node", id % " is not a figure" );
			return;
		}
		h = poseStateFor( skeleton );
		h[ "t" ] = "pose.state";
	}
	else
	{
		h = nodeStateFor( node );
		h[ "t" ] = "node.state";
	}
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	send( c.socket, h );
}

// Starts a render of whichever camera the request names, by pointing Daz's active
// viewport at it first -- doRender renders the view, so the view is what has to change.
void Server::handleRender( Connection &c, const Frame &f )
{
	if ( c.role != "control" )
	{
		sendError( c, f, "wrong_connection", "render.begin belongs on the control connection" );
		return;
	}

	DzRenderMgr* renders = dzApp ? dzApp->getRenderMgr() : nullptr;
	if ( !renders )
	{
		sendError( c, f, "not_implemented", "no render manager" );
		return;
	}
	if ( renders->isRendering() )
	{
		sendError( c, f, "busy", "Daz is already rendering" );
		return;
	}

	const QString cameraId = f.header.value( "camera" ).toString();
	if ( !cameraId.isEmpty() )
	{
		DzCamera* camera = qobject_cast<DzCamera*>( findNodeById( cameraId ) );
		DzMainWindow* window = dzApp->getInterface();
		DzViewportMgr* viewports = window ? window->getViewportMgr() : nullptr;
		Dz3DViewport* viewport = viewports ? qobject_cast<Dz3DViewport*>( viewports->getActiveViewport() ) : nullptr;
		if ( camera && viewport )
		{
			viewport->setCamera( camera );
		}
		else if ( camera )
		{
			log( "Render: could not reach the active viewport, rendering its current camera instead" );
		}
	}

	log( "Render started from VR" );
	// aboutToRender / renderFinished do the telling; this only reports that it began.
	const bool started = renders->doRender();
	QJsonObject h;
	h[ "t" ] = "render.state";
	h[ "seq" ] = m_seq++;
	h[ "ref_seq" ] = f.seq();
	h[ "rendering" ] = started;
	h[ "started" ] = started;
	send( c.socket, h );
}

void Server::broadcastRenderState( bool rendering )
{
	if ( m_connections.isEmpty() )
	{
		return;
	}
	QJsonObject h;
	h[ "t" ] = "render.state";
	h[ "seq" ] = m_seq++;
	h[ "rendering" ] = rendering;
	broadcastControl( h );
}

void Server::onNodeListChanged()
{
	if ( m_sceneBusy || m_connections.isEmpty() )
	{
		return;
	}
	m_nodeListTimer->start();	// restarts, so a storm collapses to one shot
}

void Server::onNodeChanged( DzNode* node )
{
	if ( m_connections.isEmpty() )
	{
		return;
	}
	QJsonObject h = nodeStateFor( node );
	h[ "t" ] = "node.state";
	h[ "seq" ] = m_seq++;
	broadcastControl( h );
}

void Server::onFigureChanged( DzSkeleton* figure )
{
	if ( m_connections.isEmpty() )
	{
		return;
	}
	QJsonObject h = poseStateFor( figure );
	h[ "t" ] = "pose.state";
	h[ "seq" ] = m_seq++;
	broadcastControl( h );
}

//////////////////////////////////////////////////////////////////////////
// helpers

void Server::send( QTcpSocket* socket, const QJsonObject &header, const QByteArray &payload )
{
	QByteArray body = encodeBody( header, payload );

	auto it = m_connections.find( socket );
	if ( it != m_connections.end() && it->channel.armed() )
	{
		body = it->channel.seal( body );
		if ( body.isEmpty() )
		{
			// Sealing can only fail if CNG itself failed; carrying on would put a
			// plaintext frame where the client expects a record, which it would read
			// as a protocol error anyway. Say so and drop the connection.
			log( "Could not encrypt a frame for " % peerString( socket ) % "; closing" );
			socket->close();
			return;
		}
	}
	socket->write( frameFromBody( body ) );
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
	// One encode per connection, because each has its own keys. A connection that is
	// part-way through its handshake is skipped rather than sent plaintext it would
	// try to read as a record; it asks for the scene as soon as it is secured anyway.
	const QList<QTcpSocket*> sockets = m_connections.keys();
	for ( QTcpSocket* socket : sockets )
	{
		auto it = m_connections.find( socket );
		if ( it == m_connections.end() || !it->ready || it->role != "control" )
		{
			continue;
		}
		if ( it->wantsCrypto && !it->channel.armed() )
		{
			continue;
		}
		send( socket, header );
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
