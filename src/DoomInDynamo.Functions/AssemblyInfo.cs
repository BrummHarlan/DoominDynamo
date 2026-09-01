using System.Runtime.CompilerServices;

// The WAD pipeline is internal so Dynamo's zero-touch import only sees the one
// RevitToWad class; the smoke test harness still gets to exercise the pipeline
// directly (and headlessly) through this.
[assembly: InternalsVisibleTo("WadSmokeTest")]
