#include "bridge_protocol.h"

#include <QJsonDocument>
#include <QJsonParseError>
#include <QtEndian>

namespace DazVrBridge {

QByteArray encodeFrame( const QJsonObject &header, const QByteArray &payload )
{
	const QByteArray json = QJsonDocument( header ).toJson( QJsonDocument::Compact );
	const quint32 headerLen = quint32( json.size() );
	const quint32 frameLen = 4 + headerLen + quint32( payload.size() );

	QByteArray out;
	out.reserve( 8 + int( frameLen ) );

	char len[4];
	qToLittleEndian( frameLen, len );
	out.append( len, 4 );
	qToLittleEndian( headerLen, len );
	out.append( len, 4 );
	out.append( json );
	out.append( payload );
	return out;
}

void FrameDecoder::feed( const QByteArray &bytes )
{
	if ( m_error.isEmpty() )
	{
		m_buffer.append( bytes );
	}
}

bool FrameDecoder::next( Frame &frame )
{
	if ( !m_error.isEmpty() || m_buffer.size() < 8 )
	{
		return false;
	}

	const quint32 frameLen = qFromLittleEndian<quint32>( m_buffer.constData() );
	if ( frameLen < 4 || frameLen > kMaxFrameBytes )
	{
		m_error = QString( "bad frame length %1" ).arg( frameLen );
		return false;
	}
	if ( quint64( m_buffer.size() ) < 4ull + frameLen )
	{
		return false; // need more bytes
	}

	const quint32 headerLen = qFromLittleEndian<quint32>( m_buffer.constData() + 4 );
	if ( headerLen > frameLen - 4 )
	{
		m_error = "header length exceeds frame";
		return false;
	}

	QJsonParseError perr;
	const QJsonDocument doc = QJsonDocument::fromJson(
		QByteArray( m_buffer.constData() + 8, int( headerLen ) ), &perr );
	if ( perr.error != QJsonParseError::NoError || !doc.isObject() )
	{
		m_error = "header is not a JSON object: " + perr.errorString();
		return false;
	}

	const quint32 payloadLen = frameLen - 4 - headerLen;
	frame.header = doc.object();
	frame.payload = QByteArray( m_buffer.constData() + 8 + headerLen, int( payloadLen ) );
	m_buffer.remove( 0, int( 8 + frameLen ) );
	return true;
}

} // namespace DazVrBridge
