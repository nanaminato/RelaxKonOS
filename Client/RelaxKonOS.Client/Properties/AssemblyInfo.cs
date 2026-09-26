using System.Runtime.CompilerServices;

// The Linux credential stores are internal, and their only failure mode is a silent
// "password was not saved". A test assembly must be able to reach them directly to prove
// that a Linux write actually round-trips instead of degrading.
[assembly: InternalsVisibleTo("RelaxKonOS.ServerCenter.Tests")]
