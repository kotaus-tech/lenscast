using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Lenscast;
internal record Phone(string Name, string Address, bool Usb) { public string Label => $"{Name} · {(Usb ? "USB" : "Wi-Fi")} · готов к подключению"; }
internal sealed class UsbDiscoveryException(string message) : Exception(message) { }
internal static class Discovery {
 internal static async Task<List<Phone>> Usb() {
  var adb=Path.Combine(AppContext.BaseDirectory,"adb","adb.exe");
  if(!File.Exists(adb)) throw new FileNotFoundException("Не найден компонент USB. Переустановите Lenscast.");
  var output=await Run(adb,"devices");
  var devices=output.Split('\n').Select(l=>l.Trim().Split('\t')).Where(p=>p.Length==2 && p[1]=="device").ToList();
  if(devices.Count>1) throw new UsbDiscoveryException("Подключите только один телефон по USB.");
  var result=new List<Phone>();
  foreach(var d in devices) {
   await Run(adb,"-s",d[0],"forward","tcp:27185","tcp:27185");
   var name=(await Run(adb,"-s",d[0],"shell","getprop","ro.product.model")).Trim();
   result.Add(new(name,"127.0.0.1",true));
  }
  if(result.Count==0 && output.Contains("unauthorized")) throw new UsbDiscoveryException("Подтвердите отладку по USB на телефоне и повторите поиск.");
  return result;
 }
 private static async Task<string> Run(string file,params string[] args) {
  var info=new ProcessStartInfo(file) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
  // Keep Lenscast's daemon separate from the default adb server of other tools.
  info.ArgumentList.Add("-P"); info.ArgumentList.Add("5038");
  foreach(var arg in args) info.ArgumentList.Add(arg);
  using var p=Process.Start(info)!;
  using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
  var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
  try { await p.WaitForExitAsync(ct.Token); } catch { p.Kill(true); throw; }
  if(p.ExitCode!=0) throw new IOException("Не удалось подключить USB. Проверьте кабель и разрешение отладки. " + await stderr);
  return await stdout;
 }
 internal static async Task<List<Phone>> Network() {
  // RFC 6762 §6.7: a one-shot query from an ephemeral port gets a unicast reply.
  // No dependency on owning port 5353 (often already held by Windows or Bonjour).
  using var udp=new UdpClient(new IPEndPoint(IPAddress.Any,0));
  var destination=new IPEndPoint(IPAddress.Parse("224.0.0.251"),5353);
  await udp.SendAsync(Query("_lenscast._tcp.local",12),destination);
  using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(4));
  var names=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  var hosts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  var queried=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  try {
   while(true) {
    var response=await udp.ReceiveAsync(ct.Token);
    if(response.RemoteEndPoint.Port!=5353) continue;
    Parse(response.Buffer,names,hosts);
    foreach(var service in names.ToList()) {
     if(string.IsNullOrEmpty(service.Value)) {
      if(queried.Add("SRV:"+service.Key)) await udp.SendAsync(Query(service.Key,33),destination,ct.Token);
     } else if(!hosts.ContainsKey(service.Value) && queried.Add("A:"+service.Value)) {
      await udp.SendAsync(Query(service.Value,1),destination,ct.Token);
     }
    }
   }
  } catch(OperationCanceledException) {}
  return names.Where(x=>hosts.ContainsKey(x.Value)).Select(x=>new Phone(x.Key.Replace("._lenscast._tcp.local","",StringComparison.OrdinalIgnoreCase),hosts[x.Value],false)).DistinctBy(p=>p.Address).ToList();
 }
 internal static byte[] Query(string name, ushort type) {
  var query=new List<byte>(new byte[12]); query[5]=1;
  foreach(var label in name.Split('.')) {
   var bytes=Encoding.UTF8.GetBytes(label);
   if(bytes.Length is <1 or >63) throw new InvalidDataException("Недопустимое имя в сети");
   query.Add((byte)bytes.Length); query.AddRange(bytes);
  }
  query.AddRange(new byte[]{0,(byte)(type>>8),(byte)type,0,1});
  return query.ToArray();
 }
 private static string Name(byte[] b,ref int offset) {
  var result=new List<string>(); var pos=offset; int end=-1;
  for(int steps=0;steps<128;steps++) {
   if(pos>=b.Length) throw new InvalidDataException(); int n=b[pos++];
   if(n==0) { offset=end<0?pos:end; return string.Join('.',result); }
   if((n&192)==192) { if(pos>=b.Length) throw new InvalidDataException(); if(end<0) end=pos+1; pos=((n&63)<<8)|b[pos]; continue; }
   if(n>63 || pos+n>b.Length) throw new InvalidDataException();
   result.Add(Encoding.UTF8.GetString(b,pos,n)); pos+=n;
  }
  throw new InvalidDataException();
 }
 private static ushort U16(byte[] b,int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p,2));
 internal static void Parse(byte[] b,Dictionary<string,string> names,Dictionary<string,string> hosts) {
  try {
   if(b.Length<12 || (b[2]&128)==0) return;
   int pos=12, questions=U16(b,4), count=U16(b,6)+U16(b,8)+U16(b,10);
   for(int i=0;i<questions;i++) { Name(b,ref pos); pos+=4; }
   for(int i=0;i<count;i++) {
    var name=Name(b,ref pos); int type=U16(b,pos), len=U16(b,pos+8); pos+=10;
    if(pos+len>b.Length) return;
    if(type==12 && name.Equals("_lenscast._tcp.local",StringComparison.OrdinalIgnoreCase)) {
     int p=pos; var target=Name(b,ref p);
     if(target.EndsWith("._lenscast._tcp.local",StringComparison.OrdinalIgnoreCase)) names.TryAdd(target,"");
    }
    if(type==33 && name.EndsWith("._lenscast._tcp.local",StringComparison.OrdinalIgnoreCase) && len>=7 && U16(b,pos+4)==27183) { int p=pos+6; names[name]=Name(b,ref p); }
    if(type==1 && len==4) hosts[name]=new IPAddress(b.AsSpan(pos,4)).ToString();
    pos+=len;
   }
  } catch(Exception e) when(e is ArgumentException or InvalidDataException or IndexOutOfRangeException) { /* ignore malformed/unrelated DNS datagrams */ }
 }
}
