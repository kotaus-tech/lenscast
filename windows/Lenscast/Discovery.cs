using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;
namespace Lenscast;
internal record Phone(string Name, string Address, bool Usb, string? UsbSerial = null) {
 public string Label => $"{Name} · {(Usb ? "USB" : "Wi-Fi")} · готов к подключению";
}
internal sealed class UsbDiscoveryException(string message) : Exception(message) { }
internal static class Discovery {
 internal static Task<List<Phone>> Usb(CancellationToken ct = default) => UsbDiscovery.Search(ct);
 internal static Task<List<Phone>> Network(CancellationToken ct = default) => MdnsDiscovery.Search(ct);
 internal static byte[] Query(string name, ushort type, bool unicastResponse = false) {
  var query=new List<byte>(new byte[12]); query[5]=1;
  foreach(var label in name.Split('.')) {
   var bytes=Encoding.UTF8.GetBytes(label);
   if(bytes.Length is <1 or >63) throw new InvalidDataException("Недопустимое имя в сети");
   query.Add((byte)bytes.Length); query.AddRange(bytes);
  }
  query.AddRange(new byte[]{0,(byte)(type>>8),(byte)type,(byte)(unicastResponse ? 128 : 0),1});
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
