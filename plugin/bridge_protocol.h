#pragma once

// Wire framing shared by the control and bulk connections.
// See ../protocol/PROTOCOL.md for the message catalog.
//
//   u32   frame_len    bytes that follow this field (little-endian)
//   u32   header_len
//   u8[]  header       UTF-8 JSON object, always has "t" (type) and "seq"
//   u8[]  payload      frame_len - 4 - header_len bytes, often empty

#include <QByteArray>
#include <QJsonObject>
#include <QString>

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

QByteArray	encodeFrame( const QJsonObject &header, const QByteArray &payload = QByteArray() );

// Incremental decoder: feed() whatever the socket delivers, then drain with next().
class FrameDecoder
{
public:
	void	feed( const QByteArray &bytes );
	bool	next( Frame &frame );

	bool	hasError() const { return !m_error.isEmpty(); }
	QString	error() const { return m_error; }

private:
	QByteArray	m_buffer;
	QString		m_error;
};

} // namespace DazVrBridge
