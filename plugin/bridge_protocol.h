#pragma once

// Wire framing shared by the control and bulk connections.
// See ../protocol/PROTOCOL.md for the message catalog.
//
//   u32   frame_len    bytes that follow this field (little-endian)
//   u8[]  body          frame_len bytes
//
// and the body is either a plain frame
//
//   u32   header_len
//   u8[]  header       UTF-8 JSON object, always has "t" (type) and "seq"
//   u8[]  payload      frame_len - 4 - header_len bytes, often empty
//
// or, once a connection has been secured, one encrypted record wrapping exactly those
// bytes (see crypto.h). The length prefix stays outside, in the clear, because it is
// how the stream is cut into records in the first place.

#include <QByteArray>
#include <QJsonObject>
#include <QString>

#include "crypto.h"


namespace DazVrBridge {

constexpr int		kProtocolVersion = 1;
constexpr quint16	kDefaultPort = 41427;
// Assets travel on the bulk connection in single frames; keep a sane ceiling.
constexpr quint32	kMaxFrameBytes = 512u * 1024u * 1024u;

struct Frame
{
	QJsonObject	header;
	QByteArray	payload;

	QString	type() const { return header.value( "t" ).toString(); }
	qint64	seq() const { return header.value( "seq" ).toInteger( -1 ); }
};

// The inner body: header length, header, payload. What gets encrypted.
QByteArray	encodeBody( const QJsonObject &header, const QByteArray &payload = QByteArray() );
// Length-prefixes a body (encrypted or not) for the wire.
QByteArray	frameFromBody( const QByteArray &body );
bool		parseBody( const QByteArray &body, Frame &frame, QString &error );

// Convenience for the unencrypted case: encodeBody then frameFromBody.
QByteArray	encodeFrame( const QJsonObject &header, const QByteArray &payload = QByteArray() );

// Incremental decoder: feed() whatever the socket delivers, then drain with next().
class FrameDecoder
{
public:
	void	feed( const QByteArray &bytes );
	// `channel`, when armed, unwraps each body before it is parsed. Passed in rather
	// than held, because the connections live in a QHash whose values move on rehash.
	bool	next( Frame &frame, SecureChannel* channel = nullptr );

	bool	hasError() const { return !m_error.isEmpty(); }
	QString	error() const { return m_error; }

private:
	QByteArray	m_buffer;
	QString		m_error;
};

} // namespace DazVrBridge
