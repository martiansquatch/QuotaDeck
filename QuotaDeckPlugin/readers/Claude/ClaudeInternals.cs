// The readers assembly's one friend: the readers test project, so the renewal's overlap guard —
// deliberately not part of the public surface — can still be pinned directly by a test.
//
// Declared in this file rather than the project file because QuotaDeck.Readers.csproj is shared by
// the reader PRs in flight and none of them may edit it.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("QuotaDeck.Readers.Tests")]
