using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace AssumptionZero.Tests;
[TestClass]
public class RustInteropTests
{
    private static string Rust()
    {
        string? path = Environment.GetEnvironmentVariable("RUST_APP_TEST");
        if (path is null || !File.Exists(path)) Assert.Inconclusive("Set RUST_APP_TEST to the built Rust Release executable.");
        return path!;
    }
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
    [TestMethod]
    public async Task FragmentedFixtureReplayMatchesEveryDecisionAndFinalState()
    {
        string rust = Rust(); string output = Path.Combine(Path.GetTempPath(), "az-interop-" + Guid.NewGuid().ToString("N"));
        try
        {
            int code = await BenchmarkRunner.RunAsync(["compare", "--recording", "demo", "--fixture", Path.Combine(Root,"AssumptionZero.App/replay-fixture.json"),
                "--rust-app", rust, "--csharp-app", Path.Combine(Root,"AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll"),
                "--smoke", "--trace", "--fragment-size", "1", "--out", output]);
            Assert.AreEqual(0, code);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "paired-comparison.json")));
            Assert.IsTrue(report.RootElement.GetProperty("Equivalent").GetBoolean());
            Assert.IsFalse(report.RootElement.GetProperty("PerformanceComparisonValid").GetBoolean());
            var traces = Directory.GetFiles(output,"*-decisions.jsonl",SearchOption.AllDirectories);
            Assert.AreEqual(2,traces.Length);
            CollectionAssert.AreEqual(await File.ReadAllLinesAsync(traces[0]),await File.ReadAllLinesAsync(traces[1]));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output,true); }
    }
    private static Process StartRust(string rust, string mode, string url, string output, string? connection = null)
    {
        var start = new ProcessStartInfo(rust) { RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var key in new[] {"REPLAY_FIXTURE","CORRECTNESS_TRACE","BENCHMARK_DIAGNOSTIC","MEASURE_ALLOCATIONS"}) start.Environment.Remove(key);
        start.Environment["EXECUTION_MODE"]=mode;start.Environment["REPLAY_URL"]=url;start.Environment["LOG_EVENTS"]="false";
        if(mode=="Replay") {start.Environment["BENCHMARK_OUTPUT"]=output;start.Environment["BENCHMARK_EXPECTED_FRAMES"]="10";start.Environment["BENCHMARK_WARMUP_FRAMES"]="5";}
        else {start.Environment.Remove("BENCHMARK_OUTPUT");start.Environment["PG_CONNECTION"]=connection!;start.Environment["BINANCE_SBE_URL"]=url+"?streams=";start.Environment["BINANCE_API_KEY"]="local-test";}
        return Process.Start(start)!;
    }
    [TestMethod]
    public async Task RustWritesIncompleteReportOnInvalidReplayControlMessage()
    {
        string rust=Rust();string path=Path.GetTempFileName();
        var recording=new Recording(Guid.NewGuid(),[new(1,0,WebSocketMessageType.Text,"{\"code\":-1}"u8.ToArray())]);
        await using var server=ReplayServer.Create(recording,"http://127.0.0.1:0");await server.StartAsync();
        try
        {
            using var p=StartRust(rust,"Replay",server.Urls.Single().Replace("http://","ws://")+"/stream",path);
            var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            try {await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}finally{if(!p.HasExited)p.Kill(true);}
            await stdout; await stderr; Assert.AreNotEqual(0,p.ExitCode);
            using var report=JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.IsFalse(report.RootElement.GetProperty("Completed").GetBoolean());
            Assert.AreEqual(1,report.RootElement.GetProperty("ReceivedFrames").GetInt32());
            Assert.AreEqual(0,report.RootElement.GetProperty("MeasuredFrames").GetInt32());
        }
        finally {File.Delete(path);}
    }
    [TestMethod]
    [TestCategory("PostgreSQL")]
    public async Task RustRecorderPersistsSharedFormatAndDrainsOnClosure()
    {
        string rust=Rust();string? connection=Environment.GetEnvironmentVariable("PG_TEST_CONNECTION");
        if(string.IsNullOrWhiteSpace(connection))Assert.Inconclusive("Set PG_TEST_CONNECTION for isolated Rust recorder integration.");
        string schema="rust_capture_"+Guid.NewGuid().ToString("N");await using var db=new NpgsqlConnection(connection);await db.OpenAsync();
        await using(var create=new NpgsqlCommand($"CREATE SCHEMA {schema}",db))await create.ExecuteNonQueryAsync();
        try
        {
            var scoped=new NpgsqlConnectionStringBuilder(connection){SearchPath=schema,Pooling=false};
            await using var setup=new NpgsqlConnection(scoped.ConnectionString);await setup.OpenAsync();
            await using(var init=new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"init.sql")),setup))await init.ExecuteNonQueryAsync();
            var original=DemoRecording.Create();var recording=new Recording(original.Id,original.Messages.Take(10));
            await using var server=ReplayServer.Create(recording,"http://127.0.0.1:0",speed:1000);await server.StartAsync();
            using var p=StartRust(rust,"SeedData",server.Urls.Single().Replace("http://","ws://")+"/stream","",scoped.ConnectionString);
            var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            try {await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}finally{if(!p.HasExited)p.Kill(true);}
            await stdout; string error=await stderr;
            // Live capture treats a remote close as failure, but drains every queued insert first.
            Assert.AreNotEqual(0,p.ExitCode);
            var repository=new RecordingRepository(scoped.ConnectionString);var summaries=await repository.ListAsync();
            Assert.AreEqual(1,summaries.Count,error);Assert.AreEqual(10L,summaries[0].MessageCount);
            var captured=await repository.LoadAsync(summaries[0].Id);
            for(int i=0;i<10;i++)CollectionAssert.AreEqual(recording.Messages[i].Payload,captured.Messages[i].Payload);
        }
        finally {await using var drop=new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",db);await drop.ExecuteNonQueryAsync();}
    }
}
