using System.IO;
using SoundSync.Services;
using Xunit;

namespace SoundSync.Tests;

public class ConnectionLogTests
{
    [Fact]
    public void Log_RecordsReasonOnOneLineAndRotatesOversizedFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoundSync-log-" + Guid.NewGuid());
        string path = Path.Combine(directory, "connection.log");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, new string('x', 512 * 1024 + 1));
            new ConnectionLog(path).Write("capture stopped\r\nretry pending");
            Assert.True(File.Exists(path + ".1"));
            string line = Assert.Single(File.ReadAllLines(path));
            Assert.Contains("capture stopped  retry pending", line);
            Assert.True(new FileInfo(path).Length < 1024);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".1");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
