#!/usr/bin/env dotnet
// Summarizes one scripts/run.cs results directory.
//
// Prints Markdown tables (median of the runs, fastest first), writes
// summary.json next to the raw results, and refreshes the results section of
// README.md.
//
// Usage: dotnet scripts/summarize.cs [results/<run>]     (default: the newest run)

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

const string EntryPointKey = "EntryPointFilePath";
const string SystemFile = "system.txt";
const string SummaryFile = "summary.json";

var entryPoint = AppContext.GetData( EntryPointKey ) as string;
if ( entryPoint is null )
{
    throw new InvalidOperationException( "Run this file as a script: dotnet scripts/summarize.cs" );
}

var root = Path.GetFullPath( Path.Combine( entryPoint, "..", ".." ) );
var run = args.Length > 0 ? Path.GetFullPath( args[0] ) : Results.NewestRun( Path.Combine( root, "results" ) );

var system = Results.ReadSystem( Path.Combine( run, SystemFile ) );
var m4 = Results.ReadLemireM4( Path.Combine( root, "lemire", "results_m4max.txt" ) );
var (rows, other) = Results.Summarize( run, system, m4 );

var json = new AppJson( new JsonSerializerOptions
{
    WriteIndented = true,
    IndentSize = 4,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
} );
var summaryJson = JsonSerializer.Serialize( new Summary( system, rows, other ), json.Summary );
await File.WriteAllTextAsync( Path.Combine( run, SummaryFile ), summaryJson + "\n" );

var tables = Report.Markdown( system, rows, other, Path.GetRelativePath( root, run ).Replace( '\\', '/' ) );
await Markers.ReplaceAsync( Path.Combine( root, "README.md" ), "<!-- RESULTS:BEGIN -->", "<!-- RESULTS:END -->", tables );
Console.WriteLine( tables );

// One benchmark results file: its language, how to label its toolchain, and its
// section in Lemire's results. SystemKey and Format are null for C#, whose
// runtime version comes from the program's own header line.
internal sealed record Bench( string Id, string Lang, string? SystemKey, string? Format, string? M4Section );

internal sealed record Sample( string Case, double Ns, double Ms );

internal sealed record Row( string Id, string Lang, string Tool, string Case, double[] Runs, double Ns, double Ms )
{
    public string? Call
    {
        get; init;
    }
    public string? Kind
    {
        get; init;
    }
    public double? M4
    {
        get; init;
    }
}

internal sealed record Summary( Dictionary<string, string> System, List<Row> Rows, List<Row> Other );

[JsonSerializable( typeof( Summary ) )]
internal sealed partial class AppJson : JsonSerializerContext;

internal static partial class Results
{
    private static readonly Bench[] Benches =
    [
        new( "csharp-net11-jit", "C#", null, null, null ),
        new( "csharp-net11-aot", "C#", null, null, null ),
        new( "csharp-net10-jit", "C#", null, null, null ),
        new( "csharp-net10-aot", "C#", null, null, null ),
        new( "cpp-gcc", "C++", "gcc", "GCC {0}, -O3", "C++" ),
        new( "cpp-clang", "C++", "clang", "Clang {0}, -O3", "C++" ),
        new( "rust", "Rust", "rustc", "rustc {0}, release", "Rust" ),
        new( "go", "Go", "go", "Go {0}", "Go" ),
        new( "nim", "Nim", "nim", "Nim {0}, -d:danger", "Nim" ),
        new( "node", "Node.js", "node", "Node {0}", "Node" ),
        new( "bun", "Bun", "bun", "Bun {0}", "Bun" ),
        new( "python", "Python", "python", "CPython {0}", "Python" ),
    ];

    // The integer-to-string cases; everything else is a baseline or a side case.
    private static readonly Dictionary<string, (string Call, string? Kind)> Calls = new( )
    {
        ["i.ToString() ulong"] = ("i.ToString()", "ulong"),
        ["i.ToString() int"] = ("i.ToString()", "int"),
        ["to_string(i)"] = ("std::to_string(i)", null),
        ["i.to_string()"] = ("i.to_string()", null),
        ["itoa + to_owned()"] = ("itoa + to_owned()", null),
        ["strconv.Itoa(i)"] = ("strconv.Itoa(i)", null),
        ["$i"] = ("$i", null),
        ["String(i)"] = ("String(i)", null),
        ["str(i)"] = ("str(i)", null),
    };

    public static string NewestRun( string resultsDir )
    {
        var runs = Directory.GetDirectories( resultsDir );
        if ( runs.Length == 0 )
        {
            throw new InvalidOperationException( $"No runs in {resultsDir}. Run: dotnet scripts/run.cs" );
        }

        return runs.Order( StringComparer.Ordinal ).Last( );
    }

    public static Dictionary<string, string> ReadSystem( string path ) =>
        File.ReadLines( path )
            .Select( l => l.Split( ':', 2 ) )
            .Where( p => p.Length == 2 )
            .ToDictionary( p => p[0].Trim( ), p => p[1].Trim( ) );

    // (section, case) -> M/s from Lemire's Apple M4 Max results.
    public static Dictionary<(string Section, string Case), double> ReadLemireM4( string path )
    {
        var m4 = new Dictionary<(string, string), double>( );
        foreach ( var (header, samples) in ParseBlocks( path ) )
        {
            var section = header.Split( ' ' )[0];
            foreach ( var sample in samples )
            {
                m4[(section, sample.Case)] = sample.Ms;
            }
        }

        return m4;
    }

    public static (List<Row> Rows, List<Row> Other) Summarize(
        string run, Dictionary<string, string> system, Dictionary<(string, string), double> m4 )
    {
        var runs = ExpectedRuns( system );

        var missing = Benches.Where( b => !File.Exists( ResultsFile( run, b ) ) ).Select( b => b.Id ).ToList( );
        if ( missing.Count > 0 )
        {
            throw new InvalidDataException( $"{run} is incomplete. No results for: {string.Join( ", ", missing )}." );
        }

        List<Row> rows = [];
        List<Row> other = [];
        foreach ( var bench in Benches )
        {
            foreach ( var row in SummarizeFile( bench, ResultsFile( run, bench ), system, runs ) )
            {
                if ( Calls.TryGetValue( row.Case, out var call ) )
                {
                    rows.Add( WithCall( row, call, bench, system, m4 ) );
                }
                else
                {
                    other.Add( row );
                }
            }
        }

        return ([.. rows.OrderBy( r => r.Ns )], [.. other.OrderBy( r => r.Case, StringComparer.Ordinal ).ThenBy( r => r.Ns )]);
    }

    private static string ResultsFile( string run, Bench bench ) => Path.Combine( run, $"{bench.Id}.txt" );

    // The report shows the median run, so the run count must be odd.
    private static int ExpectedRuns( Dictionary<string, string> system )
    {
        if ( !system.TryGetValue( "runs", out var text ) || !int.TryParse( text, out var runs ) || runs < 1 )
        {
            throw new InvalidDataException( "system.txt has no valid 'runs' line." );
        }

        if ( runs % 2 == 0 )
        {
            throw new InvalidDataException( $"system.txt says {runs} runs; the median run needs an odd count." );
        }

        return runs;
    }

    // One row per case in a results file, holding its median run.
    private static IEnumerable<Row> SummarizeFile( Bench bench, string path, Dictionary<string, string> system, int runs )
    {
        var blocks = ReadCompleteRuns( path, runs );
        var tool = Toolchain( bench, blocks[^1].Header, system );

        foreach ( var name in blocks[0].Samples.Select( s => s.Case ) )
        {
            var samples = blocks.Select( b => b.Samples.First( s => s.Case == name ) ).ToList( );
            var median = samples.OrderBy( s => s.Ns ).ElementAt( runs / 2 );
            yield return new Row( bench.Id, bench.Lang, tool, name, [.. samples.Select( s => s.Ns )], median.Ns, median.Ms );
        }
    }

    // Reads a results file and checks that it holds exactly `runs` complete runs,
    // each reporting the same cases, so an interrupted run cannot pass as complete.
    private static List<(string Header, List<Sample> Samples)> ReadCompleteRuns( string path, int runs )
    {
        var file = Path.GetFileName( path );
        var blocks = ParseBlocks( path )
            .Select( b => (b.Header, Samples: b.Samples.Select( Normalize ).ToList( )) )
            .ToList( );
        if ( blocks.Count != runs )
        {
            throw new InvalidDataException( $"{file}: expected {runs} runs, found {blocks.Count}." );
        }

        var cases = blocks[0].Samples.Select( s => s.Case ).ToList( );
        if ( cases.Count == 0 )
        {
            throw new InvalidDataException( $"{file}: the first run reports no results." );
        }

        var incomplete = blocks.FindIndex( b => !b.Samples.Select( s => s.Case ).SequenceEqual( cases ) );
        if ( incomplete >= 0 )
        {
            throw new InvalidDataException( $"{file}: run {incomplete + 1} does not report the same cases as run 1." );
        }

        return blocks;
    }

    // JS and Python print p[i&15] + 'x'; C# prints "x". Same case.
    private static Sample Normalize( Sample sample ) => sample with { Case = sample.Case.Replace( '\'', '"' ) };

    private static Row WithCall(
        Row row, (string Call, string? Kind) call, Bench bench, Dictionary<string, string> system,
        Dictionary<(string, string), double> m4 )
    {
        var tool = row.Case == "itoa + to_owned()" ? $"{row.Tool}, itoa {system.GetValueOrDefault( "itoa", "?" )}" : row.Tool;
        double? lemire = bench.M4Section is { } section && m4.TryGetValue( (section, row.Case), out var ms ) ? ms : null;

        return row with
        {
            Call = call.Call,
            Kind = call.Kind,
            Tool = tool,
            M4 = lemire
        };
    }

    private static string Toolchain( Bench bench, string header, Dictionary<string, string> system )
    {
        if ( bench.SystemKey is null || bench.Format is null )
        {
            var version = DotnetVersion( ).Match( header ).Groups[1].Value;
            var mode = header.Contains( "NativeAOT", StringComparison.Ordinal ) ? "NativeAOT" : "JIT";
            return $".NET {version} {mode}";
        }

        if ( !system.TryGetValue( bench.SystemKey, out var line ) )
        {
            throw new InvalidDataException( $"system.txt has no '{bench.SystemKey}' line." );
        }

        return string.Format( bench.Format, Version( ).Match( line ).Value );
    }

    // Yields (header, samples) for each program run in a file. A header is any
    // non-empty line that is not a "<case> <ns> ns/string <M/s> M/s" result.
    private static IEnumerable<(string Header, List<Sample> Samples)> ParseBlocks( string path )
    {
        string? header = null;
        List<Sample> samples = [];
        foreach ( var line in File.ReadLines( path ) )
        {
            var match = ResultLine( ).Match( line );
            if ( match.Success )
            {
                samples.Add( new Sample( match.Groups["case"].Value.Trim( ),
                    double.Parse( match.Groups["ns"].Value ), double.Parse( match.Groups["ms"].Value ) ) );
                continue;
            }

            if ( line.Trim( ).Length == 0 )
            {
                continue;
            }

            if ( header is not null )
            {
                yield return (header, samples);
            }

            header = line.Trim( );
            samples = [];
        }

        if ( header is not null )
        {
            yield return (header, samples);
        }
    }

    [GeneratedRegex( @"^(?<case>.+?)\s+(?<ns>[\d.]+) ns/string\s+(?<ms>[\d.]+) M/s" )]
    private static partial Regex ResultLine( );

    [GeneratedRegex( @"\d+\.\d+(?:\.\d+)?" )]
    private static partial Regex Version( );

    [GeneratedRegex( @"\.NET (\d+\.\d+\.\d+(?:-[a-z]+\.\d+)?)" )]
    private static partial Regex DotnetVersion( );
}

internal static class Report
{
    public static string Markdown( Dictionary<string, string> system, List<Row> rows, List<Row> other, string runPath )
    {
        List<string> lines = [.. Intro( system, rows, runPath )];

        lines.AddRange( Table( rows.Where( r => !IsIntVariant( r ) ) ) );
        lines.AddRange(
        [
            "",
            "C# uses a `ulong` counter here, like the `uint64_t` and `u64` loops in C++ and Rust. " +
            "Python converts 10 million integers (up to 7 digits) instead of 100 million, as in Lemire's code. " +
            "The M4 Max column is from Lemire's post: a different machine, compare loosely. " +
            "His C++ used Apple clang with libc++; both C++ builds here use libstdc++.",
            "",
            "### C# with an `int` counter",
            "",
            "An idiomatic C# variant. It changes both the loop arithmetic and the formatting path " +
            "(`int.ToString()`), so compare it with the C# `ulong` rows, not with the other languages.",
            "",
        ] );
        lines.AddRange( Table( rows.Where( IsIntVariant ) ) );

        lines.AddRange( SideCases( other ) );
        return string.Join( '\n', lines );
    }

    private static bool IsIntVariant( Row row ) => row.Lang == "C#" && row.Kind == "int";

    private static string[] Intro( Dictionary<string, string> s, List<Row> rows, string runPath )
    {
        var runs = rows.Count > 0 ? rows[0].Runs.Length : 0;
        var boost = s.GetValueOrDefault( "boost" ) == "1" ? "on" : "off";

        return
        [
            "## Results",
            "",
            $"{s.GetValueOrDefault( "cpu", "?" )}, {s.GetValueOrDefault( "kernel", "?" )}, " +
            $"`{s.GetValueOrDefault( "governor", "?" )}` governor, boost {boost}, pinning: {s.GetValueOrDefault( "pin", "none" )}. " +
            $"Benchmarks ran one at a time in {runs} rounds; each round ran every benchmark once, starting at a " +
            "different point in the list. Every run reports Lemire's best of 5 passes; the tables show the median " +
            $"run. Raw output: [`{runPath}/`]({runPath}/).",
            "",
        ];
    }

    private static IEnumerable<string> Table( IEnumerable<Row> rows )
    {
        yield return "| Implementation | Toolchain | M strings/s | ns/string | Runs (ns) | M4 Max, Lemire (M/s) |";
        yield return "|---|---|---:|---:|---|---:|";

        foreach ( var r in rows )
        {
            var impl = $"{r.Lang} `{r.Call}`" + (r.Kind is null ? "" : $" ({r.Kind})");
            var lemire = r.M4 is { } v ? $"{v:F1}" : "—";
            var runs = string.Join( " / ", r.Runs.Select( x => $"{x:F2}" ) );
            yield return $"| {impl} | {r.Tool} | {r.Ms:F1} | {r.Ns:F2} | {runs} | {lemire} |";
        }
    }

    private static IEnumerable<string> SideCases( List<Row> other )
    {
        yield return "";
        yield return "<details>";
        yield return "<summary>Side cases: loop, empty string, and concatenation</summary>";
        yield return "";
        yield return "Baselines and a short concatenation, from the JavaScript, Python and C# files.";
        yield return "";
        yield return "| Case | Implementation | ns/string | M/s |";
        yield return "|---|---|---:|---:|";

        foreach ( var r in other )
        {
            yield return $"| `{r.Case}` | {r.Lang}, {r.Tool} | {r.Ns:F2} | {r.Ms:F1} |";
        }

        yield return "";
        yield return "</details>";
    }
}

internal static class Markers
{
    // Replaces the text between two marker strings, which must appear exactly once.
    public static async Task ReplaceAsync( string path, string begin, string end, string body )
    {
        var text = await File.ReadAllTextAsync( path );
        var block = new Regex( $"({Regex.Escape( begin )}).*?({Regex.Escape( end )})", RegexOptions.Singleline );
        if ( block.Count( text ) != 1 )
        {
            throw new InvalidDataException( $"{Path.GetFileName( path )}: expected one {begin} ... {end} block." );
        }

        await File.WriteAllTextAsync( path, block.Replace( text, m => $"{m.Groups[1].Value}\n{body}\n{m.Groups[2].Value}" ) );
    }
}
