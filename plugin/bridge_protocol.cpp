#include "bridge_protocol.h"

#include <QJsonDocument>
#include <QJsonParseError>
#include <QtEndian>

namespace DazVrBridge {

QByteArray encodeBody( const QJsonObject &header, const QByteArray &payload )
{
	const QByteArray json = QJsonDocument( header ).toJson( QJsonDocument::Compact );

	QByteArray out;
	out.reserve( 4 + json.size() + payload.size() );
	char len[4];
	qToLittleEndian( quint32( json.size() ), len );
	out.append( len, 4 );
	out.append( json );
	out.append( payload );
	return out;
}

QByteArray frameFromBody( const QByteArray &body )
{
	QByteArray out;
	out.reserve( 4 + body.size() );
	char len[4];
	qToLittleEndian( quint32( body.size() ), len );
	out.append( len, 4 );
	out.append( body );
	return out;
}

QByteArray encodeFrame( const QJsonObject &header, const QByteArray &payload )
{
	return frameFromBody( encodeBody( header, payload ) );
}

bool parseBody( const QByteArray &body, Frame &frame, QString &error )
{
	if ( body.size() < 4 )
	{
		error = "frame body is too short";
		return false;
	}
	const quint32 headerLen = qFromLittleEndian<quint32>( body.constData() );
	if ( quint64( headerLen ) + 4ull > quint64( body.size() ) )
	{
		error = "header length exceeds frame";
		return false;
	}

	QJsonParseError perr;
	const QJsonDocument doc = QJsonDocument::fromJson(
		QByteArray( body.constData() + 4, int( headerLen ) ), &perr );
	if ( perr.error != QJsonParseError::NoError || !doc.isObject() )
	{
		error = "header is not a JSON object: " + perr.errorString();
		return false;
	}

	frame.header = doc.object();
	frame.payload = body.mid( 4 + int( headerLen ) );
	return true;
}

void FrameDecoder::feed( const QByteArray &bytes )
{
	if ( m_error.isEmpty() )
	{
		m_buffer.append( bytes );
	}
}

bool FrameDecoder::next( Frame &frame, SecureChannel* channel )
{
	if ( !m_error.isEmpty() || m_buffer.size() < 4 )
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

	QByteArray body = m_buffer.mid( 4, int( frameLen ) );
	m_buffer.remove( 0, int( 4 + frameLen ) );

	if ( channel && channel->armed() )
	{
		QByteArray plain;
		if ( !channel->open( body, plain, m_error ) )
		{
			return false;
		}
		body = plain;
	}

	return parseBody( body, frame, m_error );
}

} // namespace DazVrBridge
