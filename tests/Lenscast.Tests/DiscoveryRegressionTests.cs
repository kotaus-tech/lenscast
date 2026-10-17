using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Lenscast.Tests;

internal static class DiscoveryRegressionTests {
 private static void Check(bool ok,string text) { if(!ok) throw new Exception(text); Console.WriteLine("Пройдено: "+text); }
 internal static async Task Run() {
  var devices=UsbDiscovery.ParseDevices("* daemon started successfully\r\nList of devices attached\r\nSERIAL123    device product:x model:POCO_X3_NFC device:surya transport_id:1\r\nemulator-5554 device model:Emulator\r\n192.0.2.1:5555 device model:Wireless\r\n");
  Check(devices.Count==1 && devices[0].Name=="POCO X3 NFC","USB: пробелы, CRLF, model и исключение беспроводных/emulator соединений");
  var commands=new List<string[]>();
  Task<string> Ready(string[] args,CancellationToken ct) { commands.Add(args); return Task.FromResult("List of devices attached\nSERIAL123\tdevice model:OPPO_Reno_15\n"); }
  var phones=await UsbDiscovery.Search(Ready,CancellationToken.None);
  Check(phones.Count==1 && phones[0].UsbSerial=="SERIAL123" && commands.Count==1 && commands[0].SequenceEqual(new[]{"devices","-l"}),"USB: карточка появляется без shell/getprop и forward");
  await UsbDiscovery.Forward(phones[0].UsbSerial!,Ready,CancellationToken.None);
  Check(commands[^1].SequenceEqual(new[]{"-s","SERIAL123","forward","tcp:27185","tcp:27185"}),"USB: forward выполняется отдельно при подключении");
  foreach(var (state,expected) in new[]{("unauthorized","подтвердите"),("offline","Переподключите"),("","драйвер")}) {
   int attempts=0;
   Task<string> State(string[] args,CancellationToken ct) { attempts++; return Task.FromResult("List of devices attached\n"+(state.Length>0?$"SERIAL123\t{state}\n":"")); }
   try { await UsbDiscovery.Search(State,CancellationToken.None); throw new Exception("Неподготовленное устройство принято"); }
   catch(UsbDiscoveryException e) { Check(e.Message.Contains(expected),"USB: понятное действие для состояния "+(state.Length>0?state:"нет устройств")); }
   if(state is "" or "offline") Check(attempts==3,"USB: повторное перечисление после старта сервера");
  }
  Check(Discovery.Query("_lenscast._tcp.local",12,true)[^2]==128,"mDNS: бит unicast-response в стандартном запросе");
  await UdpExchange();
 }
 private static byte[] DnsName(string value) {
  var bytes=new List<byte>();
  foreach(var label in value.Split('.')) { var part=Encoding.UTF8.GetBytes(label); bytes.Add((byte)part.Length); bytes.AddRange(part); }
  bytes.Add(0); return bytes.ToArray();
 }
 private static byte[] Reply(string owner,ushort type,byte[] data) {
  var packet=new List<byte>(new byte[12]); packet[2]=0x84; packet[7]=1;
  packet.AddRange(DnsName(owner)); packet.AddRange(new byte[]{(byte)(type>>8),(byte)type,0,1,0,0,0,10,(byte)(data.Length>>8),(byte)data.Length}); packet.AddRange(data); return packet.ToArray();
 }
 private static async Task UdpExchange() {
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
  using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
  int ptr=0,srv=0,a=0;
  var responder=Task.Run(async()=>{
   try {
    while(true) {
     var query=await server.ReceiveAsync(timeout.Token);
     var type=BinaryPrimitives.ReadUInt16BigEndian(query.Buffer.AsSpan(query.Buffer.Length-4,2));
     byte[] reply;
     switch(type) {
      case 12:
       if(++ptr==1) continue; // First query is lost, as on a busy Wi-Fi network.
       reply=Reply("_lenscast._tcp.local",12,DnsName("Проверка._lenscast._tcp.local")); break;
      case 33:
       srv++; var data=new byte[6]; BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4),27183);
       reply=Reply("Проверка._lenscast._tcp.local",33,data.Concat(DnsName("android.local")).ToArray()); break;
      case 1: a++; reply=Reply("android.local",1,new byte[]{192,0,2,9}); break;
      default: continue;
     }
     await server.SendAsync(reply,query.RemoteEndPoint,timeout.Token);
    }
   } catch(OperationCanceledException) { }
  });
  List<Phone> found;
  try { found=await MdnsDiscovery.Browse(IPAddress.Loopback,(IPEndPoint)server.Client.LocalEndPoint!,false,TimeSpan.FromMilliseconds(2600),timeout.Token); }
  finally { timeout.Cancel(); await responder; }
  Check(found.Count==1 && found[0].Address=="192.0.2.9" && ptr>=2 && srv>0 && a>0,"mDNS: реальный UDP, потерянный запрос, раздельные PTR/SRV/A и выбранный интерфейс");
 }
}
