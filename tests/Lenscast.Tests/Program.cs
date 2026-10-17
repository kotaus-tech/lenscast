using System.Buffers.Binary;
using System.Text;
using Lenscast;
static void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("Пройдено: " + name); }
var payload=Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"sent\":123}");
var bytes=new byte[4+payload.Length]; BinaryPrimitives.WriteInt32BigEndian(bytes,payload.Length); payload.CopyTo(bytes,4);
var message=await Connection.Read(new MemoryStream(bytes),CancellationToken.None);
Check(message.GetProperty("sent").GetInt32()==123,"Чтение сообщения управления");
foreach(var length in new[]{-1,0,1,65537,int.MaxValue}) {
 var header=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header,length);
 try { await Connection.Read(new MemoryStream(header),CancellationToken.None); throw new Exception("Длина принята"); } catch(InvalidDataException) {}
}
Check(true,"Недопустимые длины отклонены");
try { await Connection.Read(new MemoryStream(bytes[..^1]),CancellationToken.None); throw new Exception("Обрыв принят"); } catch(EndOfStreamException) {}
Check(true,"Частичное сообщение не принимается");
var names=new Dictionary<string,string>(); var hosts=new Dictionary<string,string>();
for(int n=0;n<1000;n++) { var malformed=new byte[Random.Shared.Next(0,300)]; Random.Shared.NextBytes(malformed); Discovery.Parse(malformed,names,hosts); }
Check(true,"Некорректные DNS-пакеты не приводят к сбою");
TrustStore.Save(new("192.0.2.1","TEST","secret"));
Check(TrustStore.Load("192.0.2.1")?.Token=="secret","Хранилище Windows DPAPI");
TrustStore.Forget(); Check(TrustStore.Load()==null,"Удаление доверия");
