using System.Runtime.CompilerServices;

// The console app doubles as the engine assembly for the GUI. Keep the surface
// internal to the tool, but visible to the WPF front end.
[assembly: InternalsVisibleTo("UnlockKit")]
