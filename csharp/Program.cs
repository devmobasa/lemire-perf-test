// C# port of Daniel Lemire's string-creation benchmark (lemire/bench.js and
// lemire/bench.cpp): convert i = 0 .. N-1 to a new string and store it in a
// 1024-slot ring buffer. Each case warms up with 1 million iterations, then
// reports the best of 5 timed passes over N = 100 million.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

const ulong N = 100_000_000;
const ulong WarmUp = 1_000_000;
const int Passes = 5;

var mode = RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT";
var gc = GCSettings.IsServerGC ? "server GC" : "workstation GC";
Console.WriteLine( $"{RuntimeInformation.FrameworkDescription} ({mode}, {gc})" );

(string Name, Action<ulong> Run)[] cases =
[
    ( "loop only", Bench.LoopOnly ),
    ( "\"\"", Bench.Literal ),
    ( "i.ToString() ulong", Bench.FromULong ),
    ( "i.ToString() int", Bench.FromInt ),
    ( "p[i&15] + \"x\"", Bench.Concat ),
];

foreach ( var (name, run) in cases )
{
    run( WarmUp );

    var best = double.MaxValue;
    for ( var pass = 0; pass < Passes; pass++ )
    {
        var t0 = Stopwatch.GetTimestamp( );
        run( N );
        var dt = Stopwatch.GetElapsedTime( t0 ).TotalNanoseconds;

        if ( dt < best )
        {
            best = dt;
        }
    }

    // Read the buffer back so the stored strings are observable, like Lemire's "check".
    long total = 0;
    foreach ( var s in Bench.Buf )
    {
        total += s?.Length ?? 0;
    }

    var ns = best / N;
    Console.WriteLine( $"{name,-24} {ns,7:F2} ns/string  {1e3 / ns,8:F1} M/s   (check {total})" );
}

internal static class Bench
{
    public static readonly string?[] Buf = new string?[1024];

    private static readonly string[] Parts = [.. Enumerable.Range( 0, 16 ).Select( k => (( char ) ('a' + k)).ToString( ) )];

    [MethodImpl( MethodImplOptions.NoInlining )]
    public static void LoopOnly( ulong n )
    {
        var buf = Buf;
        for ( ulong i = 0; i < n; i++ )
        {
            buf[i & 1023] = null;
        }
    }

    [MethodImpl( MethodImplOptions.NoInlining )]
    public static void Literal( ulong n )
    {
        var buf = Buf;
        for ( ulong i = 0; i < n; i++ )
        {
            buf[i & 1023] = "";
        }
    }

    [MethodImpl( MethodImplOptions.NoInlining )]
    public static void FromULong( ulong n )
    {
        var buf = Buf;
        for ( ulong i = 0; i < n; i++ )
        {
            buf[i & 1023] = i.ToString( );
        }
    }

    // Idiomatic C# variant: a 32-bit counter formatted with int.ToString(). It changes both
    // the loop arithmetic and the formatting path, so it is not a like-for-like match for the
    // 64-bit C++ and Rust loops; FromULong is the comparable case.
    [MethodImpl( MethodImplOptions.NoInlining )]
    public static void FromInt( ulong n )
    {
        var buf = Buf;
        for ( var i = 0; i < ( int ) n; i++ )
        {
            buf[i & 1023] = i.ToString( );
        }
    }

    [MethodImpl( MethodImplOptions.NoInlining )]
    public static void Concat( ulong n )
    {
        var buf = Buf;
        var p = Parts;
        for ( ulong i = 0; i < n; i++ )
        {
            buf[i & 1023] = p[i & 15] + "x";
        }
    }
}
