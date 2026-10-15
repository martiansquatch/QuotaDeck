namespace QuotaDeck.Readers.Claude;

/// <summary>
/// Where one line about the Claude reader goes.
///
/// A seam rather than a reference: the readers project cannot see the plugin's own log helper (the
/// plugin references this project, not the other way round), and a library that writes to the
/// console on its own is not one that can be tested. The default writes to stderr, which the Logi
/// Plugin Service keeps with the plugin's other output; the plugin may replace it with its own log
/// line, and a test sets it to capture.
/// </summary>
public static class ClaudeLog
{
    private static Action<string>? _sink = DefaultSink;

    /// <summary>Where notes go. Set to nil to silence the reader entirely.</summary>
    public static Action<string>? Sink
    {
        get => _sink;
        set => _sink = value;
    }

    /// <summary>One diagnostic line. Never throws: a log call must not be able to fail a fetch.</summary>
    public static void Note(string message)
    {
        try
        {
            Sink?.Invoke(message);
        }
        catch
        {
            // A sink that throws is the sink's problem, not the reader's.
        }
    }

    private static void DefaultSink(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
        }
    }
}
