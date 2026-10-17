using System.IO;
namespace Lenscast;
internal static class Log {
 internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lenscast");
 internal static readonly string FilePath = Path.Combine(DirectoryPath, "журнал.txt");
 internal static void Write(string text, Exception? e = null) {
  lock (DirectoryPath) { Directory.CreateDirectory(DirectoryPath); if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.WriteAllText(FilePath, ""); File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {text} {e}\n"); }
 }
}
