#include "texture_bake.h"

#include <QCryptographicHash>
#include <QFileInfo>
#include <QImage>
#include <QImageReader>
#include <QStringBuilder>

#include "dzapp.h"
#include "dzimagemgr.h"

namespace DazVrBridge {

namespace {

// Blob layout, little-endian, matching the client's DztTexture:
//
//   "DZT1"  u32 format (1 = BC1, 3 = BC3)  u32 width  u32 height  u32 mips
//   then every mip level, largest first, back to back
//
// No per-level offsets: block counts are arithmetic, and Unity wants the levels
// contiguous in exactly this order anyway.
const char kMagic[ 4 ] = { 'D', 'Z', 'T', '1' };
// Four bytes of magic and four u32s. Named, because describeTexture has to predict
// the exact byte count produceTexture will emit and the two were written apart.
const int kHeaderBytes = 4 + 4 * 4;

// Bumped whenever the conversion itself changes -- the flip, the coverage rescale,
// the encoder. A texture's hash otherwise names only its source maps, so a fix in
// here would leave every client happily serving stale bytes from disk out of a cache
// that had no way to know they were wrong. Twice now.
const int kPipelineVersion = 3;

void writeU32( QByteArray &out, quint32 v )
{
	out.append( char( v & 0xFF ) );
	out.append( char( ( v >> 8 ) & 0xFF ) );
	out.append( char( ( v >> 16 ) & 0xFF ) );
	out.append( char( ( v >> 24 ) & 0xFF ) );
}

// Header only: no decode, which is what keeps the manifest fast.
bool readable( const QString &path, QFileInfo &info, QSize &size )
{
	if ( path.isEmpty() )
	{
		return false;
	}
	info = QFileInfo( path );
	if ( !info.exists() || !info.isFile() )
	{
		return false;
	}
	size = QImageReader( path ).size();
	return size.isValid() && !size.isEmpty();
}

// Daz's own loader first: it reaches formats Qt does not always carry a plugin for,
// and content ships plenty of TIFFs.
bool loadImage( const QString &path, QImage &out )
{
	if ( path.isEmpty() )
	{
		return false;
	}
	if ( DzImageMgr* images = dzApp ? dzApp->getImageMgr() : nullptr )
	{
		images->loadImage( path, out );
	}
	if ( out.isNull() )
	{
		out.load( path );
	}
	return !out.isNull();
}

int blocksFor( int dimension )
{
	return qMax( 1, ( dimension + 3 ) / 4 );
}

// Unity's chain runs all the way to 1x1, and every level below 4x4 still occupies
// one whole block. Counting it any other way makes LoadRawTextureData reject the
// buffer outright, so the arithmetic here has to match theirs exactly.
qint64 chainBytes( int width, int height, int mips, bool alpha )
{
	const int blockBytes = alpha ? 16 : 8;
	qint64 total = 0;
	for ( int level = 0; level < mips; ++level )
	{
		const int w = qMax( 1, width >> level );
		const int h = qMax( 1, height >> level );
		total += qint64( blocksFor( w ) ) * blocksFor( h ) * blockBytes;
	}
	return total;
}

int mipCount( int width, int height )
{
	int mips = 1;
	while ( ( width >> ( mips - 1 ) ) > 1 || ( height >> ( mips - 1 ) ) > 1 )
	{
		++mips;
	}
	return mips;
}

int powerOfTwoAtMost( int value, int cap )
{
	int result = 4;	// never below one block
	while ( result * 2 <= value && result * 2 <= cap )
	{
		result *= 2;
	}
	return result;
}

// ---- block compression
//
// Range-fit: take the bounding box of the block's colours as the two endpoints and
// snap each pixel to the nearest of the four interpolated values. It is not the
// best BC1 encoder there is -- a principal-axis fit would beat it on blocks with a
// diagonal spread -- but it is a few dozen lines, has no failure modes, and the
// difference is invisible on skin at the resolutions that ship.

quint16 to565( int r, int g, int b )
{
	return quint16( ( ( r >> 3 ) << 11 ) | ( ( g >> 2 ) << 5 ) | ( b >> 3 ) );
}

void from565( quint16 c, int &r, int &g, int &b )
{
	r = ( ( c >> 11 ) & 0x1F ) * 255 / 31;
	g = ( ( c >> 5 ) & 0x3F ) * 255 / 63;
	b = ( c & 0x1F ) * 255 / 31;
}

void writeColorBlock( QByteArray &out, const QRgb block[ 16 ] )
{
	int lowR = 255, lowG = 255, lowB = 255;
	int highR = 0, highG = 0, highB = 0;
	for ( int i = 0; i < 16; ++i )
	{
		lowR = qMin( lowR, qRed( block[ i ] ) );   highR = qMax( highR, qRed( block[ i ] ) );
		lowG = qMin( lowG, qGreen( block[ i ] ) ); highG = qMax( highG, qGreen( block[ i ] ) );
		lowB = qMin( lowB, qBlue( block[ i ] ) );  highB = qMax( highB, qBlue( block[ i ] ) );
	}

	// Pull the endpoints in by a sixteenth of the range: the interpolated pair sit
	// at thirds, so the extremes are better served slightly inside the box.
	const int insetR = ( highR - lowR ) >> 4;
	const int insetG = ( highG - lowG ) >> 4;
	const int insetB = ( highB - lowB ) >> 4;
	lowR = qMin( 255, lowR + insetR ); highR = qMax( 0, highR - insetR );
	lowG = qMin( 255, lowG + insetG ); highG = qMax( 0, highG - insetG );
	lowB = qMin( 255, lowB + insetB ); highB = qMax( 0, highB - insetB );

	quint16 c0 = to565( highR, highG, highB );
	quint16 c1 = to565( lowR, lowG, lowB );
	// c0 > c1 selects the four-colour (opaque) mode. Equal endpoints are a flat
	// block; the three-colour mode it would otherwise select has a transparent
	// slot we never want here.
	if ( c0 < c1 )
	{
		qSwap( c0, c1 );
	}

	int palette[ 4 ][ 3 ];
	from565( c0, palette[ 0 ][ 0 ], palette[ 0 ][ 1 ], palette[ 0 ][ 2 ] );
	from565( c1, palette[ 1 ][ 0 ], palette[ 1 ][ 1 ], palette[ 1 ][ 2 ] );
	for ( int k = 0; k < 3; ++k )
	{
		palette[ 2 ][ k ] = ( 2 * palette[ 0 ][ k ] + palette[ 1 ][ k ] ) / 3;
		palette[ 3 ][ k ] = ( palette[ 0 ][ k ] + 2 * palette[ 1 ][ k ] ) / 3;
	}

	quint32 indices = 0;
	for ( int i = 0; i < 16; ++i )
	{
		const int r = qRed( block[ i ] ), g = qGreen( block[ i ] ), b = qBlue( block[ i ] );
		int bestSlot = 0;
		int bestError = INT_MAX;
		for ( int s = 0; s < 4; ++s )
		{
			const int dr = r - palette[ s ][ 0 ];
			const int dg = g - palette[ s ][ 1 ];
			const int db = b - palette[ s ][ 2 ];
			const int error = dr * dr + dg * dg + db * db;
			if ( error < bestError ) { bestError = error; bestSlot = s; }
		}
		indices |= quint32( bestSlot ) << ( i * 2 );
	}

	out.append( char( c0 & 0xFF ) ); out.append( char( c0 >> 8 ) );
	out.append( char( c1 & 0xFF ) ); out.append( char( c1 >> 8 ) );
	writeU32( out, indices );
}

void writeAlphaBlock( QByteArray &out, const QRgb block[ 16 ] )
{
	int low = 255, high = 0;
	for ( int i = 0; i < 16; ++i )
	{
		low = qMin( low, qAlpha( block[ i ] ) );
		high = qMax( high, qAlpha( block[ i ] ) );
	}

	// a0 > a1 is the eight-value mode: both endpoints plus six interpolated. The
	// six-value mode's spare slots for 0 and 255 buy nothing on a cutout map.
	const int a0 = high;
	const int a1 = low;
	int palette[ 8 ];
	palette[ 0 ] = a0;
	palette[ 1 ] = a1;
	if ( a0 > a1 )
	{
		for ( int i = 1; i <= 6; ++i )
		{
			palette[ i + 1 ] = ( ( 7 - i ) * a0 + i * a1 ) / 7;
		}
	}
	else
	{
		for ( int i = 1; i <= 4; ++i )
		{
			palette[ i + 1 ] = ( ( 5 - i ) * a0 + i * a1 ) / 5;
		}
		palette[ 6 ] = 0;
		palette[ 7 ] = 255;
	}

	quint64 indices = 0;
	for ( int i = 0; i < 16; ++i )
	{
		const int a = qAlpha( block[ i ] );
		int bestSlot = 0;
		int bestError = INT_MAX;
		for ( int s = 0; s < 8; ++s )
		{
			const int error = qAbs( a - palette[ s ] );
			if ( error < bestError ) { bestError = error; bestSlot = s; }
		}
		indices |= quint64( bestSlot ) << ( i * 3 );
	}

	out.append( char( a0 ) );
	out.append( char( a1 ) );
	for ( int byte = 0; byte < 6; ++byte )
	{
		out.append( char( ( indices >> ( byte * 8 ) ) & 0xFF ) );
	}
}

// The fraction of an image that would survive the alpha test at this scale.
float coverage( const QImage &image, float scale, int cutoff )
{
	const qint64 total = qint64( image.width() ) * image.height();
	if ( total <= 0 )
	{
		return 0.0f;
	}
	qint64 passed = 0;
	for ( int y = 0; y < image.height(); ++y )
	{
		const QRgb* row = reinterpret_cast<const QRgb*>( image.constScanLine( y ) );
		for ( int x = 0; x < image.width(); ++x )
		{
			if ( qAlpha( row[ x ] ) * scale >= cutoff ) ++passed;
		}
	}
	return float( double( passed ) / double( total ) );
}

// Averaging a cutout's edges as the chain goes down drags alpha toward the middle,
// so fewer and fewer texels clear the cutoff: eyebrows and hair thin out with
// distance, and shimmer as the sampler crosses between levels. Scaling each level's
// alpha until the same proportion passes as at full resolution is the standard cure,
// and costs nothing at runtime because it is baked in here.
void matchCoverage( QImage &level, float target, int cutoff )
{
	if ( target <= 0.0f || target >= 1.0f )
	{
		return;	// fully solid or fully empty: nothing to hold
	}
	float low = 0.0f, high = 4.0f;
	for ( int i = 0; i < 12; ++i )
	{
		const float mid = ( low + high ) * 0.5f;
		if ( coverage( level, mid, cutoff ) < target ) low = mid; else high = mid;
	}
	const float scale = ( low + high ) * 0.5f;
	if ( qAbs( scale - 1.0f ) < 0.01f )
	{
		return;
	}
	for ( int y = 0; y < level.height(); ++y )
	{
		QRgb* row = reinterpret_cast<QRgb*>( level.scanLine( y ) );
		for ( int x = 0; x < level.width(); ++x )
		{
			const int a = qBound( 0, int( qAlpha( row[ x ] ) * scale + 0.5f ), 255 );
			row[ x ] = qRgba( qRed( row[ x ] ), qGreen( row[ x ] ), qBlue( row[ x ] ), a );
		}
	}
}

void compressLevel( const QImage &image, bool alpha, QByteArray &out )
{
	const int width = image.width();
	const int height = image.height();
	for ( int by = 0; by < blocksFor( height ); ++by )
	{
		for ( int bx = 0; bx < blocksFor( width ); ++bx )
		{
			QRgb block[ 16 ];
			for ( int y = 0; y < 4; ++y )
			{
				for ( int x = 0; x < 4; ++x )
				{
					// Levels narrower than four pixels still fill a whole block;
					// clamping to the edge is what every encoder does there.
					const int sx = qMin( bx * 4 + x, width - 1 );
					const int sy = qMin( by * 4 + y, height - 1 );
					block[ y * 4 + x ] = image.pixel( sx, sy );
				}
			}
			if ( alpha )
			{
				writeAlphaBlock( out, block );
			}
			writeColorBlock( out, block );
		}
	}
}

} // namespace

QString TextureRef::label() const
{
	const QString color = colorPath.isEmpty() ? QString() : QFileInfo( colorPath ).fileName();
	const QString opacity = opacityPath.isEmpty() ? QString() : QFileInfo( opacityPath ).fileName();
	if ( color.isEmpty() ) return opacity;
	if ( opacity.isEmpty() ) return color;
	return color % " + " % opacity;
}

TextureRef describeTexture( const QString &colorPath, const QString &opacityPath, int texMax )
{
	TextureRef ref;

	QFileInfo colorInfo, opacityInfo;
	QSize colorSize, opacitySize;
	if ( readable( colorPath, colorInfo, colorSize ) )
	{
		ref.colorPath = colorInfo.absoluteFilePath();
	}
	if ( readable( opacityPath, opacityInfo, opacitySize ) )
	{
		ref.opacityPath = opacityInfo.absoluteFilePath();
	}
	if ( ref.colorPath.isEmpty() && ref.opacityPath.isEmpty() )
	{
		return ref;
	}

	// The colour map sets the resolution when there is one; an opacity-only surface
	// (eyelashes, a tear film) is sized by its own map.
	const QSize source = ref.colorPath.isEmpty() ? opacitySize : colorSize;
	ref.alpha = !ref.opacityPath.isEmpty();
	ref.width = powerOfTwoAtMost( source.width(), qMax( 4, texMax ) );
	ref.height = powerOfTwoAtMost( source.height(), qMax( 4, texMax ) );
	ref.mips = mipCount( ref.width, ref.height );
	ref.size = kHeaderBytes + chainBytes( ref.width, ref.height, ref.mips, ref.alpha );

	// The sources' identity, not their pixels. Two surfaces sharing maps share one
	// asset and one cache entry; editing a map in place changes its modification
	// time and so produces a new one.
	QCryptographicHash digest( QCryptographicHash::Sha1 );
	const auto identify = [&digest]( const QString &path, const QFileInfo &info )
	{
		digest.addData( path.toUtf8() );
		if ( path.isEmpty() ) return;
		digest.addData( QByteArray::number( info.lastModified().toMSecsSinceEpoch() ) );
		digest.addData( QByteArray::number( info.size() ) );
	};
	identify( ref.colorPath, colorInfo );
	identify( ref.opacityPath, opacityInfo );
	digest.addData( QByteArray::number( ref.width ) );
	digest.addData( QByteArray::number( ref.height ) );
	digest.addData( ref.alpha ? "bc3" : "bc1" );
	digest.addData( QByteArray::number( kPipelineVersion ) );
	ref.hash = "sha1:" % QString::fromLatin1( digest.result().toHex() );
	return ref;
}

QByteArray produceTexture( const TextureRef &ref, QString* errorOut )
{
	if ( !ref.isValid() )
	{
		if ( errorOut ) *errorOut = "invalid texture reference";
		return QByteArray();
	}

	QImage image;
	if ( !ref.colorPath.isEmpty() && !loadImage( ref.colorPath, image ) )
	{
		if ( errorOut ) *errorOut = "could not decode " % ref.colorPath;
		return QByteArray();
	}
	if ( image.isNull() )
	{
		// Opacity without a colour map -- eyelashes, a tear film. White, so the
		// material's own base colour carries the tint.
		image = QImage( ref.width, ref.height, QImage::Format_ARGB32 );
		image.fill( QColor( 255, 255, 255 ) );
	}
	if ( image.format() != QImage::Format_ARGB32 )
	{
		image = image.convertToFormat( QImage::Format_ARGB32 );
	}
	if ( image.width() != ref.width || image.height() != ref.height )
	{
		image = image.scaled( ref.width, ref.height, Qt::IgnoreAspectRatio, Qt::SmoothTransformation );
	}

	// Daz stores opacity as a greyscale image in its own file, so its luminance has
	// to become this texture's alpha. Without this step BC3 would carry the 255 that
	// an opaque JPEG decodes to and clip nothing at all.
	if ( !ref.opacityPath.isEmpty() )
	{
		QImage mask;
		if ( !loadImage( ref.opacityPath, mask ) )
		{
			if ( errorOut ) *errorOut = "could not decode " % ref.opacityPath;
			return QByteArray();
		}
		if ( mask.format() != QImage::Format_ARGB32 )
		{
			mask = mask.convertToFormat( QImage::Format_ARGB32 );
		}
		if ( mask.width() != ref.width || mask.height() != ref.height )
		{
			mask = mask.scaled( ref.width, ref.height, Qt::IgnoreAspectRatio, Qt::SmoothTransformation );
		}
		for ( int y = 0; y < ref.height; ++y )
		{
			const QRgb* maskRow = reinterpret_cast<const QRgb*>( mask.constScanLine( y ) );
			QRgb* row = reinterpret_cast<QRgb*>( image.scanLine( y ) );
			for ( int x = 0; x < ref.width; ++x )
			{
				row[ x ] = qRgba( qRed( row[ x ] ), qGreen( row[ x ] ), qBlue( row[ x ] ), qGray( maskRow[ x ] ) );
			}
		}
	}

	// QImage puts scanline zero at the TOP; Unity's raw texture data puts row zero at
	// the BOTTOM, the same way its UVs run. Uploading QImage order unflipped turns
	// every map upside down -- lips on the forehead, which is exactly how it looked.
	// Flipping the source once is enough: every mip is scaled from it.
	image = image.mirrored( false, true );

	QByteArray out;
	out.reserve( int( ref.size ) );
	out.append( kMagic, 4 );
	writeU32( out, ref.alpha ? 3 : 1 );
	writeU32( out, quint32( ref.width ) );
	writeU32( out, quint32( ref.height ) );
	writeU32( out, quint32( ref.mips ) );
	Q_ASSERT( out.size() == kHeaderBytes );

	const int cutoff = int( kAlphaCutoff * 255.0f );
	const float baseCoverage = ref.alpha ? coverage( image, 1.0f, cutoff ) : 0.0f;

	QImage level = image;
	for ( int mip = 0; mip < ref.mips; ++mip )
	{
		const int w = qMax( 1, ref.width >> mip );
		const int h = qMax( 1, ref.height >> mip );
		if ( level.width() != w || level.height() != h )
		{
			level = image.scaled( w, h, Qt::IgnoreAspectRatio, Qt::SmoothTransformation );
			if ( ref.alpha )
			{
				matchCoverage( level, baseCoverage, cutoff );
			}
		}
		compressLevel( level, ref.alpha, out );
	}

	if ( out.size() != ref.size )
	{
		// The client sizes its buffer from the manifest, so a mismatch here would
		// surface as a rejected upload rather than as anything legible.
		if ( errorOut )
		{
			*errorOut = QString( "size mismatch for %1: produced %2, described %3" )
				.arg( ref.label() ).arg( out.size() ).arg( ref.size );
		}
		return QByteArray();
	}
	return out;
}

} // namespace DazVrBridge
