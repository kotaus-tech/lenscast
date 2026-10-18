using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
namespace Lenscast;

internal sealed record VideoPacket(byte Type,long PhoneUs,byte[] Data,byte Codec=1) {
 internal static async Task<VideoPacket> Read(Stream stream,CancellationToken ct) {
  var header=new byte[24]; await stream.ReadExactlyAsync(header,ct);
  int size=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16));
  if(BinaryPrimitives.ReadUInt32BigEndian(header)!=0x4c435631 || header[4]!=1 || header[5]>2 || header[6] is not (1 or 2) || header[7]!=0 || size is <1 or >16777216) throw new InvalidDataException("Недопустимый видеопакет");
  long time=BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
  if(time<0) throw new InvalidDataException("Недопустимая метка кадра");
  var bytes=new byte[size]; await stream.ReadExactlyAsync(bytes,ct);
  return new(header[5],time,bytes,header[6]);
 }
 internal (int Width,int Height,byte[] Csd) Format() {
  if(Type!=0 || Data.Length<12) throw new InvalidDataException("Нет параметров потока");
  int w=BinaryPrimitives.ReadInt32BigEndian(Data),h=BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(4));
  if(w is <16 or >4096 || h is <16 or >4096 || (w&1)!=0 || (h&1)!=0) throw new InvalidDataException("Недопустимые размеры потока");
  return(w,h,Data[8..]);
 }
}

internal sealed class VideoReceiver:IDisposable {
 private readonly CancellationTokenSource stop=new();
 private readonly BlockingCollection<VideoPacket> packets=new(3);
 private readonly Func<object,Task> send;
 private readonly Action<VideoFrame> frame;
 private readonly Action<string> status;
 private TcpClient? client;
 private VideoPacket? config;
 private bool waitingKey=true;
 private bool software;
 private long lastRequest;
 internal VideoReceiver(string address,bool usb,string fingerprint,string ticket,Func<object,Task> command,Action<VideoFrame> render,Action<string> notice) {
  send=command; frame=render; status=notice;
  _=Task.Factory.StartNew(Decode,TaskCreationOptions.LongRunning);
  _=Network(address,usb,fingerprint,ticket);
 }
 private async Task Network(string address,bool usb,string fingerprint,string ticket) {
  var ct=stop.Token;
  try {
   while(!ct.IsCancellationRequested) {
    try {
     using var tcp=new TcpClient { NoDelay=true,ReceiveBufferSize=64*1024 }; client=tcp;
     await tcp.ConnectAsync(address,usb?27186:27184,ct);
     using var net=tcp.GetStream(); Stream input=net;
     if(!usb) {
      var tls=new SslStream(net,false,(_,cert,_,_)=>cert!=null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))==fingerprint);
      try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost="Lenscast",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13 },ct); input=tls; }
      catch { tls.Dispose(); throw; }
     }
     using(input) {
      var hello=JsonSerializer.SerializeToUtf8Bytes(new {version=1,ticket}); var prefix=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix,hello.Length);
      await input.WriteAsync(prefix,ct); await input.WriteAsync(hello,ct);
      config=null; waitingKey=true;
      while(!ct.IsCancellationRequested) {
       using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
       var packet=await VideoPacket.Read(input,deadline.Token);
       if(packet.Type==0) { packet.Format(); config=packet; Clear(); waitingKey=true; packets.TryAdd(packet); }
       else if(!waitingKey || packet.Type==2) {
        if(!packets.TryAdd(packet)) {
         Clear(); waitingKey=true;
         if(config is not null) packets.TryAdd(config);
         if(packet.Type==2) { packets.TryAdd(packet); waitingKey=false; } else RequestKey();
        } else if(packet.Type==2) waitingKey=false;
       }
      }
     }
    } catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
    catch(Exception e) { Log.Write("Видеоканал прерван",e); status("Видеоканал восстанавливается. Если экран не разрешён — нажмите «Разрешить показ экрана» на телефоне."); }
    finally { client=null; }
    await Task.Delay(500,ct);
   }
  } catch(OperationCanceledException) { }
  finally { packets.CompleteAdding(); }
 }
 private void SelectCodec(int codec) {
  _=Select();
  async Task Select() { try { await send(new {type="videoCodec",codec}); } catch(Exception e) { if(!stop.IsCancellationRequested) Log.Write("Выбор видеокодека не доставлен",e); } }
 }
 private void Clear() { while(packets.TryTake(out _)) {} }
 private void RequestKey() {
  var now=Environment.TickCount64;
  if(now-Interlocked.Read(ref lastRequest)<500) return;
  Interlocked.Exchange(ref lastRequest,now);
  _=Request();
  async Task Request() { try { await send(new {type="keyframe"}); } catch(Exception e) { if(!stop.IsCancellationRequested) Log.Write("Ключевой кадр не запрошен",e); } }
 }
 private void Decode() {
  VideoDecoder? decoder=null;
  byte[]? csd=null;
  int width=0,height=0,codec=1;
  bool keyNeeded=true;
  try {
   bool hevc=false;
   try { using var probe=new VideoDecoder(64,64,true,2); hevc=true; } catch(Exception e) { Log.Write("HEVC не выбран: используем обязательный H.264",e); }
   SelectCodec(hevc?2:1);
   foreach(var packet in packets.GetConsumingEnumerable(stop.Token)) {
    try {
     if(packet.Type==0) {
      (width,height,csd)=packet.Format(); codec=packet.Codec; decoder?.Dispose(); decoder=null; keyNeeded=true;
      continue;
     }
     if(csd is null || (keyNeeded && packet.Type!=2)) continue;
     if(decoder is null) {
      try { decoder=new VideoDecoder(width,height,software,codec); }
      catch(Exception e) when(!software) { software=true; Log.Write("Переход на программный декодировщик",e); decoder=new VideoDecoder(width,height,true,codec); }
     }
     var bytes=packet.Data;
     if(keyNeeded) { bytes=new byte[csd.Length+packet.Data.Length]; csd.CopyTo(bytes,0); packet.Data.CopyTo(bytes,csd.Length); }
     decoder.Feed(bytes,packet.PhoneUs,f=> { if(stop.IsCancellationRequested) f.Dispose(); else frame(f); }); keyNeeded=false;
    } catch(Exception e) {
     Log.Write("Ошибка декодирования видео",e);
     if(decoder?.UsingDevice==true) { software=true; status("Аппаратный декодировщик недоступен · используем процессор"); }
     else if(codec==2) { SelectCodec(1); csd=null; software=false; status("HEVC недоступен · возвращаемся к H.264"); }
     else status("Не удалось декодировать экран. Перезапустите показ экрана на телефоне.");
     decoder?.Dispose(); decoder=null; keyNeeded=true; RequestKey();
    }
   }
  } catch(OperationCanceledException) { }
  finally { decoder?.Dispose(); }
 }
 public void Dispose() { if(stop.IsCancellationRequested) return; stop.Cancel(); client?.Dispose(); }
}
