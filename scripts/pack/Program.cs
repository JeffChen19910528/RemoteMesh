using System.Formats.Tar;
using System.IO.Compression;

// Packs a folder into a .tar.gz with explicit Unix permissions, so the result is right whichever system builds it
// (a Windows file system has no executable bit). usage: PackArchive <folder> <archive.tar.gz> <rootName>
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: PackArchive <folder> <archive.tar.gz> <rootName>");
    return 2;
}

var folder = Path.GetFullPath(args[0]);
var archive = Path.GetFullPath(args[1]);
var rootName = args[2];

// Programs that must be started: the application, the agent, and the macOS bundle's executable.
var executables = new HashSet<string>(StringComparer.Ordinal) { "RemoteMesh", "RemoteDesktop.Agent" };
const UnixFileMode DirectoryMode = (UnixFileMode)0b111_101_101; // 0755
const UnixFileMode ProgramMode = (UnixFileMode)0b111_101_101;   // 0755
const UnixFileMode DataMode = (UnixFileMode)0b110_100_100;      // 0644

using var file = File.Create(archive);
using var gzip = new GZipStream(file, CompressionLevel.Optimal);
using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, rootName) { Mode = DirectoryMode });
var count = 0;
foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
{
    var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
    var name = rootName + "/" + relative;
    if (Directory.Exists(path))
    {
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name) { Mode = DirectoryMode });
        continue;
    }
    var isProgram = executables.Contains(Path.GetFileName(path));
    using var data = File.OpenRead(path);
    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = isProgram ? ProgramMode : DataMode, DataStream = data });
    count++;
}
Console.WriteLine($"{count} files -> {archive}");
return 0;
