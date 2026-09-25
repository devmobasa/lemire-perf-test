#!/usr/bin/env dotnet
// Builds every benchmark, then runs RUNS rounds. Each round runs every benchmark
// once, one process at a time; nothing runs in parallel. Output goes to
// results/<timestamp>/, one file per benchmark, plus system.txt with the machine
// and tool versions.
//
// Usage: dotnet scripts/run.cs [runs]                     (odd number, default: 3)
// Pin to a core like Lemire's run_all.sh:  PIN="taskset -c 8" dotnet scripts/run.cs
//   Pinning also confines GC and JIT helper threads (.NET, Go, JS) to that core.
// Override tools with CXX_GCC, CXX_CLANG, CARGO, RUSTC, GO, NIM, DOTNET, NODE, BUN, PYTHON.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

const string EntryPointKey = "EntryPointFilePath";
const string ResultsDir = "results";
const string SystemFile = "system.txt";
const int DefaultRuns = 3;

var entryPoint = AppContext.GetData( EntryPointKey ) as string;
if ( entryPoint is null )
{
    throw new InvalidOperationException( "Run this file as a script: dotnet scripts/run.cs" );
}

Directory.SetCurrentDirectory( Path.GetFullPath( Path.Combine( entryPoint, "..", ".." ) ) );

// The report shows the median run, so the run count must be odd.
var runs = DefaultRuns;
if ( args.Length > 0 && (!int.TryParse( args[0], out runs ) || runs < 1 || runs % 2 == 0) )
{
    throw new ArgumentException( $"Expected an odd, positive number of runs, got '{args[0]}'." );
}

var pin = (Environment.GetEnvironmentVariable( "PIN" ) ?? "").Split( ' ', StringSplitOptions.RemoveEmptyEntries );
var tools = Tools.FromEnvironment( );
var outDir = Path.Combine( ResultsDir, DateTime.Now.ToString( "yyyy-MM-dd_HHmmss" ) );
Directory.CreateDirectory( outDir );

await Benchmarks.BuildAllAsync( tools );

var system = await Benchmarks.DescribeSystemAsync( tools, pin, runs );
await File.WriteAllLinesAsync( Path.Combine( outDir, SystemFile ), system );
Console.WriteLine( string.Join( '\n', system ) );

// One benchmark process at a time, in rounds. Each round starts at a different
// point in the list, so slow drift in machine state (heat, background load)
// does not always land on the same benchmark.
var benches = Benchmarks.All( tools );
for ( var round = 0; round < runs; round++ )
{
    var offset = round * benches.Length / runs;
    for ( var i = 0; i < benches.Length; i++ )
    {
        var (name, command) = benches[(i + offset) % benches.Length];
        Console.WriteLine( $"== {name} run {round + 1}/{runs}" );
        await Proc.TeeAsync( [.. pin, .. command], Path.Combine( outDir, $"{name}.txt" ) );
    }
}

Console.WriteLine( $"== results in {outDir}" );
Console.WriteLine( "   summarize with: dotnet scripts/summarize.cs" );

// Tool binaries, overridable through environment variables of the same role.
internal sealed record Tools(
    string CxxGcc, string CxxClang, string Cargo, string Rustc, string Go,
    string Nim, string Dotnet, string Node, string Bun, string Python )
{
    public static Tools FromEnvironment( ) => new(
        Env( "CXX_GCC", "g++" ), Env( "CXX_CLANG", "clang++" ), Env( "CARGO", "cargo" ), Env( "RUSTC", "rustc" ),
        Env( "GO", "go" ), Env( "NIM", "nim" ), Env( "DOTNET", "dotnet" ), Env( "NODE", "node" ),
        Env( "BUN", "bun" ), Env( "PYTHON", "python3" ) );

    private static string Env( string name, string fallback ) =>
        Environment.GetEnvironmentVariable( name ) is { Length: > 0 } value ? value : fallback;
}

internal static class Benchmarks
{
    private const string BuildDir = "build";

    public static async Task BuildAllAsync( Tools t )
    {
        Directory.CreateDirectory( BuildDir );
        Console.WriteLine( "== build" );

        await Proc.StepAsync( t.CxxGcc, "-O3", "-std=c++20", "-o", $"{BuildDir}/bench_cpp_gcc", "lemire/bench.cpp" );
        await Proc.StepAsync( t.CxxClang, "-O3", "-std=c++20", "-o", $"{BuildDir}/bench_cpp_clang", "lemire/bench.cpp" );
        await Proc.StepAsync( t.Cargo, "build", "--release", "-q", "--manifest-path", "lemire/rust/Cargo.toml",
            "--target-dir", $"{BuildDir}/rust" );
        await Proc.StepAsync( t.Go, "build", "-o", $"{BuildDir}/bench_go", "lemire/bench.go" );
        await Proc.StepAsync( t.Nim, "c", "-d:danger", "--hints:off", $"--nimcache:{BuildDir}/nimcache",
            $"-o:{BuildDir}/bench_nim", "lemire/bench.nim" );

        // Both C# modes publish against the same runtime pack, so JIT versus NativeAOT
        // compares the compilation mode only, not two runtime patch versions. Each
        // variant gets its own artifacts path; they would otherwise share obj/ and
        // could pick up each other's runtimeconfig.json.
        var os = OperatingSystem.IsMacOS( ) ? "osx" : "linux";
        var rid = $"{os}-{RuntimeInformation.OSArchitecture.ToString( ).ToLowerInvariant( )}";
        foreach ( var version in new[] { 10, 11 } )
        {
            await PublishCSharpAsync( t, version, rid, "jit", "--self-contained", "-p:PublishAot=false" );
            await PublishCSharpAsync( t, version, rid, "aot", "-p:PublishAot=true" );
        }
    }

    private static Task PublishCSharpAsync( Tools t, int version, string rid, string mode, params string[] options ) =>
        Proc.StepAsync(
        [
            t.Dotnet, "publish", "csharp", "-c", "Release", "-f", $"net{version}.0", "-r", rid, .. options,
            "--artifacts-path", $"{BuildDir}/msbuild/cs-net{version}-{mode}",
            "-o", $"{BuildDir}/cs-net{version}-{mode}", "-v", "q", "-nologo",
        ] );

    public static (string Name, string[] Command)[] All( Tools t ) =>
    [
        ( "csharp-net11-jit", [$"{BuildDir}/cs-net11-jit/bench"] ),
        ( "csharp-net11-aot", [$"{BuildDir}/cs-net11-aot/bench"] ),
        ( "csharp-net10-jit", [$"{BuildDir}/cs-net10-jit/bench"] ),
        ( "csharp-net10-aot", [$"{BuildDir}/cs-net10-aot/bench"] ),
        ( "cpp-gcc", [$"{BuildDir}/bench_cpp_gcc"] ),
        ( "cpp-clang", [$"{BuildDir}/bench_cpp_clang"] ),
        ( "rust", [$"{BuildDir}/rust/release/strbench"] ),
        ( "go", [$"{BuildDir}/bench_go"] ),
        ( "nim", [$"{BuildDir}/bench_nim"] ),
        ( "node", [t.Node, "lemire/bench.js"] ),
        ( "bun", [t.Bun, "lemire/bench.js"] ),
        ( "python", [t.Python, "lemire/bench.py"] ),
    ];

    // The "key: value" lines of system.txt; scripts/summarize.cs reads them back.
    // The CPU is recorded only by core count and the time without a timezone, so a
    // published results folder does not identify the machine or its location.
    public static async Task<string[]> DescribeSystemAsync( Tools t, string[] pin, int runs ) =>
    [
        $"date:     {DateTime.Now:yyyy-MM-ddTHH:mm:ss}",
        $"cpu:      {CpuCores( )}",
        $"kernel:   {( await Proc.CaptureAsync( "uname", "-sr" ) ).Trim( )}",
        $"governor: {ReadOr( "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor" )}",
        $"boost:    {ReadOr( "/sys/devices/system/cpu/cpufreq/boost" )}",
        $"pin:      {( pin.Length > 0 ? string.Join( ' ', pin ) : "none" )}",
        $"runs:     {runs}",
        $"gcc:      {FirstLine( await Proc.CaptureAsync( t.CxxGcc, "--version" ) )}",
        $"clang:    {FirstLine( await Proc.CaptureAsync( t.CxxClang, "--version" ) )}",
        $"rustc:    {FirstLine( await Proc.CaptureAsync( t.Rustc, "--version" ) )}",
        $"itoa:     {CargoLockVersion( "lemire/rust/Cargo.lock", "itoa" )}",
        $"go:       {FirstLine( await Proc.CaptureAsync( t.Go, "version" ) )}",
        $"nim:      {FirstLine( await Proc.CaptureAsync( t.Nim, "--version" ) )}",
        $"dotnet:   {NetCoreRuntimes( await Proc.CaptureAsync( t.Dotnet, "--list-runtimes" ) )}",
        $"node:     {FirstLine( await Proc.CaptureAsync( t.Node, "--version" ) )}",
        $"bun:      {FirstLine( await Proc.CaptureAsync( t.Bun, "--version" ) )}",
        $"python:   {FirstLine( await Proc.CaptureAsync( t.Python, "--version" ) )}",
    ];

    private static string FirstLine( string text ) => text.Split( '\n' )[0].Trim( );

    private static string ReadOr( string path ) => File.Exists( path ) ? File.ReadAllText( path ).Trim( ) : "n/a";

    // Physical core count from /proc/cpuinfo ("cpu cores"), falling back to logical processors.
    private static string CpuCores( )
    {
        const string CpuInfo = "/proc/cpuinfo";
        var line = File.Exists( CpuInfo )
            ? File.ReadLines( CpuInfo ).FirstOrDefault( l => l.StartsWith( "cpu cores", StringComparison.Ordinal ) )
            : null;

        if ( line is not null && int.TryParse( line.Split( ':', 2 )[1], out var cores ) )
        {
            return $"{cores}-core processor";
        }

        return $"{Environment.ProcessorCount}-thread processor";
    }

    private static string CargoLockVersion( string lockFile, string crate )
    {
        var lines = File.ReadAllLines( lockFile );
        var index = Array.IndexOf( lines, $"name = \"{crate}\"" );
        if ( index < 0 || index + 1 >= lines.Length )
        {
            return "?";
        }

        return lines[index + 1].Replace( "version = ", "", StringComparison.Ordinal ).Trim( '"', ' ' );
    }

    private static string NetCoreRuntimes( string listRuntimes ) =>
        string.Join( ' ', listRuntimes.Split( '\n' )
            .Where( l => l.StartsWith( "Microsoft.NETCore.App ", StringComparison.Ordinal ) )
            .Select( l => l.Split( ' ' )[1] ) );
}

internal static class Proc
{
    // Runs a build step quietly; prints its output only when it fails.
    public static async Task StepAsync( params string[] argv )
    {
        Console.WriteLine( $"   {string.Join( ' ', argv )}" );

        using var process = Start( argv );
        var stdout = process.StandardOutput.ReadToEndAsync( );
        var stderr = process.StandardError.ReadToEndAsync( );
        await process.WaitForExitAsync( );

        if ( process.ExitCode != 0 )
        {
            await Console.Error.WriteAsync( await stdout + await stderr );
            throw new InvalidOperationException( $"'{argv[0]}' exited with code {process.ExitCode}." );
        }
    }

    // Returns stdout, or stderr when a tool prints its version there.
    public static async Task<string> CaptureAsync( params string[] argv )
    {
        using var process = Start( argv );
        var stdout = process.StandardOutput.ReadToEndAsync( );
        var stderr = process.StandardError.ReadToEndAsync( );
        await process.WaitForExitAsync( );

        var output = await stdout;
        return output.Length > 0 ? output : await stderr;
    }

    // Runs one benchmark, echoing its output and appending it to a results file.
    public static async Task TeeAsync( string[] argv, string path )
    {
        using var process = Start( argv );
        await using var file = File.AppendText( path );
        var stderr = process.StandardError.ReadToEndAsync( );

        while ( await process.StandardOutput.ReadLineAsync( ) is { } line )
        {
            Console.WriteLine( line );
            await file.WriteLineAsync( line );
        }

        await process.WaitForExitAsync( );

        if ( process.ExitCode != 0 )
        {
            await Console.Error.WriteAsync( await stderr );
            throw new InvalidOperationException( $"'{string.Join( ' ', argv )}' exited with code {process.ExitCode}." );
        }
    }

    private static Process Start( string[] argv )
    {
        var info = new ProcessStartInfo( argv[0] )
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach ( var arg in argv[1..] )
        {
            info.ArgumentList.Add( arg );
        }

        Process? process;
        try
        {
            process = Process.Start( info );
        }
        catch ( Win32Exception e )
        {
            throw new InvalidOperationException(
                $"Cannot start '{argv[0]}'. Install it or point the matching environment variable at it.", e );
        }

        if ( process is null )
        {
            throw new InvalidOperationException( $"Starting '{argv[0]}' did not create a process." );
        }

        return process;
    }
}
