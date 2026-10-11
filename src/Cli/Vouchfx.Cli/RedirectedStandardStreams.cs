using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Vouchfx.Cli;

/// <summary>
/// Makes redirected text independent of the attached terminal's code page.
/// </summary>
internal static class RedirectedStandardStreams
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    /// <summary>
    /// Installs UTF-8 readers/writers only for redirected handles, before command dispatch.
    /// </summary>
    /// <remarks>
    /// Console.InputEncoding and Console.OutputEncoding setters can change a shared Windows
    /// console's code page. Replacing the process's text wrappers avoids that side effect and
    /// leaves interactive handles unchanged. The stdin wrapper reads lazily, so the run command's
    /// byte-level EOF watcher still owns its input without competing buffered reads.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Console retains these wrappers for the process lifetime; their standard handles stay open.")]
    internal static void Initialize()
    {
        if (Console.IsOutputRedirected)
            Console.SetOut(CreateWriter(Console.OpenStandardOutput()));
        if (Console.IsErrorRedirected)
            Console.SetError(CreateWriter(Console.OpenStandardError()));
        if (Console.IsInputRedirected)
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Utf8WithoutBom,
                detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true));
    }

    private static StreamWriter CreateWriter(Stream stream) => new(stream, Utf8WithoutBom,
        bufferSize: 1024, leaveOpen: true)
    {
        AutoFlush = true,
    };
}
