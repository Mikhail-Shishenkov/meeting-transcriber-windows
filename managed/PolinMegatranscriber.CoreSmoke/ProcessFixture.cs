using System.Text;

internal static class ProcessFixture
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        switch (arguments.FirstOrDefault())
        {
            case "echo" when arguments.Length == 3:
                await WriteUtf8Async(Console.OpenStandardOutput(), arguments[1]);
                await WriteUtf8Async(Console.OpenStandardError(), arguments[2]);
                return 0;
            case "diagnostic":
                Console.Out.Write(new string('o', 100_000));
                Console.Error.Write(new string('e', 100_000));
                return 0;
            case "wait":
                Console.Out.WriteLine(Environment.ProcessId);
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            default:
                Console.Error.Write("Unknown process fixture mode.");
                return 64;
        }
    }

    private static Task WriteUtf8Async(Stream stream, string value) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
}
