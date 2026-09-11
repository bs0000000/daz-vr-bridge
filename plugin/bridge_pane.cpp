#include "bridge_pane.h"

#include <QCheckBox>
#include <QFormLayout>
#include <QHBoxLayout>
#include <QLabel>
#include <QPushButton>
#include <QSpinBox>
#include <QTextBrowser>
#include <QTextDocument>
#include <QVBoxLayout>

#include "dzapp.h"
#include "dzpanesettings.h"
#include "dzstyle.h"

#include "bridge_server.h"

using DazVrBridge::Server;

namespace {

const char* c_settingPort = "Port";
const char* c_settingAutoStart = "AutoStart";
const char* c_settingPairing = "RequirePairingCode";

const int c_maxLogLines = 500;

} // namespace

DzVrBridgePane::DzVrBridgePane() :
	DzPane( "DazVrBridge" )
{
	setLabel( tr( "VR Bridge" ) );

	const int margin = dzApp->style()->pixelMetric( DZ_PM_GeneralMargin );

	QVBoxLayout* mainLyt = new QVBoxLayout();
	mainLyt->setContentsMargins( margin, margin, margin, margin );
	mainLyt->setSpacing( margin );

	// --- server controls
	QFormLayout* formLyt = new QFormLayout();
	formLyt->setContentsMargins( 0, 0, 0, 0 );

	QHBoxLayout* portLyt = new QHBoxLayout();
	m_portSpn = new QSpinBox();
	m_portSpn->setRange( 1024, 65535 );
	m_portSpn->setValue( DazVrBridge::kDefaultPort );
	m_portSpn->setMinimumWidth( 90 );
	m_startBtn = new QPushButton( tr( "Start" ) );
	portLyt->addWidget( m_portSpn );
	portLyt->addWidget( m_startBtn );
	portLyt->addStretch( 1 );
	formLyt->addRow( tr( "Port" ), portLyt );

	m_pairingChk = new QCheckBox( tr( "Require pairing code from other machines" ) );
	m_pairingChk->setChecked( true );
	formLyt->addRow( QString(), m_pairingChk );

	m_autoStartChk = new QCheckBox( tr( "Start automatically when this pane opens" ) );
	formLyt->addRow( QString(), m_autoStartChk );

	mainLyt->addLayout( formLyt );

	// --- what the headset needs to know
	QFormLayout* infoLyt = new QFormLayout();
	infoLyt->setContentsMargins( 0, 0, 0, 0 );

	m_statusLbl = new QLabel();
	m_addressLbl = new QLabel();
	m_addressLbl->setTextInteractionFlags( Qt::TextSelectableByMouse );
	m_codeLbl = new QLabel();
	m_codeLbl->setTextInteractionFlags( Qt::TextSelectableByMouse );
	// Daz Studio's application stylesheet overrides QFont; a widget stylesheet wins.
	m_codeLbl->setStyleSheet( "font-size: 18pt; font-weight: bold; letter-spacing: 2px;" );
	m_clientsLbl = new QLabel();

	infoLyt->addRow( tr( "Status" ), m_statusLbl );
	infoLyt->addRow( tr( "Address" ), m_addressLbl );
	infoLyt->addRow( tr( "Pairing code" ), m_codeLbl );
	infoLyt->addRow( tr( "Clients" ), m_clientsLbl );
	mainLyt->addLayout( infoLyt );

	// --- log
	// QTextBrowser is what the SDK's own panes use, so it picks up Daz Studio's styling.
	m_logEdt = new QTextBrowser();
	m_logEdt->setReadOnly( true );
	m_logEdt->document()->setMaximumBlockCount( c_maxLogLines );
	m_logEdt->setMinimumHeight( 120 );
	mainLyt->addWidget( m_logEdt, 1 );

	setLayout( mainLyt );
	setMinimumSize( 260, 320 );

	Server* server = Server::instance();
	connect( m_startBtn, &QPushButton::clicked, this, &DzVrBridgePane::toggleServer );
	connect( m_pairingChk, &QCheckBox::toggled, server, &Server::setPairingRequired );
	connect( server, &Server::listeningChanged, this, &DzVrBridgePane::updateStatus );
	connect( server, &Server::connectionCountChanged, this, &DzVrBridgePane::updateStatus );
	connect( server, &Server::logMessage, this, &DzVrBridgePane::appendLog );

	updateStatus();
}

DzVrBridgePane::~DzVrBridgePane()
{
}

void DzVrBridgePane::restoreSettings( const DzPaneSettings &settings )
{
	DzPane::restoreSettings( settings );

	m_portSpn->setValue( settings.getIntValue( c_settingPort, DazVrBridge::kDefaultPort ) );
	m_autoStartChk->setChecked( settings.getBoolValue( c_settingAutoStart, false ) );
	m_pairingChk->setChecked( settings.getBoolValue( c_settingPairing, true ) );

	if ( m_autoStartChk->isChecked() && !Server::instance()->isListening() )
	{
		toggleServer();
	}
}

void DzVrBridgePane::saveSettings( DzPaneSettings &settings ) const
{
	DzPane::saveSettings( settings );

	settings.setIntValue( c_settingPort, m_portSpn->value() );
	settings.setBoolValue( c_settingAutoStart, m_autoStartChk->isChecked() );
	settings.setBoolValue( c_settingPairing, m_pairingChk->isChecked() );
}

void DzVrBridgePane::toggleServer()
{
	Server* server = Server::instance();
	if ( server->isListening() )
	{
		server->stop();
		return;
	}

	server->setPairingRequired( m_pairingChk->isChecked() );
	QString error;
	if ( !server->start( quint16( m_portSpn->value() ), &error ) )
	{
		m_statusLbl->setText( tr( "Failed: %1" ).arg( error ) );
	}
}

void DzVrBridgePane::updateStatus()
{
	Server* server = Server::instance();
	const bool on = server->isListening();

	m_startBtn->setText( on ? tr( "Stop" ) : tr( "Start" ) );
	m_portSpn->setReadOnly( on ); // not setEnabled: Daz's disabled style blanks the text
	m_statusLbl->setText( on ? tr( "Listening" ) : tr( "Stopped" ) );

	if ( on )
	{
		QStringList addrs = Server::lanAddresses();
		if ( addrs.isEmpty() )
		{
			addrs << "127.0.0.1";
		}
		for ( QString &a : addrs )
		{
			a += ":" + QString::number( server->port() );
		}
		m_addressLbl->setText( addrs.join( "\n" ) );
		m_codeLbl->setText( server->pairingCode() );
	}
	else
	{
		m_addressLbl->setText( QString::fromUtf8( "\xE2\x80\x94" ) ); // em dash
		m_codeLbl->setText( QString::fromUtf8( "\xE2\x80\x94" ) );
	}

	m_clientsLbl->setText( QString::number( server->connectionCount() ) );
}

void DzVrBridgePane::appendLog( const QString &line )
{
	m_logEdt->append( line.toHtmlEscaped() );
}

// The SDK builds moc with -i (no header include), so each source pulls in its own moc output.
#include "moc_bridge_pane.cpp"
