#pragma once

// The "VR Bridge" pane: start/stop the server, show the address and pairing
// code the headset needs, and tail the log. The server itself is a singleton
// (see bridge_server.h) so it survives the pane being closed.

#include "dzaction.h"
#include "dzpane.h"

class QCheckBox;
class QLabel;
class QPushButton;
class QSpinBox;
class QTextBrowser;

class DzVrBridgePane : public DzPane
{
	Q_OBJECT
public:
	DzVrBridgePane();
	~DzVrBridgePane() override;

	void	restoreSettings( const DzPaneSettings &settings ) override;
	void	saveSettings( DzPaneSettings &settings ) const override;

private:
	Q_SLOT void	toggleServer();
	Q_SLOT void	forgetPaired();
	Q_SLOT void	updateStatus();
	Q_SLOT void	appendLog( const QString &line );

	QSpinBox*		m_portSpn = nullptr;
	QPushButton*	m_startBtn = nullptr;
	QCheckBox*		m_autoStartChk = nullptr;
	QCheckBox*		m_pairingChk = nullptr;
	QCheckBox*		m_discoverChk = nullptr;
	QCheckBox*		m_encryptChk = nullptr;
	QSpinBox*		m_daysSpn = nullptr;
	QPushButton*	m_forgetBtn = nullptr;
	QLabel*			m_statusLbl = nullptr;
	QLabel*			m_addressLbl = nullptr;
	QLabel*			m_codeLbl = nullptr;
	QLabel*			m_clientsLbl = nullptr;
	QTextBrowser*	m_logEdt = nullptr;
};

// Puts "VR Bridge" in Window > Panes (Tabs) and toggles the pane.
class DzVrBridgePaneAction : public DzPaneAction
{
	Q_OBJECT
public:
	DzVrBridgePaneAction() :
		DzPaneAction( "DzVrBridgePane" ) {}
};
