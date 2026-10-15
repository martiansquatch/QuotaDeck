namespace QuotaDeck.Core.Readers.Services;

/// <summary>
/// How a reader runs a local executable.
///
/// The Claude CLI (<c>claude --print</c>), the Antigravity probes (<c>ps</c>, <c>lsof</c>) and the
/// keychain fallback (<c>/usr/bin/security</c>) all need the same three things: a bounded wait, a
/// closed stdin, and a kill that escalates when the process ignores SIGTERM. Lives in core so
/// readers can be tested against a scripted runner; the one real implementation
/// (<c>ProcessRunner</c>) lives in the readers project.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
