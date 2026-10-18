using System.Diagnostics;
using System.IO;
using System.Text;
namespace Lenscast;

internal record AdbDevice(string Serial, string State, string Name);
internal static class UsbDiscovery {
 internal static string AdbPath => Path.Combine(AppContext.BaseDirectory,"adb","adb.exe");
 private static readonly SemaphoreSlim Commands = new(1,1);

 internal static async Task<List<Phone>> Search(CancellationToken ct = default) => await Search(Run,ct);
 // Injectable command runner keeps parsing and empty/unauthorized/offline cases testable.
 internal static async Task<List<Phone>> Search(Func<string[],CancellationToken,Task<string>> run, CancellationToken ct) {
  List<AdbDevice> devices = [];
  for(int attempt=0;attempt<3;attempt++) {
   devices=ParseDevices(await run(["devices","-l"],ct));
   if(devices.Count>0 && devices[0].State!="offline") break;
   if(attempt<2) await Task.Delay(750,ct); // The USB backend may still be scanning on daemon startup.
  }
  Log.Write($"USB: физических устройств {devices.Count}; состояния: {string.Join(", ",devices.Select(d=>d.State))}");
  if(devices.Count>1) throw new UsbDiscoveryException("Оставьте подключённым один телефон по USB.");
  if(devices.Count==0) throw new UsbDiscoveryException("Windows не видит подключение для отладки. Проверьте драйвер телефона в Диспетчере устройств.");
  var device=devices[0];
  if(device.State=="unauthorized") throw new UsbDiscoveryException("Телефон найден. Разблокируйте его и подтвердите запрос доверия этому ПК.");
  if(device.State!="device") throw new UsbDiscoveryException("Подключение USB не готово. Переподключите кабель.");
  // Enumeration must not depend on shell/getprop or on a running Lenscast server.
  return [new Phone(device.Name,"127.0.0.1",true,device.Serial)];
 }
 internal static List<AdbDevice> ParseDevices(string text) {
  var result=new List<AdbDevice>();
  foreach(var line in text.Split('\n')) {
   var fields=line.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
   if(fields.Length<2 || fields[0]=="List" || fields[0].StartsWith('*')) continue;
   var serial=fields[0];
   if(serial.StartsWith("emulator-",StringComparison.Ordinal) || serial.Contains(':') || serial.Contains("._adb-tls-",StringComparison.Ordinal)) continue;
   if(fields[1] is not ("device" or "offline" or "unauthorized" or "no")) continue;
   var model=fields.FirstOrDefault(p=>p.StartsWith("model:",StringComparison.Ordinal));
   result.Add(new(serial,fields[1],model is null ? "Телефон" : model[6..].Replace('_',' ')));
  }
  return result;
 }
 internal static Task Forward(string serial, CancellationToken ct) => Forward(serial,Run,ct);
 internal static async Task Forward(string serial,Func<string[],CancellationToken,Task<string>> run,CancellationToken ct) {
  if(string.IsNullOrWhiteSpace(serial)) throw new UsbDiscoveryException("Выберите телефон USB из списка.");
  await run(["-s",serial,"forward","tcp:27186","tcp:27186"],ct);
  await run(["-s",serial,"forward","tcp:27185","tcp:27185"],ct);
  Log.Write("USB: перенаправление канала управления выполнено");
 }
 private static async Task<string> Run(string[] args,CancellationToken ct) {
  if(!File.Exists(AdbPath)) throw new UsbDiscoveryException("Компонент USB отсутствует. Переустановите Lenscast.");
  await Commands.WaitAsync(ct);
  try {
   var info=new ProcessStartInfo(AdbPath) { UseShellExecute=false,CreateNoWindow=true,
    RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
   // ADB owns USB interfaces in its daemon. A second daemon on 5038 can see no devices
   // while Android Studio / another tool's normal daemon already owns them.
   foreach(var arg in new[]{"-H","127.0.0.1","-P","5037"}.Concat(args)) info.ArgumentList.Add(arg);
   Log.Write($"USB: adb {string.Join(' ',args)}; сервер 127.0.0.1:5037");
   using var p=Process.Start(info) ?? throw new IOException("Не удалось запустить adb");
   var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
   using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
   try { await p.WaitForExitAsync(timeout.Token); }
   catch { if(!p.HasExited) p.Kill(true); throw; }
   var output=await stdout; var error=await stderr;
   // Device serials/driver messages are diagnostics; never log hello/code/token payloads.
   Log.Write($"USB: код выхода {p.ExitCode}; вывод: {output.Trim()}; сообщения: {error.Trim()}");
   if(p.ExitCode!=0) throw new UsbDiscoveryException("Не удалось подготовить USB. Переподключите кабель и подтвердите доверие на телефоне.");
   return output;
  } catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
  catch(UsbDiscoveryException) { throw; }
  catch(Exception e) { Log.Write("USB: ошибка запуска команды",e); throw new UsbDiscoveryException("Компонент USB не отвечает. Закройте Lenscast и запустите снова."); }
  finally { Commands.Release(); }
 }
}
