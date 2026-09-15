// Half of the interop harness: exercises plugin/crypto.cpp from the command line so
// the client's C# can be checked against it byte for byte.
//
// Neither end of this bridge can be tested in place -- the plugin needs Daz running
// and the client needs a headset -- so an encrypted channel that "should" work would
// otherwise be found out on the first evening someone tried to use it. This runs both
// implementations against the published test vectors for SHA-256, HMAC and HKDF, and
// then against each other in both directions.
//
// Build and run: tools/crypto_interop/run.ps1

#include <QByteArray>
#include <QJsonObject>

#include <stdio.h>
#include <string.h>

#include "crypto.h"

using namespace DazVrBridge;

namespace {

QByteArray hex( const QByteArray &raw ) { return raw.toHex(); }
QByteArray unhex( const char* text ) { return QByteArray::fromHex( QByteArray( text ) ); }

void line( const char* name, const QByteArray &value )
{
	printf( "%s %s\n", name, value.constData() );
	fflush( stdout );
}

int vectors()
{
	line( "sha256", hex( sha256( "abc" ) ) );
	line( "hmac", hex( hmacSha256( "Jefe", "what do ya want for nothing?" ) ) );

	const QByteArray ikm( 22, '\x0b' );
	line( "hkdf", hex( hkdf( ikm, unhex( "000102030405060708090a0b0c" ),
		unhex( "f0f1f2f3f4f5f6f7f8f9" ), 42 ) ) );

	QByteArray cipher;
	const QByteArray key = unhex( "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" );
	const QByteArray iv = unhex( "000102030405060708090a0b0c0d0e0f" );
	if ( !aesCbcEncrypt( key, iv, "dazvrbridge interop", cipher ) )
	{
		printf( "aescbc FAILED\n" );
		return 1;
	}
	line( "aescbc", hex( cipher ) );
	line( "base64url", toBase64Url( unhex( "fbff0110" ) ) );

	QJsonObject claims;
	claims[ "iss" ] = "daz-vr-bridge";
	claims[ "exp" ] = 4102444800.0;		// 2100-01-01, so the vector does not rot
	line( "token", signToken( "s3cret", claims ) );
	return 0;
}

int sealRecord( char** argv )
{
	SecureChannel channel;
	channel.arm( unhex( argv[2] ), unhex( argv[3] ), unhex( argv[4] ), true );
	const QByteArray record = channel.seal( unhex( argv[5] ) );
	if ( record.isEmpty() )
	{
		printf( "seal FAILED\n" );
		return 1;
	}
	line( "record", hex( record ) );
	return 0;
}

int openRecord( char** argv )
{
	SecureChannel channel;
	channel.arm( unhex( argv[2] ), unhex( argv[3] ), unhex( argv[4] ), true );
	QByteArray body;
	QString error;
	if ( !channel.open( unhex( argv[5] ), body, error ) )
	{
		printf( "open FAILED %s\n", error.toUtf8().constData() );
		return 1;
	}
	line( "body", hex( body ) );
	return 0;
}

// Prints an ephemeral public key, then waits for the other side to encrypt something
// to it and hands back what came out.
int rsa()
{
	RsaKey key;
	if ( !key.generate( 2048 ) )
	{
		printf( "generate FAILED\n" );
		return 1;
	}
	line( "n", hex( key.modulus() ) );
	line( "e", hex( key.exponent() ) );

	char buffer[ 8192 ];
	if ( !fgets( buffer, sizeof( buffer ), stdin ) )
	{
		printf( "no ciphertext on stdin\n" );
		return 1;
	}
	QByteArray plain;
	if ( !key.decryptOaep( QByteArray::fromHex( QByteArray( buffer ).trimmed() ), plain ) )
	{
		printf( "decrypt FAILED\n" );
		return 1;
	}
	line( "plain", hex( plain ) );
	return 0;
}

} // namespace

int main( int argc, char** argv )
{
	if ( argc < 2 ) { printf( "usage: plugin_side vectors|seal|open|rsa\n" ); return 2; }
	const char* mode = argv[1];
	if ( strcmp( mode, "vectors" ) == 0 ) return vectors();
	if ( strcmp( mode, "seal" ) == 0 && argc == 6 ) return sealRecord( argv );
	if ( strcmp( mode, "open" ) == 0 && argc == 6 ) return openRecord( argv );
	if ( strcmp( mode, "rsa" ) == 0 ) return rsa();
	printf( "bad arguments\n" );
	return 2;
}
