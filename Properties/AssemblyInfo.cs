using System.Runtime.InteropServices;

// Every native dependency in this application is a Windows system DLL.
// Restricting P/Invoke lookup to System32 prevents a same-named file beside
// the wallpaper executable or in the working directory from being loaded.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
