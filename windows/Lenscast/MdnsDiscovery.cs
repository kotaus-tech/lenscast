using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace Lenscast;

internal static class MdnsDiscovery {
 private static readonly IPAddress Group=IPAddress.Parse("224.0.0.251");
 internal static async Task<List<Phone>> Search(CancellationToken ct) {
  var interfaces=NetworkInterface.GetAllNetworkInterfaces()
   .Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
   .SelectMany(n=>n.GetIPProperties().UnicastAddresses
    .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
    .Select(a=>(n.Name,Address:a.Address)))
   .DistinctBy(i=>i.Address).ToArray();
  Log.Write("Wi-Fi: интерфейсы ПК: "+string.Join("; ",interfaces.Select(i=>$"{i.Name} {i.Address}")));
  var scans=interfaces.Select(i=>Browse(i.Address,new IPEndPoint(Group,5353),true,TimeSpan.FromSeconds(5),ct));
  var phones=(await Task.WhenAll(scans)).SelectMany(p=>p).DistinctBy(p=>p.Address).ToList();
  Log.Write($"Wi-Fi: поиск завершён, найдено телефонов {phones.Count}");
  return phones;
 }
 // Endpoint/timeout injection allows an actual UDP exchange in CI without a phone.
 internal static async Task<List<Phone>> Browse(IPAddress local,IPEndPoint destination,bool multicast,TimeSpan duration,CancellationToken ct) {
  using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(duration);
  var token=stop.Token;
  var names=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  var hosts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  UdpClient? shared=null;
  int packets=0;
  var queried=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  try {
   using var legacy=new UdpClient(new IPEndPoint(local,0));
   if(multicast) {
    legacy.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastInterface,local.GetAddressBytes());
    legacy.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastTimeToLive,255);
    try {
     shared=new UdpClient(AddressFamily.InterNetwork);
     shared.Client.ExclusiveAddressUse=false;
     shared.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
     shared.Client.Bind(new IPEndPoint(IPAddress.Any,5353));
     shared.JoinMulticastGroup(Group,local);
     shared.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastInterface,local.GetAddressBytes());
     shared.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastTimeToLive,255);
    } catch(SocketException e) { shared?.Dispose(); shared=null; Log.Write($"Wi-Fi {local}: порт mDNS занят/недоступен, используем unicast",e); }
   }
   Log.Write($"Wi-Fi {local}: запросы через выбранный интерфейс; multicast-приём {shared is not null}");
   async Task Query(string name,ushort type) {
    await legacy.SendAsync(Discovery.Query(name,type),destination,token);
    if(shared is not null) await shared.SendAsync(Discovery.Query(name,type,true),destination,token);
   }
   async Task RequestMissing() {
    foreach(var service in names.Take(64).ToList()) {
     if(string.IsNullOrEmpty(service.Value)) { if(queried.Add("SRV:"+service.Key)) await Query(service.Key,33); }
     else if(!hosts.ContainsKey(service.Value) && queried.Add("A:"+service.Value)) await Query(service.Value,1);
    }
   }
   await Query("_lenscast._tcp.local",12);
   Task<UdpReceiveResult> legacyRead=legacy.ReceiveAsync(token).AsTask();
   Task<UdpReceiveResult>? sharedRead=shared?.ReceiveAsync(token).AsTask();
   Task retry=Task.Delay(1000,token);
   while(!token.IsCancellationRequested) {
    var pending=new List<Task>{legacyRead,retry}; if(sharedRead is not null) pending.Add(sharedRead);
    var ready=await Task.WhenAny(pending);
    if(ready==retry) {
     await retry; queried.Clear(); await Query("_lenscast._tcp.local",12); await RequestMissing(); retry=Task.Delay(1000,token); continue;
    }
    var reply=await (ready==legacyRead ? legacyRead : sharedRead!);
    if(ready==legacyRead) legacyRead=legacy.ReceiveAsync(token).AsTask();
    else sharedRead=shared!.ReceiveAsync(token).AsTask();
    if(reply.RemoteEndPoint.Port!=destination.Port || reply.Buffer.Length<12 || (reply.Buffer[2]&128)==0) continue;
    packets++;
    Discovery.Parse(reply.Buffer,names,hosts);
    await RequestMissing();
   }
  } catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
  catch(SocketException e) { Log.Write($"Wi-Fi {local}: ошибка UDP",e); }
  finally { shared?.Dispose(); }
  ct.ThrowIfCancellationRequested();
  Log.Write($"Wi-Fi {local}: ответов {packets}, служб {names.Count}, адресов {hosts.Count}");
  return names.Where(x=>!string.IsNullOrEmpty(x.Value) && hosts.ContainsKey(x.Value))
   .Select(x=>new Phone(x.Key[..^"._lenscast._tcp.local".Length],hosts[x.Value],false)).DistinctBy(p=>p.Address).ToList();
 }
}
