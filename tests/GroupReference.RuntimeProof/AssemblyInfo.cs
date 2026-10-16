using System.Runtime.CompilerServices;

// Tests can inspect the owned executable's probes. Production persistence and
// crypto visibility remain unchanged; this assembly contains no deployment API.
[assembly: InternalsVisibleTo("Platform.Persistence.Tests")]
