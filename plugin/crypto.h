#pragma once

// Everything cryptographic the bridge needs, over Windows CNG.
//
// Why these primitives and not others: the two ends are Qt/C++ and Unity/Mono, and the
// intersection of what both can do WITHOUT shipping a library is narrow. Qt gives us
// SHA-256 and nothing else; Mono gives us RSA, AES-CBC and HMAC with certainty, and
// everything newer (AES-GCM, X25519) only sometimes, depending on the runtime it ends
// up on. So the handshake is RSA-OAEP key transport and the record layer is AES-256-CBC
// with HMAC-SHA256 in encrypt-then-MAC order -- the same construction TLS 1.2 used for
// a decade, assembled from parts rather than invented.
//
// What this protects against: anyone else on the network reading the scene, the poses,
// or the pairing code as they go past, and anyone injecting commands into a session.
// What it does not: an active attacker who already knows the pairing code, and the
// plugin's own port being open. Confidentiality is bound to the code (or to a saved
// session token) by a MAC over the ephemeral key, so a listener who does not know the
// code cannot stand in the middle -- but a client that connects over loopback without
// a code gets privacy from listeners and no authentication at all, which is the right
// trade for a connection that never leaves the machine.

#include <QByteArray>
#include <QJsonObject>
#include <QString>

namespace DazVrBridge {

QByteArray	randomBytes( int count );
QByteArray	sha256( const QByteArray &data );
QByteArray	hmacSha256( const QByteArray &key, const QByteArray &data );
// RFC 5869, SHA-256. Used to turn one shared secret into four independent keys.
QByteArray	hkdf( const QByteArray &ikm, const QByteArray &salt, const QByteArray &info, int length );

bool	aesCbcEncrypt( const QByteArray &key, const QByteArray &iv, const QByteArray &plain, QByteArray &out );
bool	aesCbcDecrypt( const QByteArray &key, const QByteArray &iv, const QByteArray &cipher, QByteArray &out );

// Length-independent compare, so a wrong MAC cannot be found one byte at a time.
bool	constantTimeEquals( const QByteArray &a, const QByteArray &b );

QByteArray	toBase64Url( const QByteArray &raw );
QByteArray	fromBase64Url( const QByteArray &text );

// One connection's ephemeral RSA key. Ephemeral on purpose: nothing the plugin keeps
// on disk can decrypt a session that was captured yesterday.
class RsaKey
{
public:
	RsaKey() = default;
	~RsaKey();
	RsaKey( const RsaKey & ) = delete;
	RsaKey&	operator=( const RsaKey & ) = delete;

	bool	generate( int bits = 2048 );
	bool	valid() const { return m_key != nullptr; }
	QByteArray	modulus() const { return m_modulus; }
	QByteArray	exponent() const { return m_exponent; }
	// RSAES-OAEP, SHA-1 mask (see the .cpp for why that and not SHA-256).
	bool	decryptOaep( const QByteArray &cipher, QByteArray &plain ) const;

private:
	void*		m_key = nullptr;	// BCRYPT_KEY_HANDLE
	QByteArray	m_modulus;
	QByteArray	m_exponent;
};

// The record layer. One per connection, holding both directions' keys and both
// counters. A record is:
//
//   "DZE" 0x01 | u64 counter (LE) | u8[16] iv | AES-256-CBC ciphertext | u8[32] mac
//
// with the MAC taken over everything before it (encrypt-then-MAC), and the counter
// required to increase, so a record cannot be replayed or reordered into the stream.
class SecureChannel
{
public:
	// `asServer` decides which derived key is for sending and which for receiving.
	void	arm( const QByteArray &premaster, const QByteArray &clientNonce,
				 const QByteArray &serverNonce, bool asServer );
	bool	armed() const { return m_armed; }
	void	disarm();

	QByteArray	seal( const QByteArray &body );
	bool		open( const QByteArray &record, QByteArray &body, QString &error );

	static const int	kOverhead = 4 + 8 + 16 + 32 + 16;	// header, iv, mac, padding

private:
	bool		m_armed = false;
	QByteArray	m_sendKey, m_sendMac, m_recvKey, m_recvMac;
	quint64		m_sendCounter = 0;
	quint64		m_recvCounter = 0;
};

// HS256 tokens, so a headset that paired once is not asked for the code again until
// the token expires. The secret never leaves this machine; the token is a bearer
// credential and is treated as one -- it travels only inside an encrypted channel
// after the first handshake, and rotating the secret invalidates every one ever issued.
QByteArray	signToken( const QByteArray &secret, const QJsonObject &claims );
bool		verifyToken( const QByteArray &secret, const QByteArray &token,
						 QJsonObject &claims, QString &error );

} // namespace DazVrBridge
