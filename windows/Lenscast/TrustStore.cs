using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace Lenscast;
internal record Trust(string Address, string Fingerprint, string Token);
internal static class TrustStore {
 private static string PathName => Path.Combine(Log.DirectoryPath, "доверие.bin");
 private static List<Trust> All() {
  try { return File.Exists(PathName) ? JsonSerializer.Deserialize<List<Trust>>(ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser)) ?? [] : []; }
  catch (Exception e) { Log.Write("Не удалось прочитать сохранённое доверие",e); return []; }
 }
 internal static Trust? Load(string? address=null) => address is null ? All().LastOrDefault() : All().FirstOrDefault(t=>t.Address==address);
 internal static Trust? ForFingerprint(string fingerprint) => All().FirstOrDefault(t=>t.Fingerprint==fingerprint);
 internal static void Save(Trust trust) { var list=All(); list.RemoveAll(t=>t.Fingerprint==trust.Fingerprint || t.Address==trust.Address); list.Add(trust); Directory.CreateDirectory(Log.DirectoryPath); File.WriteAllBytes(PathName, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(list), null, DataProtectionScope.CurrentUser)); }
 internal static void Forget() { if (File.Exists(PathName)) File.Delete(PathName); }
}
