using System.Buffers.Binary;
using Lenscast;
internal static class ScreenRegressionTests {
 static void Check(bool value,string message) { if(!value) throw new Exception(message); Console.WriteLine("Пройдено: "+message); }
 internal static async Task Run() {
  var raw=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"red.h264"));
  foreach(var (file,w,h) in new[]{("red.h264",64,64),("red-crop.h264",66,50)})
  foreach(bool software in new[]{true,false}) {
   var encoded=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,file));
   int rendered=0;
   using var decoder=new VideoDecoder(w,h,software);
   for(int i=0;i<4;i++) decoder.Feed(encoded,1_000_000+i*33333,frame=> {
    using(frame) {
     Check(frame.Width==w && frame.Height==h,"MFT: реальные размеры декодированного кадра");
     int pixel=((h/2)*w+w/2)*4;
     Check(frame.Pixels[pixel+2]>200 && frame.Pixels[pixel]<70 && frame.Pixels[pixel+1]<70 && frame.Pixels[pixel+3]==255,"MFT: H.264 → NV12 → красный BGRA");
     rendered++;
    }
   });
   Check(rendered>0,software?"Программный декодировщик Microsoft работает":"Автоматический выбор DXVA/программного декодировщика работает");
  }
  byte[] header=new byte[24]; BinaryPrimitives.WriteUInt32BigEndian(header,0x4c435631); header[4]=1; header[5]=2; header[6]=1;
  BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8),123); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),raw.Length);
  var packet=await VideoPacket.Read(new MemoryStream(header.Concat(raw).ToArray()),CancellationToken.None);
  Check(packet.Type==2 && packet.PhoneUs==123 && packet.Data.SequenceEqual(raw),"Кадрирование видеоканала");
  foreach(int size in new[]{0,-1,16777217,int.MaxValue}) {
   BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),size);
   try { await VideoPacket.Read(new MemoryStream(header),CancellationToken.None); throw new Exception("Принят недопустимый видеопакет"); } catch(InvalidDataException) {}
  }
  Check(true,"Недопустимые длины видеопакетов отклонены до выделения памяти");
  var format=new byte[12]; BinaryPrimitives.WriteInt32BigEndian(format,1080); BinaryPrimitives.WriteInt32BigEndian(format.AsSpan(4),1920); format[11]=1;
  Check(new VideoPacket(0,0,format).Format().Height==1920,"Параметры портретного потока");
  BinaryPrimitives.WriteInt32BigEndian(format,int.MaxValue);
  try { new VideoPacket(0,0,format).Format(); throw new Exception("Принят слишком большой кадр"); } catch(InvalidDataException) {}
 }
}
