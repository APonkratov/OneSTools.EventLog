using System.Text;
using Microsoft.Extensions.Logging;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class FileLoggerRotationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onestools-log", Guid.NewGuid().ToString("N"));
    private string Log => Path.Combine(_root, "log.txt");
    public FileLoggerRotationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RotationCountsUtf8BytesAndRetainsNewestCompleteMessages()
    {
        var logger = new FileLogger(Log, "test", 200, 2);
        for (var id = 0; id < 5; id++) logger.LogError("record-{Id}: {Text}", id, new string('Ж', 70));
        Assert.Equal(3, Directory.GetFiles(_root).Length);
        Assert.Contains("record-4:", File.ReadAllText(Log));
        Assert.Contains("record-3:", File.ReadAllText(Log + ".1"));
        Assert.Contains("record-2:", File.ReadAllText(Log + ".2"));
        foreach (var file in Directory.GetFiles(_root))
        {
            Assert.Contains(new string('Ж', 70), new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file)));
            Assert.True(new FileInfo(file).Length <= 200);
        }
    }

    [Fact]
    public void OversizedProblemRecordRemainsIntactAndRotatesOnNextMessage()
    {
        var logger = new FileLogger(Log, "test", 200, 2);
        var raw = System.Text.Json.JsonSerializer.Serialize(new string('Ж', 1000) + "\n\"{payload}\"");
        logger.LogError("raw {RawRecordJson}", raw);
        var original = File.ReadAllBytes(Log);
        Assert.Contains(raw, File.ReadAllText(Log));
        Assert.True(original.Length > 200);
        logger.LogError("next record");
        Assert.Equal(original, File.ReadAllBytes(Log + ".1"));
        Assert.DoesNotContain(raw, File.ReadAllText(Log));
    }

    [Fact]
    public void RotationFailureIsReportedInsteadOfPretendingToSaveProblemRecord()
    {
        var logger = new FileLogger(Log, "test", 100, 1);
        logger.LogError(new string('a', 100));
        var original = File.ReadAllText(Log);
        Directory.CreateDirectory(Log + ".1");
        Assert.ThrowsAny<IOException>(() => logger.LogError("problem record"));
        Assert.Equal(original, File.ReadAllText(Log));
    }

    [Fact]
    public void IndependentCategoriesShareTheWriteLock()
    {
        Parallel.For(0, 100, id => new FileLogger(Log, "category-" + id).LogError("unique-record-{Id:D3}", id));
        var text = File.ReadAllText(Log);
        for (var id = 0; id < 100; id++) Assert.Contains($"unique-record-{id:D3}", text);
        Assert.Equal(100, text.Split("unique-record-").Length - 1);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
