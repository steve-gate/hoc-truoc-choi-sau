// Fix for WPF project: System.IO is required by File, Path, Directory,
// DirectoryInfo, StreamReader and StreamWriter usages across FocusLock.App.
global using System.IO;
