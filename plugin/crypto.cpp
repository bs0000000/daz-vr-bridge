#include "crypto.h"

#include <QDateTime>
#include <QJsonDocument>
#include <QtEndian>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>

namespace DazVrBridge {

namespace {

inline bool ok( NTSTATUS status ) { return status >= 0; }

// One provider handle per algorithm, opened once. CNG handles are thread-safe for
// use; opening them is not free and this runs per frame on the bulk connection.
BCRYPT_ALG_HANDLE algorithm( LPCWSTR name, DWORD flags )
{
	// Deliberately never closed: they live as long as the plugin does.
	static BCRYPT_ALG_HANDLE aes = nullptr;
	static BCRYPT_ALG_HANDLE sha = nullptr;
	static BCRYPT_ALG_HANDLE hmac = nullptr;
	static BCRYPT_ALG_HANDLE rsa = nullptr;

	BCRYPT_ALG_HANDLE* slot = nullptr;
	if ( wcscmp( name, BCRYPT_AES_ALGORITHM ) == 0 ) slot = &aes;
	else if ( wcscmp( name, BCRYPT_RSA_ALGORITHM ) == 0 ) slot = &rsa;
	else slot = ( flags & BCRYPT_ALG_HANDLE_HMAC_FLAG ) ? &hmac : &sha;

	if ( !*slot )
	{
		BCRYPT_ALG_HANDLE handle = nullptr;
		if ( !ok( BCryptOpenAlgorithmProvider( &handle, name, nullptr, flags ) ) )
		{
			return nullptr;
		}
		if ( slot == &aes )
		{
			if ( !ok( BCryptSetProperty( handle, BCRYPT_CHAINING_MODE,
					PUCHAR( BCRYPT_CHAIN_MODE_CBC ), sizeof( BCRYPT_CHAIN_MODE_CBC ), 0 ) ) )
			{
				BCryptCloseAlgorithmProvider( handle, 0 );
				return nullptr;
			}
		}
		*slot = handle;
	}
	return *slot;
}

bool aesCbc( const QByteArray &key, const QByteArray &iv, const QByteArray &in,
			 QByteArray &out, bool encrypt )
{
	if ( key.size() != 32 || iv.size() != 16 )
	{
		return false;
	}
	BCRYPT_ALG_HANDLE alg = algorithm( BCRYPT_AES_ALGORITHM, 0 );
	if ( !alg )
	{
		return false;
	}

	BCRYPT_KEY_HANDLE handle = nullptr;
	if ( !ok( BCryptGenerateSymmetricKey( alg, &handle, nullptr, 0,
			PUCHAR( key.constData() ), ULONG( key.size() ), 0 ) ) )
	{
		return false;
	}

	// BCryptEncrypt walks the IV buffer forward as it chains, so it must be a copy.
	QByteArray vector = iv;
	ULONG written = 0;
	NTSTATUS status = encrypt
		? BCryptEncrypt( handle, PUCHAR( in.constData() ), ULONG( in.size() ), nullptr,
				PUCHAR( vector.data() ), ULONG( vector.size() ), nullptr, 0, &written, BCRYPT_BLOCK_PADDING )
		: BCryptDecrypt( handle, PUCHAR( in.constData() ), ULONG( in.size() ), nullptr,
				PUCHAR( vector.data() ), ULONG( vector.size() ), nullptr, 0, &written, BCRYPT_BLOCK_PADDING );
	if ( !ok( status ) )
	{
		BCryptDestroyKey( handle );
		return false;
	}

	out.resize( int( written ) );
	vector = iv;
	status = encrypt
		? BCryptEncrypt( handle, PUCHAR( in.constData() ), ULONG( in.size() ), nullptr,
				PUCHAR( vector.data() ), ULONG( vector.size() ),
				PUCHAR( out.data() ), written, &written, BCRYPT_BLOCK_PADDING )
		: BCryptDecrypt( handle, PUCHAR( in.constData() ), ULONG( in.size() ), nullptr,
				PUCHAR( vector.data() ), ULONG( vector.size() ),
				PUCHAR( out.data() ), written, &written, BCRYPT_BLOCK_PADDING );
	BCryptDestroyKey( handle );
	if ( !ok( status ) )
	{
		return false;
	}
	out.resize( int( written ) );
	return true;
}

} // namespace

QByteArray randomBytes( int count )
{
	QByteArray out( count, Qt::Uninitialized );
	if ( !ok( BCryptGenRandom( nullptr, PUCHAR( out.data() ), ULONG( count ),
			BCRYPT_USE_SYSTEM_PREFERRED_RNG ) ) )
	{
		return QByteArray();
	}
	return out;
}

QByteArray sha256( const QByteArray &data )
{
	BCRYPT_ALG_HANDLE alg = algorithm( BCRYPT_SHA256_ALGORITHM, 0 );
	if ( !alg )
	{
		return QByteArray();
	}
	BCRYPT_HASH_HANDLE hash = nullptr;
	if ( !ok( BCryptCreateHash( alg, &hash, nullptr, 0, nullptr, 0, 0 ) ) )
	{
		return QByteArray();
	}
	QByteArray out( 32, Qt::Uninitialized );
	const bool good = ok( BCryptHashData( hash, PUCHAR( data.constData() ), ULONG( data.size() ), 0 ) )
		&& ok( BCryptFinishHash( hash, PUCHAR( out.data() ), 32, 0 ) );
	BCryptDestroyHash( hash );
	return good ? out : QByteArray();
}

QByteArray hmacSha256( const QByteArray &key, const QByteArray &data )
{
	BCRYPT_ALG_HANDLE alg = algorithm( BCRYPT_SHA256_ALGORITHM, BCRYPT_ALG_HANDLE_HMAC_FLAG );
	if ( !alg )
	{
		return QByteArray();
	}
	BCRYPT_HASH_HANDLE hash = nullptr;
	if ( !ok( BCryptCreateHash( alg, &hash, nullptr, 0,
			PUCHAR( key.constData() ), ULONG( key.size() ), 0 ) ) )
	{
		return QByteArray();
	}
	QByteArray out( 32, Qt::Uninitialized );
	const bool good = ok( BCryptHashData( hash, PUCHAR( data.constData() ), ULONG( data.size() ), 0 ) )
		&& ok( BCryptFinishHash( hash, PUCHAR( out.data() ), 32, 0 ) );
	BCryptDestroyHash( hash );
	return good ? out : QByteArray();
}

QByteArray hkdf( const QByteArray &ikm, const QByteArray &salt, const QByteArray &info, int length )
{
	const QByteArray prk = hmacSha256( salt, ikm );			// extract
	QByteArray out;
	QByteArray block;
	quint8 counter = 1;
	while ( out.size() < length )							// expand
	{
		QByteArray input = block;
		input.append( info );
		input.append( char( counter++ ) );
		block = hmacSha256( prk, input );
		if ( block.isEmpty() )
		{
			return QByteArray();
		}
		out.append( block );
	}
	out.resize( length );
	return out;
}

bool aesCbcEncrypt( const QByteArray &key, const QByteArray &iv, const QByteArray &plain, QByteArray &out )
{
	return aesCbc( key, iv, plain, out, true );
}

bool aesCbcDecrypt( const QByteArray &key, const QByteArray &iv, const QByteArray &cipher, QByteArray &out )
{
	return aesCbc( key, iv, cipher, out, false );
}

bool constantTimeEquals( const QByteArray &a, const QByteArray &b )
{
	if ( a.size() != b.size() )
	{
		return false;
	}
	unsigned char diff = 0;
	for ( int i = 0; i < a.size(); ++i )
	{
		diff |= static_cast<unsigned char>( a[i] ) ^ static_cast<unsigned char>( b[i] );
	}
	return diff == 0;
}

QByteArray toBase64Url( const QByteArray &raw )
{
	return raw.toBase64( QByteArray::Base64UrlEncoding | QByteArray::OmitTrailingEquals );
}

QByteArray fromBase64Url( const QByteArray &text )
{
	return QByteArray::fromBase64( text, QByteArray::Base64UrlEncoding | QByteArray::OmitTrailingEquals );
}

//////////////////////////////////////////////////////////////////////////
// RsaKey

RsaKey::~RsaKey()
{
	if ( m_key )
	{
		BCryptDestroyKey( BCRYPT_KEY_HANDLE( m_key ) );
		m_key = nullptr;
	}
}

bool RsaKey::generate( int bits )
{
	BCRYPT_ALG_HANDLE alg = algorithm( BCRYPT_RSA_ALGORITHM, 0 );
	if ( !alg )
	{
		return false;
	}
	BCRYPT_KEY_HANDLE key = nullptr;
	if ( !ok( BCryptGenerateKeyPair( alg, &key, ULONG( bits ), 0 ) ) )
	{
		return false;
	}
	if ( !ok( BCryptFinalizeKeyPair( key, 0 ) ) )
	{
		BCryptDestroyKey( key );
		return false;
	}

	ULONG size = 0;
	if ( !ok( BCryptExportKey( key, nullptr, BCRYPT_RSAPUBLIC_BLOB, nullptr, 0, &size, 0 ) ) )
	{
		BCryptDestroyKey( key );
		return false;
	}
	QByteArray blob( int( size ), Qt::Uninitialized );
	if ( !ok( BCryptExportKey( key, nullptr, BCRYPT_RSAPUBLIC_BLOB,
			PUCHAR( blob.data() ), size, &size, 0 ) ) )
	{
		BCryptDestroyKey( key );
		return false;
	}

	// BCRYPT_RSAKEY_BLOB, then the exponent, then the modulus, both big-endian.
	const BCRYPT_RSAKEY_BLOB* header = reinterpret_cast<const BCRYPT_RSAKEY_BLOB*>( blob.constData() );
	const int offset = int( sizeof( BCRYPT_RSAKEY_BLOB ) );
	const int expLen = int( header->cbPublicExp );
	const int modLen = int( header->cbModulus );
	if ( offset + expLen + modLen > blob.size() )
	{
		BCryptDestroyKey( key );
		return false;
	}
	m_exponent = blob.mid( offset, expLen );
	m_modulus = blob.mid( offset + expLen, modLen );

	if ( m_key )
	{
		BCryptDestroyKey( BCRYPT_KEY_HANDLE( m_key ) );
	}
	m_key = key;
	return true;
}

bool RsaKey::decryptOaep( const QByteArray &cipher, QByteArray &plain ) const
{
	if ( !m_key )
	{
		return false;
	}
	// SHA-1 as OAEP's mask generator, to match the one overload Mono is certain to
	// have. OAEP treats the hash as a random oracle rather than leaning on collision
	// resistance, so this is not the weakness a SHA-1 signature would be -- and a
	// padding the other end refuses would be a far larger one.
	BCRYPT_OAEP_PADDING_INFO padding;
	padding.pszAlgId = BCRYPT_SHA1_ALGORITHM;
	padding.pbLabel = nullptr;
	padding.cbLabel = 0;

	ULONG written = 0;
	if ( !ok( BCryptDecrypt( BCRYPT_KEY_HANDLE( m_key ), PUCHAR( cipher.constData() ),
			ULONG( cipher.size() ), &padding, nullptr, 0, nullptr, 0, &written, BCRYPT_PAD_OAEP ) ) )
	{
		return false;
	}
	plain.resize( int( written ) );
	if ( !ok( BCryptDecrypt( BCRYPT_KEY_HANDLE( m_key ), PUCHAR( cipher.constData() ),
			ULONG( cipher.size() ), &padding, nullptr, 0,
			PUCHAR( plain.data() ), written, &written, BCRYPT_PAD_OAEP ) ) )
	{
		return false;
	}
	plain.resize( int( written ) );
	return true;
}

//////////////////////////////////////////////////////////////////////////
// SecureChannel

namespace {

const char c_recordTag[4] = { 'D', 'Z', 'E', 0x01 };

QByteArray direction( const QByteArray &premaster, const QByteArray &salt, const char* label )
{
	return hkdf( premaster, salt, QByteArray( "dazvrbridge v1 " ) + label, 64 );
}

} // namespace

void SecureChannel::arm( const QByteArray &premaster, const QByteArray &clientNonce,
						 const QByteArray &serverNonce, bool asServer )
{
	// Both nonces go in the salt, so neither side alone decides the keys: replaying a
	// captured handshake at a server that has since picked a new nonce derives nothing.
	const QByteArray salt = clientNonce + serverNonce;
	const QByteArray toServer = direction( premaster, salt, "client to server" );
	const QByteArray toClient = direction( premaster, salt, "server to client" );

	const QByteArray &send = asServer ? toClient : toServer;
	const QByteArray &recv = asServer ? toServer : toClient;
	m_sendKey = send.left( 32 );
	m_sendMac = send.mid( 32, 32 );
	m_recvKey = recv.left( 32 );
	m_recvMac = recv.mid( 32, 32 );
	m_sendCounter = 0;
	m_recvCounter = 0;
	m_armed = m_sendKey.size() == 32 && m_sendMac.size() == 32
		&& m_recvKey.size() == 32 && m_recvMac.size() == 32;
}

void SecureChannel::disarm()
{
	m_armed = false;
	m_sendKey.fill( 0 ); m_sendMac.fill( 0 );
	m_recvKey.fill( 0 ); m_recvMac.fill( 0 );
}

QByteArray SecureChannel::seal( const QByteArray &body )
{
	if ( !m_armed )
	{
		return QByteArray();
	}
	const QByteArray iv = randomBytes( 16 );
	QByteArray cipher;
	if ( iv.size() != 16 || !aesCbcEncrypt( m_sendKey, iv, body, cipher ) )
	{
		return QByteArray();
	}

	QByteArray record;
	record.reserve( 4 + 8 + 16 + cipher.size() + 32 );
	record.append( c_recordTag, 4 );
	char counter[8];
	qToLittleEndian( ++m_sendCounter, counter );
	record.append( counter, 8 );
	record.append( iv );
	record.append( cipher );
	record.append( hmacSha256( m_sendMac, record ) );
	return record;
}

bool SecureChannel::open( const QByteArray &record, QByteArray &body, QString &error )
{
	if ( !m_armed )
	{
		error = "channel is not armed";
		return false;
	}
	if ( record.size() < 4 + 8 + 16 + 16 + 32 || memcmp( record.constData(), c_recordTag, 4 ) != 0 )
	{
		error = "not an encrypted record";
		return false;
	}

	const int macAt = record.size() - 32;
	if ( !constantTimeEquals( record.mid( macAt, 32 ),
			hmacSha256( m_recvMac, QByteArray::fromRawData( record.constData(), macAt ) ) ) )
	{
		error = "record failed its MAC";
		return false;
	}

	const quint64 counter = qFromLittleEndian<quint64>( record.constData() + 4 );
	if ( counter <= m_recvCounter )
	{
		error = "record replayed or out of order";
		return false;
	}
	m_recvCounter = counter;

	// Decrypted only after the MAC passed: nothing that failed authentication is ever
	// fed to a cipher, let alone to a JSON parser.
	const QByteArray iv = record.mid( 12, 16 );
	if ( !aesCbcDecrypt( m_recvKey, iv, record.mid( 28, macAt - 28 ), body ) )
	{
		error = "record would not decrypt";
		return false;
	}
	return true;
}

//////////////////////////////////////////////////////////////////////////
// Tokens

QByteArray signToken( const QByteArray &secret, const QJsonObject &claims )
{
	QJsonObject head;
	head[ "alg" ] = "HS256";
	head[ "typ" ] = "JWT";
	const QByteArray signing =
		toBase64Url( QJsonDocument( head ).toJson( QJsonDocument::Compact ) ) + "." +
		toBase64Url( QJsonDocument( claims ).toJson( QJsonDocument::Compact ) );
	return signing + "." + toBase64Url( hmacSha256( secret, signing ) );
}

bool verifyToken( const QByteArray &secret, const QByteArray &token,
				  QJsonObject &claims, QString &error )
{
	const int second = token.lastIndexOf( '.' );
	if ( second <= 0 || secret.isEmpty() )
	{
		error = "not a token";
		return false;
	}
	const QByteArray signing = token.left( second );
	if ( signing.indexOf( '.' ) <= 0 )
	{
		error = "not a token";
		return false;
	}
	if ( !constantTimeEquals( fromBase64Url( token.mid( second + 1 ) ), hmacSha256( secret, signing ) ) )
	{
		error = "token signature does not match";
		return false;
	}

	const QJsonDocument doc = QJsonDocument::fromJson(
		fromBase64Url( signing.mid( signing.indexOf( '.' ) + 1 ) ) );
	if ( !doc.isObject() )
	{
		error = "token payload is not an object";
		return false;
	}
	claims = doc.object();

	const qint64 now = QDateTime::currentSecsSinceEpoch();
	const qint64 expires = qint64( claims.value( "exp" ).toDouble() );
	if ( expires <= 0 || now >= expires )
	{
		error = "token expired";
		return false;
	}
	return true;
}

} // namespace DazVrBridge
