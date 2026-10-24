using System.Runtime.CompilerServices;

// The Linux credential stores are internal, and their only failure mode is a silent
// "password was not saved". A test assembly must be able to reach them directly to prove
// that a Linux write actually round-trips instead of degrading.
[assembly: InternalsVisibleTo("RelaxKonOS.ServerCenter.Tests")]
[assembly: InternalsVisibleTo("RelaxKonOS.WindowPreviews.Tests")]

// The diff view is internal, and it can only be proven by rendering it: a row band that stops at
// the text run, or a gutter that loses a column, is invisible to any assertion short of a bitmap.
[assembly: InternalsVisibleTo("RelaxKonOS.GitDiff.Tests")]
